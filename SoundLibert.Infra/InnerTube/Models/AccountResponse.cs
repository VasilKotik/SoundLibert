using System.Text.Json.Serialization;

namespace SoundLibert.Infra.InnerTube.Models;

/// <summary>
/// Response of <c>account/account_menu</c> (port of Velune's AccountMenuResponse).
/// Only the active-account header is needed: name, email, handle and avatar.
/// </summary>
public class AccountMenuResponse
{
    [JsonPropertyName("responseContext")]
    public ResponseContext? ResponseContext { get; set; }

    [JsonPropertyName("actions")]
    public List<AccountMenuAction>? Actions { get; set; }
}

public class AccountMenuAction
{
    [JsonPropertyName("openPopupAction")]
    public AccountOpenPopupAction? OpenPopupAction { get; set; }
}

public class AccountOpenPopupAction
{
    [JsonPropertyName("popup")]
    public AccountPopup? Popup { get; set; }
}

public class AccountPopup
{
    [JsonPropertyName("multiPageMenuRenderer")]
    public AccountMultiPageMenuRenderer? MultiPageMenuRenderer { get; set; }
}

public class AccountMultiPageMenuRenderer
{
    [JsonPropertyName("header")]
    public AccountHeader? Header { get; set; }
}

public class AccountHeader
{
    [JsonPropertyName("activeAccountHeaderRenderer")]
    public ActiveAccountHeaderRenderer? ActiveAccountHeaderRenderer { get; set; }
}

public class ActiveAccountHeaderRenderer
{
    [JsonPropertyName("accountName")]
    public RunsText? AccountName { get; set; }

    [JsonPropertyName("email")]
    public RunsText? Email { get; set; }

    [JsonPropertyName("channelHandle")]
    public RunsText? ChannelHandle { get; set; }

    [JsonPropertyName("accountPhoto")]
    public Thumbnail? AccountPhoto { get; set; }
}
