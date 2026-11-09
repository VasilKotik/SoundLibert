using SoundLibert.Domain.Models;
using SoundLibert.Infra.InnerTube.Models;

namespace SoundLibert.Infra.InnerTube;

public static class ThumbnailExtensions
{
    public static string GetThumbnailUrl(this ThumbnailRenderer? renderer)
    {
        return renderer?.MusicThumbnailRenderer?.GetThumbnailUrl() ?? string.Empty;
    }

    public static string GetThumbnailUrl(this MusicThumbnailRenderer? renderer)
    {
        return renderer?.Thumbnail?.Thumbnails?.LastOrDefault()?.Url ?? string.Empty;
    }

    public static string FirstText(this RunsText? text)
    {
        if (text == null) return string.Empty;
        return text.Runs?.FirstOrDefault()?.Text ?? text.SimpleText ?? string.Empty;
    }

    /// <summary>
    /// All runs concatenated. Descriptions arrive split into several runs
    /// (one per line/paragraph), so <see cref="FirstText"/> would only return
    /// the first fragment — this returns the whole text.
    /// </summary>
    public static string FullText(this RunsText? text)
    {
        if (text == null) return string.Empty;
        if (text.Runs is { Count: > 0 }) return string.Concat(text.Runs.Select(r => r.Text));
        return text.SimpleText ?? string.Empty;
    }

    /// <summary>
    /// Port of Velune's splitBySeparator: splits subtitle runs into segments
    /// on the " • " separator, e.g. [artists] • [album] • [duration].
    /// </summary>
    public static List<List<Run>> SplitBySeparator(this List<Run> runs)
    {
        var result = new List<List<Run>>();
        var current = new List<Run>();
        foreach (var run in runs)
        {
            if (run.Text == " • ")
            {
                result.Add(current);
                current = new List<Run>();
            }
            else
            {
                current.Add(run);
            }
        }
        result.Add(current);
        return result;
    }

    /// <summary>
    /// Port of Velune SearchPage artist parsing: artists are the even-indexed
    /// runs (oddElements) of the FIRST subtitle segment. The old implementation
    /// added every non-separator run, so album title and duration leaked into
    /// the artist list.
    /// </summary>
    public static List<Artist> ExtractArtists(this RunsText? subtitle)
    {
        var artists = new List<Artist>();
        if (subtitle?.Runs == null) return artists;

        var segments = subtitle.Runs.SplitBySeparator();
        var first = segments.FirstOrDefault();
        if (first == null) return artists;

        for (int i = 0; i < first.Count; i += 2)
        {
            var run = first[i];
            if (string.IsNullOrWhiteSpace(run.Text)) continue;
            var artistId = run.NavigationEndpoint?.BrowseEndpoint?.BrowseId;
            artists.Add(new Artist { Name = run.Text.Trim(), Id = artistId });
        }
        return artists;
    }

    /// <summary>
    /// Port of Velune SearchPage duration parsing: duration is the first run
    /// of the LAST subtitle segment, formatted as m:ss or h:mm:ss.
    /// </summary>
    public static int ExtractDurationSeconds(this RunsText? subtitle)
    {
        if (subtitle?.Runs == null) return 0;
        var segments = subtitle.Runs.SplitBySeparator();
        var last = segments.LastOrDefault()?.FirstOrDefault();
        if (last == null) return 0;
        return ParseTime(last.Text);
    }

    public static int ParseTime(string text)
    {
        try
        {
            var parts = text.Trim().Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var min) && int.TryParse(parts[1], out var sec))
                return min * 60 + sec;
            if (parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) && int.TryParse(parts[2], out var s))
                return h * 3600 + m * 60 + s;
        }
        catch { }
        return 0;
    }
}
