using System.Diagnostics;
using System.Text.RegularExpressions;
using SoundLibert.Domain.Models;
using SoundLibert.Infra.InnerTube;
using SoundLibert.Infra.InnerTube.Models;

namespace SoundLibert.Infra;

/// <summary>
/// The clip behind a song: the music video that plays muted as the Canvas and with
/// sound in Video mode. Precision first, because a wrong clip is worse than none:
/// the cover's colours stand in when nothing fits.
/// </summary>
public partial class InnerTubeMusicProvider
{
    /// <summary>How many candidates one search may check (each check is a player request).</summary>
    private const int MaxClipChecks = 6;

    internal enum ClipVerdict { Accept, Reject, Unknown }

    /// <summary>A check's outcome; <paramref name="Tier"/> is judged from the video's own title.</summary>
    internal sealed record ClipCheck(ClipVerdict Verdict, string Note, int Tier = 0);

    private static readonly Regex LyricWord = new(@"\blyrics?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ArtistSeparators = new(
        @"\s*(?:,|&|\+|\bfeat\.?|\bft\.?|\bwith\b|\bw/|\bx\b|\band\b)\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Finds the clip for <paramref name="song"/>. Candidates come from the artist's
    /// YouTube Music page and from the Videos searches; they must name the song, and
    /// they are tried in tiers: the music video itself, then the artist performing it
    /// another way (a version, a session, a live take), then an official visualiser. Each is checked against its own
    /// player data (<see cref="JudgeClip"/>) before it is accepted. Throws when a
    /// candidate could not be checked and none was accepted, so a network hiccup is
    /// never remembered as "this song has no clip".
    /// </summary>
    private async Task<string?> FindClipAsync(SongItem song, List<SongItem> searchVideos, CancellationToken ct)
    {
        var known = await KnowSongAsync(song, ct);
        var artists = ArtistNamesOf(known);

        var page = new List<SongItem>();
        var primary = known.Artists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Name));
        if (primary != null)
        {
            try
            {
                page = await GetArtistChannelVideosAsync(primary.Id, primary.Name, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Clip] artist page failed for \"{primary.Name}\": {ex.Message}");
            }
        }

        // Search rows come first when the same video is in both: only they carry
        // YouTube Music's own type (OMV), which the artist page's rows lack.
        var channels = ArtistChannelsOf(known);
        var ranked = searchVideos.Concat(page)
            .Where(c => !string.IsNullOrEmpty(c.Id) && !string.Equals(c.Id, song.Id, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(c.Title))
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .Where(c => ClipTitleRules.TitlesMatch(song.Title, c.Title) && !ClipChoice.IsShortFormTitle(c.Title, song.Title))
            // The score's penalty for non-clip titles is the tier's job here: within a
            // tier the most watched upload should lead ("The Village Sessions", 533M).
            .Select(c => (Row: c, Tier: ClipTier(c.Title, song.Title),
                          Score: ScoreCandidate(known, c, preferVideo: true)
                                 + (ClipTitleRules.IsRealMusicVideoTitle(c.Title, song.Title) ? 0 : 30)))
            .Where(x => x.Tier > 0)
            .OrderBy(x => x.Tier)
            .ThenByDescending(x => x.Score)
            .Take(MaxClipChecks)
            .ToList();

        // A search row's title can hide what the video is ("Lose Control" turns out
        // "(Live)"): the first music video proper ends the search, anything else is
        // kept while the rest are checked, and the best of those wins.
        var passedOver = new List<string>();
        var unsure = false;
        (SongItem Row, ClipCheck Check, int Score)? best = null;
        foreach (var (row, _, score) in ranked)
        {
            var response = await PlayerForCheckAsync(row.Id, ct);
            var check = response == null
                ? new ClipCheck(ClipVerdict.Unknown, "could not be checked")
                : JudgeClip(known, artists, channels, row.Id, row.VideoType, pinned: false, response);
            if (check.Verdict == ClipVerdict.Accept)
            {
                if (best is not { } b || check.Tier < b.Check.Tier || (check.Tier == b.Check.Tier && score > b.Score))
                    best = (row, check, score);
                if (check.Tier == 1) break;
                continue;
            }
            if (check.Verdict == ClipVerdict.Unknown) unsure = true;
            passedOver.Add($"{row.Id} {check.Note}");
        }

        if (best is { } chosen)
        {
            Trace.WriteLine($"[Clip] \"{song.Title}\" ({known.ArtistDisplay}) -> {chosen.Row.Id} {chosen.Check.Note}"
                            + (passedOver.Count > 0 ? $"; passed over {string.Join("; ", passedOver)}" : string.Empty));
            return chosen.Row.Id;
        }
        if (unsure) throw new InvalidOperationException("clip choice: a candidate could not be checked");
        Trace.WriteLine($"[Clip] \"{song.Title}\" ({known.ArtistDisplay}) -> none"
                        + (passedOver.Count > 0 ? $"; rejected {string.Join("; ", passedOver)}" : "; no candidates"));
        return null;
    }

    /// <summary>
    /// 1 = the music video; 2 = the artist performing it otherwise (another version,
    /// a session, a live take: "Lose Control (The Village Sessions)"); 3 = an official
    /// visualiser; 0 = no picture of the song at all (lyric video, audio upload with a
    /// still image, cover, remix, karaoke, reaction, ...).
    /// </summary>
    internal static int ClipTier(string? title, string? songTitle)
    {
        if (string.IsNullOrWhiteSpace(title)) return 0;
        if (ClipChoice.IsLyricsUploadTitle(title, songTitle)) return 0;
        if (ClipTitleRules.IsRealMusicVideoTitle(title, songTitle))
            return ClipChoice.IsOtherVersionTitle(title, songTitle) ? 2 : 1;
        if (ClipTitleRules.IsRejectedUploadTitle(title, songTitle)) return 0;
        var rest = string.IsNullOrWhiteSpace(songTitle) ? title : title.Replace(songTitle, " ", StringComparison.OrdinalIgnoreCase);
        if (LyricWord.IsMatch(rest) || AudioWord.IsMatch(rest)) return 0;
        return ClipChoice.IsVisualizerTitle(title) ? 3 : 2;
    }

    private static readonly Regex AudioWord = new(@"\baudio\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Judges one candidate by its own player data. Being official must be shown, not
    /// read off a channel's name (anyone can call a channel "ImagineDragons"): the video
    /// comes from the channel behind the artist's own YouTube Music page
    /// (<paramref name="artistChannels"/>), from the artist's VEVO channel, or YouTube
    /// Music tags it as an official music video (<paramref name="rowType"/> OMV). A
    /// label's upload must also name the artist in its title. Fan and "Topic" channels
    /// never pass. A saved pin (<paramref name="pinned"/>) was shown official when it
    /// was chosen, so only the rest is checked again.
    /// </summary>
    internal static ClipCheck JudgeClip(SongItem song, IReadOnlyList<string> artists, IReadOnlyCollection<string> artistChannels,
        string id, string? rowType, bool pinned, PlayerResponse response)
    {
        static ClipCheck Reject(string why) => new(ClipVerdict.Reject, why);

        var status = response.PlayabilityStatus?.Status;
        if (!string.Equals(status, "OK", StringComparison.Ordinal)) return Reject($"not playable ({status ?? "no status"})");
        var details = response.VideoDetails;
        if (details == null || !string.Equals(details.VideoId, id, StringComparison.Ordinal)) return Reject("no details");

        var title = details.Title ?? string.Empty;
        if (!ClipTitleRules.TitlesMatch(song.Title, title)) return Reject($"another song \"{title}\"");
        if (ClipChoice.IsShortFormTitle(title, song.Title)) return Reject($"a short \"{title}\"");
        var tier = ClipTier(title, song.Title);
        if (tier == 0) return Reject($"not a clip \"{title}\"");

        var channel = ClipChoice.ClassifyChannel(details.Author, artists);
        if (channel == ClipChoice.ChannelKind.Topic) return Reject($"topic channel \"{details.Author}\"");
        if (channel == ClipChoice.ChannelKind.Fan) return Reject($"fan channel \"{details.Author}\"");
        if (!pinned)
        {
            var ownChannel = !string.IsNullOrEmpty(details.ChannelId) && artistChannels.Contains(details.ChannelId);
            var vevo = channel == ClipChoice.ChannelKind.Artist
                       && ClipTitleRules.Norm(details.Author).EndsWith("vevo", StringComparison.Ordinal);
            var taggedOfficial = string.Equals(rowType, "MUSIC_VIDEO_TYPE_OMV", StringComparison.Ordinal);
            if (!ownChannel && !vevo && !taggedOfficial)
                return Reject($"\"{details.Author}\" not shown official ({rowType ?? "no type"})");
            if (channel == ClipChoice.ChannelKind.Other && !ownChannel && !ClipChoice.NamesAnyArtist(title, artists))
                return Reject($"\"{details.Author}\" upload does not name the artist");
        }

        int.TryParse(details.LengthSeconds, out var length);
        if (!ClipChoice.IsPlausibleLength(length, song.DurationSeconds))
            return Reject($"{length}s for a {song.DurationSeconds}s song");

        var frame = LargestFrame(response);
        if (frame is { } f && !ClipChoice.IsWideFrame(f.Width, f.Height))
            return Reject($"{f.Width}x{f.Height} frame");

        var motion = MaxBitsPerPixel(response);
        if (ClipChoice.IsStillPicture(motion)) return Reject($"a still picture ({motion:0.0000} bpp)");

        var size = frame is { } g ? $", {g.Width}x{g.Height}" : string.Empty;
        return new ClipCheck(ClipVerdict.Accept, $"\"{title}\" by {details.Author}, {length}s{size}, tier {tier}", tier);
    }

    /// <summary>The most bits any video stream spends per pixel per frame (0 = unknown).</summary>
    private static double MaxBitsPerPixel(PlayerResponse response)
        => (response.StreamingData?.AdaptiveFormats ?? new List<StreamingFormat>())
            .Where(f => f.Width > 0 && f.Height > 0
                        && (f.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(f => (double)(f.AverageBitrate ?? f.Bitrate) / ((double)f.Width * f.Height * Math.Max(f.Fps, 1)))
            .DefaultIfEmpty(0)
            .Max();

    /// <summary>The biggest video frame among the formats, when the response lists any.</summary>
    private static (int Width, int Height)? LargestFrame(PlayerResponse response)
    {
        var formats = (response.StreamingData?.AdaptiveFormats ?? new List<StreamingFormat>())
            .Concat(response.StreamingData?.Formats ?? new List<StreamingFormat>())
            .Where(f => f.Width > 0 && f.Height > 0
                        && (f.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ?? true))
            .OrderByDescending(f => (long)f.Width * f.Height)
            .FirstOrDefault();
        return formats == null ? null : (formats.Width, formats.Height);
    }

    /// <summary>A candidate's player data from the first client that answers, or null.</summary>
    private async Task<PlayerResponse?> PlayerForCheckAsync(string id, CancellationToken ct)
    {
        foreach (var client in new[] { YouTubeClient.VISIONOS, YouTubeClient.MWEB, YouTubeClient.WEB_REMIX })
        {
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(10));
                return await _client.PlayerAsync(client, id, null, null, null, attempt.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Clip] check of {id} failed ({client.ClientName}): {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// The song with its length and artist filled in. Rows from some shelves carry
    /// neither, and without the length a 19-second teaser looked like the clip.
    /// The song's own player data has both (its "Artist - Topic" channel names the artist).
    /// </summary>
    private async Task<SongItem> KnowSongAsync(SongItem song, CancellationToken ct)
    {
        var hasArtist = song.Artists.Any(a => !string.IsNullOrWhiteSpace(a.Name));
        if (song.DurationSeconds > 0 && hasArtist) return song;

        var response = await PlayerForCheckAsync(song.Id, ct);
        var details = response?.VideoDetails;
        if (details == null) return song;

        int.TryParse(details.LengthSeconds, out var length);
        var artists = song.Artists;
        if (!hasArtist && !string.IsNullOrWhiteSpace(details.Author))
        {
            var name = details.Author.Trim();
            if (name.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase))
                name = name[..^"- Topic".Length].Trim();
            artists = new[] { new Artist { Name = name } };
        }
        return new SongItem
        {
            Id = song.Id,
            Title = song.Title,
            Artists = artists,
            AlbumTitle = song.AlbumTitle,
            VideoType = song.VideoType,
            DurationSeconds = song.DurationSeconds > 0 ? song.DurationSeconds : length
        };
    }

    /// <summary>The YouTube channels behind the song's artist pages ("UC…" browse ids).</summary>
    internal static HashSet<string> ArtistChannelsOf(SongItem song)
        => song.Artists
            .Select(a => a.Id)
            .Where(id => !string.IsNullOrEmpty(id) && id.StartsWith("UC", StringComparison.Ordinal))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Checks a clip that is known from a video row (played once as a video and paired
    /// with this song) as strictly as a new one: <paramref name="videoType"/> is that
    /// row's YouTube Music type. Could-not-check counts as fit.
    /// </summary>
    public async Task<bool> VerifyKnownClipAsync(SongItem song, string videoId, string? videoType, CancellationToken ct = default)
    {
        if (song is null || string.IsNullOrWhiteSpace(videoId)) return false;
        if (string.Equals(song.Id, videoId, StringComparison.Ordinal)) return true;
        var known = await KnowSongAsync(song, ct);
        var response = await PlayerForCheckAsync(videoId, ct);
        if (response == null) return true;
        var check = JudgeClip(known, ArtistNamesOf(known), ArtistChannelsOf(known), videoId, videoType, pinned: false, response);
        if (check.Verdict != ClipVerdict.Accept)
            Trace.WriteLine($"[Clip] known clip {videoId} for \"{song.Title}\" not used: {check.Note}");
        return check.Verdict == ClipVerdict.Accept;
    }

    /// <summary>
    /// Every name the song's artists go by, whole names first: "Lil Wayne, Wiz Khalifa
    /// & Imagine Dragons" gives the line itself and each of the three.
    /// </summary>
    internal static List<string> ArtistNamesOf(SongItem song)
    {
        var names = new List<string>();
        void Add(string? name)
        {
            var n = name?.Trim();
            if (string.IsNullOrEmpty(n) || n.Length < 2) return;
            if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        }
        foreach (var artist in song.Artists) Add(artist.Name);
        foreach (var artist in song.Artists)
            foreach (var part in ArtistSeparators.Split(artist.Name ?? string.Empty))
                Add(part);
        return names;
    }
}
