namespace SoundLibert.Domain.Interfaces;

/// <summary>
/// Holds the optional Google/YouTube Music sign-in state and applies it to the
/// InnerTube client. Sign-in is entirely optional — the app is fully usable
/// anonymously. Credentials are stored locally (encrypted where the platform
/// allows) and never logged.
/// </summary>
public interface IAccountService
{
    bool IsSignedIn { get; }
    string? AccountName { get; }
    string? AccountEmail { get; }
    string? AccountHandle { get; }
    string? AccountAvatarUrl { get; }

    /// <summary>
    /// Stable key scoping local data (playlists, history, likes, searches,
    /// files) to this sign-in (email preferred). Null while signed out
    /// (device-local scope).
    /// </summary>
    string? AccountScopeKey => IsSignedIn ? (AccountEmail ?? AccountHandle) : null;

    /// <summary>
    /// True when YouTube stopped accepting the stored session: it rejects it
    /// outright, or answers as to a signed-out visitor. The sign-in and the
    /// local library mirror are KEPT (nothing is wiped); account sync pauses
    /// until the user signs in again. The next start tries the session again,
    /// so a rejection that was only temporary heals itself.
    /// </summary>
    bool NeedsReauthentication { get; }

    /// <summary>Raised once when <see cref="NeedsReauthentication"/> turns true.</summary>
    event EventHandler? ReauthenticationRequired;

    /// <summary>Flags the session as no longer recognised (see <see cref="NeedsReauthentication"/>).</summary>
    void MarkNeedsReauthentication();

    /// <summary>Raised after a successful sign-in so the UI can refresh account-dependent data.</summary>
    event EventHandler? SignedIn;

    /// <summary>Raised after the user signs out, so account-synced data can be dropped.</summary>
    event EventHandler? SignedOut;

    /// <summary>Loads any persisted sign-in and applies it to the InnerTube client.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Persists the credentials captured from the web sign-in flow.</summary>
    Task SignInAsync(string cookie, string dataSyncId, string? visitorData,
        string? name, string? email, CancellationToken ct = default);

    Task SignOutAsync(CancellationToken ct = default);
}
