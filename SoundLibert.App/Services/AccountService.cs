using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoundLibert.Domain.Interfaces;
using SoundLibert.Infra.InnerTube;

namespace SoundLibert.App.Services;

/// <summary>
/// Persists the optional Google sign-in (cookie + DATA_SYNC_ID + visitorData)
/// and pushes it into <see cref="InnerTubeClient"/>. On Windows the store is
/// encrypted with DPAPI (current user); elsewhere it relies on user-only file
/// permissions. The cookie is never written to logs.
/// </summary>
internal sealed class AccountService : IAccountService
{
    private readonly InnerTubeClient _client;
    private readonly string _storePath;
    private AccountRecord? _record;

    public AccountService(InnerTubeClient client)
    {
        _client = client;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundLibert");
        Directory.CreateDirectory(dir);
        _storePath = Path.Combine(dir, "account.dat");

        // If YouTube rejects the stored session, the app stops sending it and
        // keeps working anonymously. Nothing is deleted (see below).
        _client.AuthenticationFailed += (_, _) => HandleAuthenticationFailed();
    }

    public event EventHandler? SignedIn;
    public event EventHandler? SignedOut;
    public event EventHandler? ReauthenticationRequired;

    private volatile bool _needsReauthentication;

    public bool NeedsReauthentication => IsSignedIn && _needsReauthentication;

    public void MarkNeedsReauthentication()
    {
        if (_record == null || _needsReauthentication) return;
        _needsReauthentication = true;
        Trace.WriteLine("[Account] Session no longer recognised by YouTube — sync paused until the next sign-in (library kept).");
        ReauthenticationRequired?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A rejection may be temporary and is no reason to lose anything: the stored
    /// sign-in and the mirrored library stay. Requests continue anonymously, sync
    /// pauses, and the next start tries the stored session again.
    /// </summary>
    private void HandleAuthenticationFailed()
    {
        if (_record == null) return;
        _client.Cookie = null;
        _client.DataSyncId = null;
        MarkNeedsReauthentication();
    }

    public bool IsSignedIn =>
        _record != null &&
        !string.IsNullOrEmpty(_record.Cookie) &&
        _record.Cookie.Contains("SAPISID", StringComparison.Ordinal) &&
        _record.Cookie.Contains("LOGIN_INFO", StringComparison.Ordinal);

    public string? AccountName => _record?.Name;
    public string? AccountEmail => _record?.Email;
    public string? AccountHandle => _record?.ChannelHandle;
    public string? AccountAvatarUrl => _record?.AvatarUrl;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var raw = await File.ReadAllBytesAsync(_storePath, ct);
            var record = JsonSerializer.Deserialize<AccountRecord>(Unprotect(raw));
            if (record == null) return;

            _record = record;

            if (!IsSignedIn)
            {
                // A stored cookie without LOGIN_INFO is an anonymous/partial
                // session — treat it as signed out instead of sending it.
                Trace.WriteLine("[Account] Stored cookie is not a real session — signing out.");
                _record = null;
                try { File.Delete(_storePath); } catch { }
                return;
            }

            Apply(record);
            Trace.WriteLine($"[Account] Loaded sign-in (email present: {!string.IsNullOrEmpty(record.Email)})");

            // Always hit an authenticated endpoint once on startup: it fills any
            // missing display fields AND validates the session. A rejected
            // session pauses the sync for this run (see HandleAuthenticationFailed).
            await EnrichFromAccountMenuAsync(ct);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Account] Load failed: {ex.Message}");
        }
    }

    public async Task SignInAsync(string cookie, string dataSyncId, string? visitorData,
        string? name, string? email, CancellationToken ct = default)
    {
        _needsReauthentication = false;
        _record = new AccountRecord
        {
            Cookie = cookie,
            DataSyncId = dataSyncId,
            VisitorData = visitorData,
            Name = name,
            Email = email
        };
            Apply(_record);

            // Resolve the real display details (name/email/handle/avatar) from the
            // account menu, then persist them with the credentials.
            await EnrichFromAccountMenuAsync(ct);

            try
            {
                var bytes = Protect(JsonSerializer.Serialize(_record));
                await File.WriteAllBytesAsync(_storePath, bytes, ct);
                Trace.WriteLine("[Account] Sign-in stored");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Account] Save failed: {ex.Message}");
            }

            SignedIn?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Fills the account record's display fields from <c>account/account_menu</c>
        /// (name, email, channel handle, avatar). Best-effort — failures are ignored.
        /// </summary>
        private async Task EnrichFromAccountMenuAsync(CancellationToken ct)
        {
            if (_record == null) return;
            try
            {
                var response = await _client.AccountMenuAsync(
                    Infra.InnerTube.Models.YouTubeClient.WEB_REMIX, ct);

                var header = response.Actions?
                    .Select(a => a.OpenPopupAction?.Popup?.MultiPageMenuRenderer?.Header?.ActiveAccountHeaderRenderer)
                    .FirstOrDefault(h => h != null);
                if (header == null)
                {
                    // No account header = YouTube treats us as a signed-out
                    // visitor (expired cookie answered with HTTP 200).
                    if (response.ResponseContext?.LoggedIn != true) MarkNeedsReauthentication();
                    return;
                }

                var name = header.AccountName?.Runs?.FirstOrDefault()?.Text;
                var email = header.Email?.Runs?.FirstOrDefault()?.Text;
                var handle = header.ChannelHandle?.Runs?.FirstOrDefault()?.Text;
                var avatar = header.AccountPhoto?.Thumbnails?.LastOrDefault()?.Url;

                if (!string.IsNullOrWhiteSpace(name)) _record.Name = name;
                if (!string.IsNullOrWhiteSpace(email)) _record.Email = email;
                if (!string.IsNullOrWhiteSpace(handle)) _record.ChannelHandle = handle;
                if (!string.IsNullOrWhiteSpace(avatar)) _record.AvatarUrl = avatar;

                Trace.WriteLine($"[Account] Resolved account menu (name present: {!string.IsNullOrEmpty(_record.Name)}, email present: {!string.IsNullOrEmpty(_record.Email)})");
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Trace.WriteLine($"[Account] account_menu rejected the session ({(int)ex.StatusCode}).");
                HandleAuthenticationFailed();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Account] account_menu enrich failed: {ex.Message}");
            }
        }


    public Task SignOutAsync(CancellationToken ct = default)
    {
        _record = null;
        _client.Cookie = null;
        _client.DataSyncId = null;
        try
        {
            if (File.Exists(_storePath)) File.Delete(_storePath);
        }
        catch { }

        SignedOut?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private void Apply(AccountRecord record)
    {
        _client.Cookie = string.IsNullOrWhiteSpace(record.Cookie) ? null : record.Cookie;
        _client.DataSyncId = string.IsNullOrWhiteSpace(record.DataSyncId) ? null : record.DataSyncId;
        if (!string.IsNullOrWhiteSpace(record.VisitorData)) _client.VisitorData = record.VisitorData;
    }

    private static byte[] Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return OperatingSystem.IsWindows()
            ? ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)
            : bytes;
    }

    private static string Unprotect(byte[] bytes)
        => OperatingSystem.IsWindows()
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser))
            : Encoding.UTF8.GetString(bytes);

    private sealed class AccountRecord
    {
        public string? Cookie { get; set; }
        public string? DataSyncId { get; set; }
        public string? VisitorData { get; set; }
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? ChannelHandle { get; set; }
        public string? AvatarUrl { get; set; }
    }
}
