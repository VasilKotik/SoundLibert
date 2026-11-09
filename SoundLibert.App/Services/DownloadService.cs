using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using SoundLibert.Audio;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;

namespace SoundLibert.App.Services;

/// <summary>
/// Downloads catalogue songs for offline listening. A single background worker
/// drains a queue (one download at a time), resolves each song's audio stream,
/// saves it under <c>%LocalAppData%\SoundLibert\downloads</c> and records the
/// path in the database. The downloaded file is played straight from disk.
/// </summary>
internal sealed class DownloadService : IDownloadService
{
    private readonly IMusicProvider _provider;
    private readonly IMusicDatabase _database;
    private readonly IAppSettings _settings;
    private readonly string _defaultDir;

    private readonly Channel<SongItem> _queue = Channel.CreateUnbounded<SongItem>();
    private readonly ConcurrentDictionary<string, DownloadState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _paths = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _progress = new(StringComparer.Ordinal);
    // Ids queued or in progress, and how to stop the one in progress.
    private readonly ConcurrentDictionary<string, CancellationTokenSource?> _active = new(StringComparer.Ordinal);
    private int _initialized;

    public DownloadService(IMusicProvider provider, IMusicDatabase database, IAppSettings settings)
    {
        _provider = provider;
        _database = database;
        _settings = settings;
        _defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundLibert", "downloads");
    }

    /// <summary>Configured folder (falls back to the default app-data folder).</summary>
    public string FolderPath =>
        string.IsNullOrWhiteSpace(_settings.DownloadFolder) ? _defaultDir : _settings.DownloadFolder;

    public async Task<(int Count, long Bytes)> GetStatsAsync(CancellationToken ct = default)
    {
        var entities = await GetDownloadedAsync(ct);
        long bytes = 0;
        var count = 0;
        foreach (var song in entities)
        {
            var path = song.DownloadPath;
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) { bytes += info.Length; count++; }
            }
            catch { /* skip unreadable */ }
        }
        return (count, bytes);
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        foreach (var id in _active.Keys.ToArray())
        {
            CancelActive(id);
            _states[id] = DownloadState.None;
            _progress.TryRemove(id, out _);
            Raise(id, DownloadState.None, -1, null);
        }

        var entities = await GetDownloadedAsync(ct);
        foreach (var song in entities)
        {
            if (string.IsNullOrWhiteSpace(song.Id)) continue;
            await RemoveAsync(song.Id, ct);
        }
    }

    /// <summary>
    /// Moves every existing offline copy into the currently configured folder so
    /// changing the setting is not "only for new downloads". Files already in
    /// the folder are skipped; a failed move leaves that copy where it was.
    /// </summary>
    public async Task MoveExistingAsync(CancellationToken ct = default)
    {
        string target;
        try
        {
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(FolderPath));
            Directory.CreateDirectory(target);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Download] folder is not usable: {ex.Message}");
            return;
        }

        foreach (var (songId, oldPath) in _paths.ToArray())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(oldPath) || !File.Exists(oldPath)) continue;
                var oldDir = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(Path.GetDirectoryName(oldPath) ?? ""));
                if (string.Equals(oldDir, target, StringComparison.OrdinalIgnoreCase)) continue;

                var newPath = Path.Combine(target, Path.GetFileName(oldPath));
                // Moving across drives copies the whole file: not on the UI thread.
                await Task.Run(() => File.Move(oldPath, newPath, overwrite: true), ct);

                _paths[songId] = newPath;
                try { await _database.SetDownloadAsync(songId, newPath, DateTime.UtcNow, ct); }
                catch (Exception ex) { Trace.WriteLine($"[Download] move record failed: {ex.Message}"); }
                Raise(songId, DownloadState.Downloaded, 1, newPath);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Download] move failed for {songId}: {ex.Message}");
            }
        }
    }

    public event EventHandler<DownloadChangedEventArgs>? Changed;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1) return;
        // The worker runs whatever happens below: a bad folder setting or a
        // database hiccup must not leave later downloads queued forever.
        _ = Task.Run(ProcessQueueAsync);
        try
        {
            Directory.CreateDirectory(FolderPath);
            var entities = await _database.GetDownloadedSongsAsync(ct);
            foreach (var e in entities)
                if (!string.IsNullOrWhiteSpace(e.DownloadPath) && File.Exists(e.DownloadPath))
                {
                    _paths[e.Id] = e.DownloadPath!;
                    _states[e.Id] = DownloadState.Downloaded;
                }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Download] init failed: {ex.Message}");
        }
    }

    public bool IsDownloaded(string songId)
        => _paths.TryGetValue(songId, out var p) && File.Exists(p);

    public bool IsBusy(string songId)
        => _active.ContainsKey(songId);

    public DownloadState GetState(string songId)
        => _states.TryGetValue(songId, out var s) ? s
            : IsDownloaded(songId) ? DownloadState.Downloaded : DownloadState.None;

    public double GetProgress(string songId)
        => _progress.TryGetValue(songId, out var p) ? p : -1;

    public bool TryGetPath(string songId, out string? path)
    {
        if (_paths.TryGetValue(songId, out var p) && File.Exists(p)) { path = p; return true; }
        path = null;
        return false;
    }

    public async Task DownloadAsync(SongItem song, CancellationToken ct = default)
    {
        if (song is null || string.IsNullOrWhiteSpace(song.Id)) return;
        if (song.IsLocal) return;                 // already a local file
        if (IsDownloaded(song.Id)) return;
        if (!_active.TryAdd(song.Id, null)) return;

        _states[song.Id] = DownloadState.Queued;
        _progress[song.Id] = -1;
        Raise(song.Id, DownloadState.Queued, -1, null);
        await _queue.Writer.WriteAsync(song, ct);
    }

    public async Task RemoveAsync(string songId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(songId)) return;

        // A copy still on its way must not reappear after being removed.
        CancelActive(songId);

        if (_paths.TryRemove(songId, out var path))
            DeleteOrRetryLater(path);
        try { await _database.ClearDownloadAsync(songId, ct); }
        catch (Exception ex) { Trace.WriteLine($"[Download] clear failed: {ex.Message}"); }

        _states[songId] = DownloadState.None;
        _progress.TryRemove(songId, out _);
        Raise(songId, DownloadState.None, -1, null);
    }

    /// <summary>Stops a queued or running download (the worker cleans up after it).</summary>
    private void CancelActive(string songId)
    {
        if (_active.TryRemove(songId, out var cts))
        {
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// A song playing from its offline copy keeps the file locked, so a failed
    /// delete is retried in the background until the file is free.
    /// </summary>
    private static void DeleteOrRetryLater(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Download] delete deferred: {ex.Message}");
        }

        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch { /* still in use */ }
            }
            Trace.WriteLine("[Download] gave up deleting a removed offline copy");
        });
    }

    public async Task<IReadOnlyList<SongItem>> GetDownloadedAsync(CancellationToken ct = default)
    {
        try
        {
            var entities = await _database.GetDownloadedSongsAsync(ct);
            return entities.Select(e => e.ToSongItem()).ToList();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Download] list failed: {ex.Message}");
            return Array.Empty<SongItem>();
        }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var song in _queue.Reader.ReadAllAsync())
        {
            using var cts = new CancellationTokenSource();
            // Removed while it waited in the queue.
            if (!_active.TryUpdate(song.Id, cts, null)) continue;
            try
            {
                await DownloadOneAsync(song, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                Debug.WriteLine($"[Download] cancelled: {song.Id}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Download] \"{song.Title}\" failed: {ex.Message}");
                _states[song.Id] = DownloadState.Failed;
                Raise(song.Id, DownloadState.Failed, -1, null);
            }
            finally
            {
                _active.TryRemove(new KeyValuePair<string, CancellationTokenSource?>(song.Id, cts));
            }
        }
    }

    private async Task DownloadOneAsync(SongItem song, CancellationToken ct)
    {
        _states[song.Id] = DownloadState.Downloading;
        _progress[song.Id] = 0;
        Raise(song.Id, DownloadState.Downloading, 0, null);

        // The recording Song mode plays: a row can be a clip paired with its song.
        var playbackId = string.IsNullOrWhiteSpace(song.SongVersionId) ? song.Id : song.SongVersionId!;
        var stream = await _provider.GetStreamInfoAsync(playbackId, null, _settings.AudioQuality, ct);
        if (stream is null || string.IsNullOrWhiteSpace(stream.Url))
            throw new InvalidOperationException("Could not resolve a stream");

        var headers = _provider.GetStreamHeaders(stream.Url, stream.ClientName);
        Directory.CreateDirectory(FolderPath);

        var finalPath = Path.Combine(FolderPath, Sanitize(song.Id) + ExtensionFor(stream.MimeType));
        var tempPath = finalPath + ".part";
        var completed = false;
        try
        {
            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await ChunkedHttpDownloader.DownloadAsync(stream.Url, headers, file, 0, (downloaded, total) =>
                {
                    if (total <= 0) return;
                    var progress = Math.Clamp((double)downloaded / total, 0, 1);
                    _progress[song.Id] = progress;
                    Raise(song.Id, DownloadState.Downloading, progress, null);
                }, ct);
            }
            File.Move(tempPath, finalPath, overwrite: true);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        ct.ThrowIfCancellationRequested();

        // Ensure the catalogue row exists, then record the offline copy.
        try { await _database.UpsertSongAsync(song.ToEntity(), ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Trace.WriteLine($"[Download] upsert failed: {ex.Message}"); }
        await _database.SetDownloadAsync(song.Id, finalPath, DateTime.UtcNow, ct);

        _paths[song.Id] = finalPath;
        _states[song.Id] = DownloadState.Downloaded;
        _progress[song.Id] = 1;
        Raise(song.Id, DownloadState.Downloaded, 1, finalPath);
        Debug.WriteLine($"[Download] saved \"{song.Title}\"");
    }

    private static string ExtensionFor(string? mime)
    {
        var m = mime ?? string.Empty;
        if (m.Contains("webm", StringComparison.OrdinalIgnoreCase)) return ".webm";
        if (m.Contains("mp4", StringComparison.OrdinalIgnoreCase)) return ".m4a";
        if (m.Contains("mpeg", StringComparison.OrdinalIgnoreCase)) return ".mp3";
        if (m.Contains("ogg", StringComparison.OrdinalIgnoreCase)) return ".ogg";
        return ".webm";
    }

    private static string Sanitize(string id)
    {
        var chars = id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }

    private void Raise(string songId, DownloadState state, double progress, string? path)
        => Changed?.Invoke(this, new DownloadChangedEventArgs
        {
            SongId = songId,
            State = state,
            Progress = progress,
            Path = path
        });
}
