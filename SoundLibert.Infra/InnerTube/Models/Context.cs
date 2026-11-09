using System.Text.Json.Serialization;

namespace SoundLibert.Infra.InnerTube.Models;

public class Context
{
    [JsonPropertyName("client")]
    public ContextClient Client { get; set; } = new();

    [JsonPropertyName("user")]
    public ContextUser? User { get; set; }

    [JsonPropertyName("thirdParty")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ContextThirdParty? ThirdParty { get; set; }
}

public class ContextThirdParty
{
    [JsonPropertyName("embedUrl")]
    public string? EmbedUrl { get; set; }
}

public class ContextClient
{
    [JsonPropertyName("clientName")]
    public string ClientName { get; set; } = string.Empty;

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = string.Empty;

    [JsonPropertyName("osName")]
    public string? OsName { get; set; }

    [JsonPropertyName("osVersion")]
    public string? OsVersion { get; set; }

    [JsonPropertyName("deviceMake")]
    public string? DeviceMake { get; set; }

    [JsonPropertyName("deviceModel")]
    public string? DeviceModel { get; set; }

    [JsonPropertyName("androidSdkVersion")]
    public string? AndroidSdkVersion { get; set; }

    [JsonPropertyName("gl")]
    public string? Gl { get; set; }

    [JsonPropertyName("hl")]
    public string? Hl { get; set; }

    [JsonPropertyName("visitorData")]
    public string? VisitorData { get; set; }
}

public class ContextUser
{
    [JsonPropertyName("onBehalfOfUser")]
    public string? OnBehalfOfUser { get; set; }
}
