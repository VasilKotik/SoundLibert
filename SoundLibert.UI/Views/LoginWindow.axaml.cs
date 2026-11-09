using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SoundLibert.Domain.Interfaces;

namespace SoundLibert.UI.Views;

/// <summary>
/// Embedded Google sign-in. Starts from a logged-out state and, once the WebView
/// lands on music.youtube.com, reads DATA_SYNC_ID / VISITOR_DATA from
/// window.yt.config_ (via the package's invokeCSharpAction bridge) and captures
/// the session cookies through the cross-platform cookie manager.
/// Logout-first is deliberate: the chooser is convenient (no retyping) but YT
/// Music first lands with the previous account, so the capture took the wrong
/// session — typing credentials once lands exactly where asked. The settle
/// window below stays as a second guard. No credentials are ever logged.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly IAccountService _account;
    private bool _completed;
    // Temporary diagnostics for the "logs into the same account" issue.
    private readonly DateTime _openedAt = DateTime.UtcNow;

    private const string ServiceLoginUrl =
        "https://accounts.google.com/ServiceLogin?continue=https%3A%2F%2Fmusic.youtube.com";

    /// <summary>Logs the WebView profile out of Google, then continues to sign-in.</summary>
    private static readonly Uri LogoutFirst = new(
        "https://accounts.google.com/Logout?continue=" + Uri.EscapeDataString(ServiceLoginUrl));

    public LoginWindow()
    {
        InitializeComponent();
        _account = App.ServiceProvider!.GetRequiredService<IAccountService>();

        Web.EnvironmentRequested += (_, e) =>
        {
            if (e is Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs windows)
                windows.UserDataFolder = SoundLibert.UI.Services.WebViewProfile.Folder;
        };
        Web.Source = LogoutFirst;

        // Stop any in-flight navigation/cookie work and release the native WebView
        // when the window closes, so a later sign-in always starts clean.
        Closed += (_, _) =>
        {
            lock (_settleGate)
            {
                _completed = true;
                _settleCts?.Cancel();
                _settleCts?.Dispose();
                _settleCts = null;
            }
            try { Web.NavigationCompleted -= Web_NavigationCompleted; } catch { }
            try { Web.WebMessageReceived -= Web_MessageReceived; } catch { }
        };
    }

    private async void Web_NavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (_completed) return;
        try
        {
            // No-op on non-YT pages (window.yt is undefined there).
            await Web.InvokeScript(
                "invokeCSharpAction('DS='+encodeURIComponent((window.yt&&window.yt.config_&&window.yt.config_.DATASYNC_ID)||'')+'|VD='+encodeURIComponent((window.yt&&window.yt.config_&&window.yt.config_.VISITOR_DATA)||''))");
        }
        catch
        {
            // Page not ready / script blocked — try again on the next navigation.
        }
    }

    private async void Web_MessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (_completed || string.IsNullOrEmpty(e.Body)) return;

        var dataSyncId = Extract(e.Body, "DS=");
        if (string.IsNullOrWhiteSpace(dataSyncId)) return;

        // Anonymous music.youtube.com pages also expose a DATA_SYNC_ID, so a
        // real session is only confirmed by the presence of the SAPISID cookie.
        // Without it we keep waiting for the user to finish signing in.
        string? cookie;
        try
        {
            cookie = await BuildCookieHeaderAsync();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(cookie) ||
            !cookie.Contains("SAPISID", StringComparison.Ordinal) ||
            !cookie.Contains("LOGIN_INFO", StringComparison.Ordinal))
            return;

        var visitorData = Extract(e.Body, "VD=");
        // YT Music first lands with the PREVIOUS account for a few seconds and
        // only then settles on the picked one — committing the first valid
        // session grabs the old account. So a session must prove stable: same
        // cookie jar across SettleDelay before it is accepted.
        var hash = SessionHash(cookie);
        lock (_settleGate)
        {
            if (_completed) return;
            if (!string.Equals(hash, _settleHash, StringComparison.Ordinal))
            {
                _settleHash = hash;
                _settleCookie = cookie;
                _settleDataSyncId = dataSyncId;
                _settleVisitorData = visitorData;
                _settleCts?.Cancel();
                _settleCts?.Dispose();
                _settleCts = new CancellationTokenSource();
                _ = SettleAsync(_settleCts.Token);
            }
        }
    }

    /// <summary>How long a session must stay unchanged before it is accepted.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(4);

    private readonly object _settleGate = new();
    private string? _settleHash;
    private string? _settleCookie;
    private string? _settleDataSyncId;
    private string? _settleVisitorData;
    private CancellationTokenSource? _settleCts;

    private static string SessionHash(string cookie) => Convert.ToHexString(
        System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(cookie)))[..8];

    private async Task SettleAsync(CancellationToken ct)
    {
        string? hash;
        lock (_settleGate) hash = _settleHash;
        try
        {
            await Task.Delay(SettleDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return; // a newer session arrived — it owns the timer now.
        }

        string? cookie;
        string? dataSyncId;
        string? visitorData;
        lock (_settleGate)
        {
            // Window closed, newer session arrived, or someone already committed.
            if (_completed || !string.Equals(hash, _settleHash, StringComparison.Ordinal)) return;
            _completed = true;
            cookie = _settleCookie;
            dataSyncId = _settleDataSyncId;
            visitorData = _settleVisitorData;
        }
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(dataSyncId)) return;

        var elapsed = DateTime.UtcNow - _openedAt;
        System.Diagnostics.Debug.WriteLine($"[Login] session settled after {elapsed.TotalSeconds:0}s, session={hash}");
        Dispatcher.UIThread.Post(() => Busy.IsVisible = true);

        try
        {
            await _account.SignInAsync(cookie, dataSyncId, visitorData, null, null);
        }
        catch
        {
            // Sign-in failed — leave the user signed out rather than crash.
        }
        finally
        {
            Dispatcher.UIThread.Post(Close);
        }
    }

    private async Task<string?> BuildCookieHeaderAsync()
    {
        var manager = Web.TryGetCookieManager();
        if (manager == null) return null;

        var cookies = await manager.GetCookiesAsync();
        var builder = new StringBuilder();
        foreach (var cookie in cookies.Where(c =>
                     !string.IsNullOrEmpty(c.Name) &&
                     (c.Domain?.Contains("youtube", StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            builder.Append(cookie.Name).Append('=').Append(cookie.Value).Append("; ");
        }
        return builder.ToString().TrimEnd(' ', ';');
    }

    private static string? Extract(string body, string key)
    {
        var index = body.IndexOf(key, StringComparison.Ordinal);
        if (index < 0) return null;

        var start = index + key.Length;
        var end = body.IndexOf('|', start);
        var raw = end < 0 ? body[start..] : body[start..end];
        var decoded = Uri.UnescapeDataString(raw.Trim('\'', '"', ' '));
        return string.IsNullOrEmpty(decoded) ? null : decoded;
    }
}
