using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace SoundLibert.Audio;

/// <summary>
/// The one chunked downloader behind streaming and offline copies. googlevideo
/// throttles bulk GETs (200 OK + a few KB, then a stall), so this appends
/// <c>ratebypass=yes</c> and fetches 1 MB Range chunks, each new request
/// resetting the throttle burst. Network trouble is retried on a fresh
/// connection; HTTP errors that a retry cannot fix (403, 404…) fail at once.
/// </summary>
public static class ChunkedHttpDownloader
{
    private const long ChunkSize = 1024 * 1024;
    private const int MaxChunkRetries = 3;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);

    // Phase timeouts live here (headers + each read), so the client-wide one stays off.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Content type and total size of a stream ("" and 0 when the probe fails).</summary>
    public static async Task<(string MediaType, long Total)> ProbeAsync(
        string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, WithRateBypass(url));
            ApplyHeaders(request, headers);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await SendForHeadersAsync(request, ct);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            return (mediaType, ParseTotalLength(response) ?? 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Download] probe failed, continuing without a size: {ex.Message}");
            return (string.Empty, 0);
        }
    }

    /// <summary>
    /// Downloads the whole stream into <paramref name="output"/>. After every
    /// chunk the bytes are flushed BEFORE <paramref name="onProgress"/> runs, so
    /// a concurrent reader of the file never sees a partly written tail.
    /// Throws on failure; a file shorter than the advertised size is a failure.
    /// </summary>
    /// <param name="onProgress">Called with (bytes so far, total size or 0 when unknown).</param>
    public static async Task DownloadAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        FileStream output,
        long knownTotal,
        Action<long, long>? onProgress,
        CancellationToken ct)
    {
        url = WithRateBypass(url);
        long downloaded = 0;
        var total = knownTotal;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = await DownloadChunkWithRetriesAsync(url, headers, output, downloaded, ct);
            downloaded += chunk.BytesRead;
            if (total <= 0 && chunk.TotalLength is > 0) total = chunk.TotalLength.Value;

            await output.FlushAsync(ct);
            onProgress?.Invoke(downloaded, total);

            if (chunk.Code == (int)HttpStatusCode.RequestedRangeNotSatisfiable) break; // past EOF
            if (chunk.Code == (int)HttpStatusCode.OK) break;                           // server sent it all
            if (total > 0 && downloaded >= total) break;
            if (chunk.BytesRead == 0) break;
            // A short chunk only ends the file when the size is unknown; with a
            // known size it is a dropped connection and the next loop continues.
            if (total <= 0 && chunk.BytesRead < ChunkSize) break;
        }

        if (downloaded == 0) throw new InvalidDataException("The download produced no data.");
        if (total > 0 && downloaded < total)
            throw new IOException($"The download ended early: {downloaded} of {total} bytes.");
    }

    public static string WithRateBypass(string url)
        => url.Contains("ratebypass=", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + (url.Contains('?') ? "&" : "?") + "ratebypass=yes";

    private static async Task<(int Code, long BytesRead, long? TotalLength)> DownloadChunkWithRetriesAsync(
        string url, IReadOnlyDictionary<string, string> headers, FileStream output, long from, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await DownloadRangeOnceAsync(url, headers, from, from + ChunkSize - 1, output, ct);
            }
            catch (Exception ex) when (attempt < MaxChunkRetries && !ct.IsCancellationRequested && IsTransient(ex))
            {
                Trace.WriteLine($"[Download] chunk at {from} failed ({ex.Message}), retry {attempt + 1}/{MaxChunkRetries}");
                output.SetLength(from);
                output.Position = from;
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), ct);
            }
        }
    }

    private static async Task<(int Code, long BytesRead, long? TotalLength)> DownloadRangeOnceAsync(
        string url, IReadOnlyDictionary<string, string> headers, long from, long to, FileStream output, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, headers);
        request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await SendForHeadersAsync(request, ct);

        var code = (int)response.StatusCode;
        var total = ParseTotalLength(response);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return (code, 0, total);
        if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"Chunk request failed: {code} {response.StatusCode}", null, response.StatusCode);
        // A full body for a later range would be written at the wrong offset.
        if (response.StatusCode == HttpStatusCode.OK && from > 0)
            throw new HttpRequestException("The server ignored the Range header.", null, response.StatusCode);

        await using var content = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920];
        long read = 0;
        while (true)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallTimeout);
            int n;
            try
            {
                n = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), stall.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No data for {StallTimeout.TotalSeconds:0} s at byte {from + read}.");
            }
            if (n == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
        }
        return (code, read, total);
    }

    private static async Task<HttpResponseMessage> SendForHeadersAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No response within {StallTimeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>Worth another try: a stall or a dropped connection, or a server hiccup.</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        TimeoutException or IOException => true,
        HttpRequestException http => http.StatusCode is null
                                     or HttpStatusCode.TooManyRequests
                                     or >= HttpStatusCode.InternalServerError,
        _ => false
    };

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var header in headers)
        {
            if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                request.Headers.UserAgent.ParseAdd(header.Value);
            else
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static long? ParseTotalLength(HttpResponseMessage response)
        => response.Content.Headers.ContentRange?.Length
           ?? response.Content.Headers.ContentLength;
}
