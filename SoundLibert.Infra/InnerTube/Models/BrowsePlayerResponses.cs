using System.Text.Json.Serialization;

namespace SoundLibert.Infra.InnerTube.Models;

using SoundLibert.Infra.InnerTube;

public class BrowseResponse
{
    [JsonPropertyName("responseContext")]
    public ResponseContext? ResponseContext { get; set; }

    [JsonPropertyName("contents")]
    public BrowseResponseContents? Contents { get; set; }

    [JsonPropertyName("header")]
    public BrowseResponseHeader? Header { get; set; }

    [JsonPropertyName("continuationContents")]
    public ContinuationContents? ContinuationContents { get; set; }

    [JsonPropertyName("microformat")]
    public Microformat? Microformat { get; set; }

    [JsonPropertyName("onResponseReceivedActions")]
    public List<OnResponseReceivedAction>? OnResponseReceivedActions { get; set; }
}

public class BrowseResponseContents
{
    [JsonPropertyName("singleColumnBrowseResultsRenderer")]
    public SingleColumnBrowseResultsRenderer? SingleColumnBrowseResultsRenderer { get; set; }

    [JsonPropertyName("twoColumnBrowseResultsRenderer")]
    public TwoColumnBrowseResultsRenderer? TwoColumnBrowseResultsRenderer { get; set; }

    [JsonPropertyName("sectionListRenderer")]
    public SectionListRenderer? SectionListRenderer { get; set; }
}

public class BrowseResponseHeader
{
    [JsonPropertyName("musicImmersiveHeaderRenderer")]
    public MusicImmersiveHeaderRenderer? MusicImmersiveHeaderRenderer { get; set; }

    [JsonPropertyName("musicResponsiveHeaderRenderer")]
    public MusicResponsiveHeaderRenderer? MusicResponsiveHeaderRenderer { get; set; }

    [JsonPropertyName("musicHeaderRenderer")]
    public MusicHeaderRenderer? MusicHeaderRenderer { get; set; }
}

public class SingleColumnBrowseResultsRenderer
{
    [JsonPropertyName("tabs")]
    public List<BrowseTab>? Tabs { get; set; }
}

public class BrowseTab
{
    [JsonPropertyName("tabRenderer")]
    public BrowseTabRenderer? TabRenderer { get; set; }
}

public class BrowseTabRenderer
{
    [JsonPropertyName("content")]
    public SectionListRendererContent? Content { get; set; }

    [JsonPropertyName("endpoint")]
    public NavigationEndpoint? Endpoint { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

public class TwoColumnBrowseResultsRenderer
{
    [JsonPropertyName("tabs")]
    public List<BrowseTab>? Tabs { get; set; }

    [JsonPropertyName("secondaryContents")]
    public SecondaryContents? SecondaryContents { get; set; }
}

public class SecondaryContents
{
    [JsonPropertyName("sectionListRenderer")]
    public SectionListRenderer? SectionListRenderer { get; set; }
}

public class MusicImmersiveHeaderRenderer
{
    [JsonPropertyName("title")]
    public RunsText? Title { get; set; }

    [JsonPropertyName("thumbnail")]
    public ThumbnailRenderer? Thumbnail { get; set; }

    [JsonPropertyName("description")]
    public RunsText? Description { get; set; }

    [JsonPropertyName("playButton")]
    public PlayButton? PlayButton { get; set; }

    [JsonPropertyName("startRadioButton")]
    public PlayButton? StartRadioButton { get; set; }

    [JsonPropertyName("subscriptionButton")]
    public SubscriptionButton? SubscriptionButton { get; set; }

    /// <summary>"535M monthly audience".</summary>
    [JsonPropertyName("monthlyListenerCount")]
    public RunsText? MonthlyListenerCount { get; set; }
}

public class MusicResponsiveHeaderRenderer
{
    [JsonPropertyName("title")]
    public RunsText? Title { get; set; }

    [JsonPropertyName("subtitle")]
    public RunsText? Subtitle { get; set; }

    [JsonPropertyName("thumbnail")]
    public ThumbnailRenderer? Thumbnail { get; set; }

    [JsonPropertyName("straplineTextOne")]
    public RunsText? StraplineTextOne { get; set; }

    [JsonPropertyName("secondSubtitle")]
    public RunsText? SecondSubtitle { get; set; }

    [JsonPropertyName("buttons")]
    public List<MenuRenderer>? Buttons { get; set; }
}

public class MusicHeaderRenderer
{
    [JsonPropertyName("title")]
    public RunsText? Title { get; set; }
}

public class PlayButton
{
    [JsonPropertyName("buttonRenderer")]
    public ButtonRenderer? ButtonRenderer { get; set; }
}

public class ButtonRenderer
{
    [JsonPropertyName("navigationEndpoint")]
    public NavigationEndpoint? NavigationEndpoint { get; set; }
}

public class SubscriptionButton
{
    [JsonPropertyName("subscribeButtonRenderer")]
    public SubscribeButtonRenderer? SubscribeButtonRenderer { get; set; }
}

public class SubscribeButtonRenderer
{
    [JsonPropertyName("channelId")]
    public string? ChannelId { get; set; }

    /// <summary>"33.4M subscribers".</summary>
    [JsonPropertyName("longSubscriberCountText")]
    public RunsText? LongSubscriberCountText { get; set; }
}

public class MenuRenderer
{
    [JsonPropertyName("items")]
    public List<MenuItem>? Items { get; set; }
}

public class MenuItem
{
    [JsonPropertyName("menuNavigationItemRenderer")]
    public MenuNavigationItemRenderer? MenuNavigationItemRenderer { get; set; }

    [JsonPropertyName("menuServiceItemRenderer")]
    public MenuServiceItemRenderer? MenuServiceItemRenderer { get; set; }
}

public class MenuNavigationItemRenderer
{
    [JsonPropertyName("navigationEndpoint")]
    public NavigationEndpoint? NavigationEndpoint { get; set; }

    [JsonPropertyName("icon")]
    public IconRenderer? Icon { get; set; }
}

public class MenuServiceItemRenderer
{
    [JsonPropertyName("serviceEndpoint")]
    public NavigationEndpoint? ServiceEndpoint { get; set; }

    [JsonPropertyName("icon")]
    public IconRenderer? Icon { get; set; }
}

public class Microformat
{
    [JsonPropertyName("microformatDataRenderer")]
    public MicroformatDataRenderer? MicroformatDataRenderer { get; set; }
}

public class MicroformatDataRenderer
{
    [JsonPropertyName("urlCanonical")]
    public string? UrlCanonical { get; set; }
}

public class OnResponseReceivedAction
{
    [JsonPropertyName("appendContinuationItemsAction")]
    public AppendContinuationItemsAction? AppendContinuationItemsAction { get; set; }
}

public class AppendContinuationItemsAction
{
    [JsonPropertyName("continuationItems")]
    public List<ContinuationItem>? ContinuationItems { get; set; }
}

public class ContinuationItem
{
    [JsonPropertyName("musicResponsiveListItemRenderer")]
    public MusicResponsiveListItemRenderer? MusicResponsiveListItemRenderer { get; set; }

    [JsonPropertyName("musicTwoRowItemRenderer")]
    public MusicTwoRowItemRenderer? MusicTwoRowItemRenderer { get; set; }

    [JsonPropertyName("continuationItemRenderer")]
    public ContinuationItemRenderer? ContinuationItemRenderer { get; set; }
}

public class ContinuationItemRenderer
{
    [JsonPropertyName("continuationEndpoint")]
    public NavigationEndpoint? ContinuationEndpoint { get; set; }
}

public class PlayerResponse
{
    [JsonPropertyName("streamingData")]
    public StreamingData? StreamingData { get; set; }

    [JsonPropertyName("videoDetails")]
    public VideoDetails? VideoDetails { get; set; }

    [JsonPropertyName("playabilityStatus")]
    public PlayabilityStatus? PlayabilityStatus { get; set; }

    [JsonPropertyName("playerConfig")]
    public PlayerConfig? PlayerConfig { get; set; }
}

public class PlayerConfig
{
    [JsonPropertyName("audioConfig")]
    public AudioConfig? AudioConfig { get; set; }
}

public class AudioConfig
{
    [JsonPropertyName("loudnessDb")]
    public double? LoudnessDb { get; set; }

    [JsonPropertyName("perceptualLoudnessDb")]
    public double? PerceptualLoudnessDb { get; set; }
}

public class StreamingData
{
    [JsonPropertyName("formats")]
    public List<StreamingFormat>? Formats { get; set; }

    [JsonPropertyName("adaptiveFormats")]
    public List<StreamingFormat>? AdaptiveFormats { get; set; }

    [JsonPropertyName("expiresInSeconds")]
    [JsonConverter(typeof(LenientIntConverter))]
    public int? ExpiresInSeconds { get; set; }
}

public class StreamingFormat
{
    [JsonPropertyName("itag")]
    public int Itag { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("bitrate")]
    public int Bitrate { get; set; }

    [JsonPropertyName("contentLength")]
    [JsonConverter(typeof(LenientLongConverter))]
    public long? ContentLength { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("fps")]
    public int Fps { get; set; }

    [JsonPropertyName("audioQuality")]
    public string? AudioQuality { get; set; }

    [JsonPropertyName("averageBitrate")]
    [JsonConverter(typeof(LenientIntConverter))]
    public int? AverageBitrate { get; set; }

    [JsonPropertyName("audioSampleRate")]
    [JsonConverter(typeof(LenientIntConverter))]
    public int? AudioSampleRate { get; set; }

    [JsonPropertyName("approxDurationMs")]
    public string? ApproxDurationMs { get; set; }

    [JsonPropertyName("loudnessDb")]
    [JsonConverter(typeof(LenientDoubleConverter))]
    public double? LoudnessDb { get; set; }

    [JsonPropertyName("signatureCipher")]
    public string? SignatureCipher { get; set; }

    [JsonPropertyName("cipher")]
    public string? Cipher { get; set; }
}

public class VideoDetails
{
    [JsonPropertyName("videoId")]
    public string? VideoId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    /// <summary>The uploading channel ("UC…"): an artist's page has the same id as their own channel.</summary>
    [JsonPropertyName("channelId")]
    public string? ChannelId { get; set; }

    [JsonPropertyName("lengthSeconds")]
    public string? LengthSeconds { get; set; }

    [JsonPropertyName("viewCount")]
    public string? ViewCount { get; set; }

    [JsonPropertyName("shortDescription")]
    public string? ShortDescription { get; set; }
}

public class PlayabilityStatus
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// <c>responseContext</c>: only the tracking params are read, for the
/// <c>logged_in</c> flag that tells a real session from a signed-out visitor.
/// </summary>
public class ResponseContext
{
    [JsonPropertyName("serviceTrackingParams")]
    public List<ServiceTrackingParams>? ServiceTrackingParams { get; set; }

    /// <summary>
    /// True/false from the <c>logged_in</c> tracking param; null when YouTube
    /// did not send it (unknown — callers must then fall back to old behaviour).
    /// </summary>
    public bool? LoggedIn
    {
        get
        {
            var value = ServiceTrackingParams?
                .SelectMany(s => s.Params ?? new List<TrackingParam>())
                .FirstOrDefault(p => p.Key == "logged_in")?.Value;
            return value switch { "1" => true, "0" => false, _ => null };
        }
    }
}

public class ServiceTrackingParams
{
    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("params")]
    public List<TrackingParam>? Params { get; set; }
}

public class TrackingParam
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}
