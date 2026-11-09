using System.Text.Json.Serialization;

namespace SoundLibert.Infra.InnerTube.Models;

public class SearchBody
{
    [JsonPropertyName("context")]
    public Context Context { get; set; } = new();

    [JsonPropertyName("query")]
    public string? Query { get; set; }

    [JsonPropertyName("params")]
    public string? Params { get; set; }
}

public class BrowseBody
{
    [JsonPropertyName("context")]
    public Context Context { get; set; } = new();

    [JsonPropertyName("browseId")]
    public string? BrowseId { get; set; }

    [JsonPropertyName("params")]
    public string? Params { get; set; }

    [JsonPropertyName("continuation")]
    public string? Continuation { get; set; }

    /// <summary>The charts' country.</summary>
    [JsonPropertyName("formData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowseFormData? FormData { get; set; }
}

public class BrowseFormData
{
    [JsonPropertyName("selectedValues")]
    public List<string> SelectedValues { get; set; } = new();
}

/// <summary>Body for account/account_menu (port of Velune's AccountMenuBody).</summary>
public class AccountMenuBody
{
    [JsonPropertyName("context")]
    public Context? Context { get; set; }

    [JsonPropertyName("deviceTheme")]
    public string DeviceTheme { get; set; } = "DEVICE_THEME_SELECTED";

    [JsonPropertyName("userInterfaceTheme")]
    public string UserInterfaceTheme { get; set; } = "USER_INTERFACE_THEME_DARK";
}

/// <summary>Body for music/get_queue (port of Velune's GetQueueBody).</summary>
public class GetQueueBody
{
    [JsonPropertyName("context")]
    public Context? Context { get; set; }

    [JsonPropertyName("videoIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? VideoIds { get; set; }

    [JsonPropertyName("playlistId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlaylistId { get; set; }
}

public class PlayerBody
{
    [JsonPropertyName("context")]
    public Context Context { get; set; } = new();

    [JsonPropertyName("videoId")]
    public string VideoId { get; set; } = string.Empty;

    [JsonPropertyName("playlistId")]
    public string? PlaylistId { get; set; }

    [JsonPropertyName("playbackContext")]
    public PlaybackContextBody? PlaybackContext { get; set; }

    [JsonPropertyName("serviceIntegrityDimensions")]
    public ServiceIntegrityDimensions? ServiceIntegrityDimensions { get; set; }

    [JsonPropertyName("contentCheckOk")]
    public bool ContentCheckOk { get; set; } = true;

    [JsonPropertyName("racyCheckOk")]
    public bool RacyCheckOk { get; set; } = true;
}

public class PlaybackContextBody
{
    [JsonPropertyName("contentPlaybackContext")]
    public ContentPlaybackContext? ContentPlaybackContext { get; set; }
}

public class ContentPlaybackContext
{
    [JsonPropertyName("signatureTimestamp")]
    public int SignatureTimestamp { get; set; }
}

public class ServiceIntegrityDimensions
{
    [JsonPropertyName("poToken")]
    public string? PoToken { get; set; }
}

public class GetSearchSuggestionsBody
{
    [JsonPropertyName("context")]
    public Context Context { get; set; } = new();

    [JsonPropertyName("input")]
    public string Input { get; set; } = string.Empty;
}
