using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Domain.Models;

namespace SoundLibert.Infra.AiMusic;

/// <summary>
/// Tells AI-generated acts apart by name. The names come from the community list kept by
/// spotify-ai-blocker (about ten thousand acts, refreshed daily; it gathers loved.music,
/// Zoundhub and Spotify AI Blocklist), downloaded and kept on this device. YouTube Music's
/// own AI label is not in the data apps receive, so names are what there is.
/// <para>
/// A name is only a name: a few of the list's one-word names are also real singers
/// (Aurora, Luna, Iris…). Those are never matched, a one-word match whose song is heard by
/// tens of millions counts as human, and the listener's own word always wins.
/// </para>
/// </summary>
public sealed partial class AiMusicFilter : IAiMusicFilter
{
    /// <summary>The community list: "artist,id" (a Spotify artist id), one act per line.</summary>
    public const string ListUrl = "https://raw.githubusercontent.com/CennoxX/spotify-ai-blocker/main/SpotifyAiArtists.csv";

    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstDownloadWait = TimeSpan.FromSeconds(4);

    /// <summary>Plays above which a one-word name match is taken for a human namesake.</summary>
    private const long HumanReach = 20_000_000;

    /// <summary>
    /// Names on the list that are also people, words, or acts sung by people (the KPop
    /// Demon Hunters' HUNTR/X): never matched by the list alone.
    /// </summary>
    private static readonly HashSet<string> NotByListAlone = new(StringComparer.Ordinal)
    {
        "huntr/x", "aurora", "luna", "iris", "nova", "max", "maya", "ari", "eli", "oliver"
    };

    private static readonly HttpClient Http = CreateClient();

    private readonly IAiMusicSettings _settings;
    private readonly string _cachePath;
    private readonly object _loadGate = new();
    private volatile HashSet<string> _names = new(StringComparer.Ordinal);
    private Task? _loading;

    public AiMusicFilter(IAiMusicSettings settings)
        : this(settings, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundLibert", "ai-artists.csv"))
    {
    }

    /// <summary>For tests: the list kept elsewhere.</summary>
    public AiMusicFilter(IAiMusicSettings settings, string cachePath)
    {
        _settings = settings;
        _cachePath = cachePath;
    }

    public event Action? Changed;

    public bool Enabled => _settings.HideAiMusic;

    public int KnownCount => _names.Count;

    public int MarkedCount => _settings.AiArtistMarks.Count;

    // ---- Judging ----

    public bool IsAi(SongItem song)
    {
        if (LooksMadeWithAi(song.Title)) return true;
        long? reach = song.ViewCount > 0 ? song.ViewCount : null;
        return song.Artists.Any(a => IsAiArtist(a.Name, reach));
    }

    public bool IsAi(ShelfItem item)
    {
        switch (item.Kind)
        {
            case ShelfItemKind.Song or ShelfItemKind.Video when item.Song is { } song:
                return IsAi(song) || SubtitleNamesAi(item.Subtitle);
            case ShelfItemKind.Song or ShelfItemKind.Video:
                return LooksMadeWithAi(item.Title) || SubtitleNamesAi(item.Subtitle);
            case ShelfItemKind.Artist:
                return IsAiArtist(item.Title, ParseReach(item.Subtitle));
            case ShelfItemKind.Album:
                return LooksMadeWithAi(item.Title) || SubtitleNamesAi(item.Subtitle);
            default:
                // A playlist is many people's music: kept.
                return false;
        }
    }

    public bool IsAi(ArtistItem artist) => IsAiArtist(artist.Name, ParseReach(artist.Subtitle));

    public bool IsAiArtist(string? name, long? reach = null)
    {
        var key = Normalize(name);
        if (key.Length == 0) return false;
        if (_settings.AiArtistMarks.TryGetValue(key, out var marked)) return marked;
        if (NotByListAlone.Contains(key) || !_names.Contains(key)) return false;
        // A one-word name heard by tens of millions is far likelier a human namesake.
        return !(reach >= HumanReach && !key.Contains(' '));
    }

    /// <summary>"Single • Kenke • 2026", "Kenke, Someone • 1.2M plays": any part that names an AI act.</summary>
    private bool SubtitleNamesAi(string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle)) return false;
        var reach = ParseReach(subtitle);
        foreach (var part in subtitle.Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsAiArtist(part, reach)) return true;
            if (part.Contains(',') &&
                part.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(p => IsAiArtist(p, reach)))
                return true;
        }
        return false;
    }

    /// <summary>Titles that say it themselves: "(AI Cover)", "AI version", "made with Suno".</summary>
    public static bool LooksMadeWithAi(string? title) => !string.IsNullOrEmpty(title) && MadeWithAi().IsMatch(title);

    [GeneratedRegex(@"\bai[\s-]+(?:cover|version|remix|song|generated|music)\b|[(\[]\s*a\.?i\.?\s*[)\]]|\bsuno\b|\budio\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MadeWithAi();

    /// <summary>"362M plays", "1.2B views", "33.4M subscribers" as a number.</summary>
    public static long? ParseReach(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = Reach().Match(text);
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value.Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        return (long)(value * match.Groups[2].Value.ToUpperInvariant() switch
        {
            "B" => 1e9,
            "M" => 1e6,
            "K" => 1e3,
            _ => 1
        });
    }

    [GeneratedRegex(@"([\d.,]+)\s*([KMB])?\s*(?:plays|views|subscribers|monthly)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reach();

    /// <summary>Case, spacing and look-alike letters set aside: "  The  Velvet Sundown" = "the velvet sundown".</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var text = name.Trim().Trim('"').Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
    }

    // ---- The listener's word ----

    public bool? MarkOf(string artistName) =>
        _settings.AiArtistMarks.TryGetValue(Normalize(artistName), out var marked) ? marked : null;

    public void Mark(string artistName, bool? isAi)
    {
        var key = Normalize(artistName);
        if (key.Length == 0) return;
        _settings.SetAiArtistMark(key, isAi);
        Changed?.Invoke();
    }

    public void ClearMarks()
    {
        foreach (var key in _settings.AiArtistMarks.Keys.ToList()) _settings.SetAiArtistMark(key, null);
        Changed?.Invoke();
    }

    // ---- The list ----

    public Task EnsureReadyAsync(CancellationToken ct = default)
    {
        Task loading;
        lock (_loadGate) loading = _loading ??= Task.Run(LoadAsync, CancellationToken.None);
        // The first time ever, the download is waited for a moment; later the kept copy is there at once.
        return loading.IsCompleted ? Task.CompletedTask : loading.WaitAsync(FirstDownloadWait, ct).ContinueWith(_ => { },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The kept copy at once; a fresh one from the network once a day, waited for only when none is kept.</summary>
    private async Task LoadAsync()
    {
        DateTime? keptAt = null;
        try
        {
            if (File.Exists(_cachePath))
            {
                var kept = Parse(await File.ReadAllLinesAsync(_cachePath).ConfigureAwait(false));
                if (kept.Count > 0)
                {
                    _names = kept;
                    keptAt = File.GetLastWriteTimeUtc(_cachePath);
                    Changed?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AiMusic] kept list unreadable: {ex.Message}");
        }

        if (keptAt is null) await DownloadAsync().ConfigureAwait(false);
        else if (DateTime.UtcNow - keptAt.Value >= RefreshAfter) _ = DownloadAsync();
    }

    private async Task DownloadAsync()
    {
        try
        {
            var text = await Http.GetStringAsync(ListUrl).ConfigureAwait(false);
            var lines = text.Split('\n');
            var names = Parse(lines);
            // A damaged or truncated answer must not empty the filter.
            if (names.Count < 1000)
            {
                Trace.WriteLine($"[AiMusic] list looked wrong ({names.Count} names); kept the old one");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            await File.WriteAllTextAsync(temp, text).ConfigureAwait(false);
            File.Move(temp, _cachePath, overwrite: true);
            _names = names;
            Trace.WriteLine($"[AiMusic] list updated: {names.Count} AI artists");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AiMusic] list download failed: {ex.Message}");
        }
    }

    /// <summary>"artist,id" lines; a name may be quoted and hold commas, the id never does.</summary>
    public static HashSet<string> Parse(IEnumerable<string> lines)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var first = true;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (first)
            {
                first = false;
                if (line.StartsWith("artist", StringComparison.OrdinalIgnoreCase)) continue;
            }
            var comma = line.LastIndexOf(',');
            var name = comma > 0 ? line[..comma] : line;
            if (name.Length >= 2 && name[0] == '"' && name[^1] == '"') name = name[1..^1].Replace("\"\"", "\"");
            var key = Normalize(name);
            if (key.Length > 0) names.Add(key);
        }
        return names;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SoundLibert");
        return client;
    }
}
