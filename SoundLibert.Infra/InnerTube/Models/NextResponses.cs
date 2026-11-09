using System.Text.Json.Serialization;

namespace SoundLibert.Infra.InnerTube.Models;

/// <summary>
/// Request body for the InnerTube `next` endpoint (port of Velune's NextBody).
/// With only a videoId it returns the "start radio / up next" queue.
/// </summary>
public class NextBody
{
    [JsonPropertyName("context")]
    public Context? Context { get; set; }

    [JsonPropertyName("videoId")]
    public string? VideoId { get; set; }

    [JsonPropertyName("playlistId")]
    public string? PlaylistId { get; set; }

    [JsonPropertyName("playlistSetVideoId")]
    public string? PlaylistSetVideoId { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("params")]
    public string? Params { get; set; }

    [JsonPropertyName("continuation")]
    public string? Continuation { get; set; }

    [JsonPropertyName("enablePersistentPlaylistPanel")]
    public bool? EnablePersistentPlaylistPanel { get; set; }

    [JsonPropertyName("isAudioOnly")]
    public bool? IsAudioOnly { get; set; }

    [JsonPropertyName("tunerSettingValue")]
    public string? TunerSettingValue { get; set; }

    [JsonPropertyName("watchEndpointMusicSupportedConfigs")]
    public WatchEndpointMusicSupportedConfigs? WatchEndpointMusicSupportedConfigs { get; set; }
}

public class WatchEndpointMusicSupportedConfigs
{
    [JsonPropertyName("watchEndpointMusicConfig")]
    public WatchEndpointMusicConfig? WatchEndpointMusicConfig { get; set; }
}

public class WatchEndpointMusicConfig
{
    [JsonPropertyName("hasPersistentPlaylistPanel")]
    public bool? HasPersistentPlaylistPanel { get; set; }

    [JsonPropertyName("musicVideoType")]
    public string? MusicVideoType { get; set; }
}

/// <summary>
/// Minimal shape of the `next` response needed to read the radio queue
/// (contents.singleColumnMusicWatchNextResultsRenderer…musicQueueRenderer…).
/// </summary>
public class NextResponse
{
    [JsonPropertyName("contents")]
    public NextContents? Contents { get; set; }
}

/// <summary>
/// Response of <c>music/get_queue</c> (port of Velune's GetQueueResponse): a flat
/// list of queue entries that reuse the playlist-panel renderer.
/// </summary>
public class GetQueueResponse
{
    [JsonPropertyName("queueDatas")]
    public List<QueueData>? QueueDatas { get; set; }
}

public class QueueData
{
    [JsonPropertyName("content")]
    public PlaylistPanelContent? Content { get; set; }
}

public class NextContents
{
    [JsonPropertyName("singleColumnMusicWatchNextResultsRenderer")]
    public SingleColumnMusicWatchNextResultsRenderer? SingleColumn { get; set; }
}

public class SingleColumnMusicWatchNextResultsRenderer
{
    [JsonPropertyName("tabbedRenderer")]
    public NextTabbedRenderer? TabbedRenderer { get; set; }
}

public class NextTabbedRenderer
{
    [JsonPropertyName("watchNextTabbedResultsRenderer")]
    public WatchNextTabbedResultsRenderer? WatchNext { get; set; }
}

public class WatchNextTabbedResultsRenderer
{
    [JsonPropertyName("tabs")]
    public List<NextTab>? Tabs { get; set; }
}

public class NextTab
{
    [JsonPropertyName("tabRenderer")]
    public NextTabRenderer? TabRenderer { get; set; }
}

public class NextTabRenderer
{
    [JsonPropertyName("content")]
    public NextTabContent? Content { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>The Related tab holds no content, only where to fetch it.</summary>
    [JsonPropertyName("endpoint")]
    public NavigationEndpoint? Endpoint { get; set; }
}

public class NextTabContent
{
    [JsonPropertyName("musicQueueRenderer")]
    public MusicQueueRenderer? MusicQueueRenderer { get; set; }
}

public class MusicQueueRenderer
{
    [JsonPropertyName("content")]
    public MusicQueueContent? Content { get; set; }
}

public class MusicQueueContent
{
    [JsonPropertyName("playlistPanelRenderer")]
    public PlaylistPanelRenderer? PlaylistPanelRenderer { get; set; }
}

public class PlaylistPanelRenderer
{
    [JsonPropertyName("contents")]
    public List<PlaylistPanelContent>? Contents { get; set; }

    [JsonPropertyName("playlistId")]
    public string? PlaylistId { get; set; }

    [JsonPropertyName("continuations")]
    public List<PanelContinuation>? Continuations { get; set; }
}

public class PanelContinuation
{
    [JsonPropertyName("nextContinuationData")]
    public ContinuationData? NextContinuationData { get; set; }

    [JsonPropertyName("reloadContinuationData")]
    public ContinuationData? ReloadContinuationData { get; set; }
}

public class ContinuationData
{
    [JsonPropertyName("continuation")]
    public string? Continuation { get; set; }
}

public class PlaylistPanelContent
{
    [JsonPropertyName("playlistPanelVideoRenderer")]
    public PlaylistPanelVideoRenderer? PlaylistPanelVideoRenderer { get; set; }

    [JsonPropertyName("playlistPanelVideoWrapperRenderer")]
    public PlaylistPanelVideoWrapperRenderer? PlaylistPanelVideoWrapperRenderer { get; set; }

    [JsonPropertyName("automixPreviewVideoRenderer")]
    public AutomixPreviewVideoRenderer? AutomixPreviewVideoRenderer { get; set; }
}

public class PlaylistPanelVideoWrapperRenderer
{
    [JsonPropertyName("primaryRenderer")]
    public PrimaryRenderer? PrimaryRenderer { get; set; }

    [JsonPropertyName("counterpart")]
    public List<CounterpartItem>? Counterpart { get; set; }
}

public class PrimaryRenderer
{
    [JsonPropertyName("playlistPanelVideoRenderer")]
    public PlaylistPanelVideoRenderer? PlaylistPanelVideoRenderer { get; set; }
}

public class CounterpartItem
{
    [JsonPropertyName("counterpartRenderer")]
    public CounterpartRenderer? CounterpartRenderer { get; set; }
}

public class CounterpartRenderer
{
    [JsonPropertyName("playlistPanelVideoRenderer")]
    public PlaylistPanelVideoRenderer? PlaylistPanelVideoRenderer { get; set; }
}

public class AutomixPreviewVideoRenderer
{
    [JsonPropertyName("content")]
    public AutomixContent? Content { get; set; }
}

public class AutomixContent
{
    [JsonPropertyName("automixPlaylistVideoRenderer")]
    public AutomixPlaylistVideoRenderer? AutomixPlaylistVideoRenderer { get; set; }
}

public class AutomixPlaylistVideoRenderer
{
    [JsonPropertyName("navigationEndpoint")]
    public NavigationEndpoint? NavigationEndpoint { get; set; }
}

public class PlaylistPanelVideoRenderer
{
    [JsonPropertyName("videoId")]
    public string? VideoId { get; set; }

    [JsonPropertyName("title")]
    public RunsText? Title { get; set; }

    [JsonPropertyName("longBylineText")]
    public RunsText? LongBylineText { get; set; }

    [JsonPropertyName("shortBylineText")]
    public RunsText? ShortBylineText { get; set; }

    [JsonPropertyName("lengthText")]
    public RunsText? LengthText { get; set; }

    [JsonPropertyName("thumbnail")]
    public Thumbnail? Thumbnail { get; set; }

    [JsonPropertyName("selected")]
    public bool Selected { get; set; }

    [JsonPropertyName("badges")]
    public List<BadgeRenderer>? Badges { get; set; }

    [JsonPropertyName("navigationEndpoint")]
    public NavigationEndpoint? NavigationEndpoint { get; set; }

    public string? MusicVideoType
    {
        get
        {
            var fromEndpoint = NavigationEndpoint?.WatchEndpoint?
                .WatchEndpointMusicSupportedConfigs?.WatchEndpointMusicConfig?.MusicVideoType;
            if (!string.IsNullOrEmpty(fromEndpoint)) return fromEndpoint;

            // Some payloads only tag the version on a badge.
            return Badges?
                .Select(b => b.MusicInlineBadgeRenderer?.Icon?.IconType)
                .FirstOrDefault(t => t != null && t.StartsWith("MUSIC_VIDEO_TYPE", System.StringComparison.Ordinal));
        }
    }
}
