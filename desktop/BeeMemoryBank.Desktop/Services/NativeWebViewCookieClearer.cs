using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Production <see cref="IWebViewCookieClearer"/> backed by an Avalonia
/// <see cref="NativeWebView"/>. Used to scrub the WebView's cookie jar between profiles so a
/// stale session cookie from profile A is never replayed against profile B (defense in depth
/// alongside the per-DB session store, which already rejects foreign cookies).
///
/// Everything here is best-effort: a missing cookie manager (unsupported platform), a failing
/// <see cref="NativeWebViewCookieManager.GetCookiesAsync"/>, or an individual
/// <see cref="NativeWebViewCookieManager.DeleteCookie"/> that throws must never abort a
/// profile switch. The caller (<see cref="ProfileSwitchService"/>) additionally wraps the whole
/// call in a try/catch so this implementation may simply propagate and let it be logged.
/// </summary>
public sealed class NativeWebViewCookieClearer : IWebViewCookieClearer
{
    private readonly NativeWebView _webView;

    public NativeWebViewCookieClearer(NativeWebView webView)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
    }

    public async Task ClearAllCookiesAsync()
    {
        var manager = _webView.TryGetCookieManager();
        if (manager == null)
        {
            // Platform/adapter does not expose a cookie manager (e.g. before the environment
            // is ready). Nothing to clear — log and succeed.
            Debug.WriteLine("NativeWebViewCookieClearer: no cookie manager available; skipping cookie clear.");
            return;
        }

        IReadOnlyList<Cookie> cookies;
        try
        {
            cookies = await manager.GetCookiesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"NativeWebViewCookieClearer: GetCookiesAsync failed: {ex.Message}");
            return;
        }

        // NativeWebViewCookieManager.DeleteCookie signature is (name, domain, path) — note the
        // argument order: name FIRST, then domain, then path.
        foreach (var cookie in cookies)
        {
            try
            {
                manager.DeleteCookie(cookie.Name, cookie.Domain, cookie.Path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NativeWebViewCookieClearer: DeleteCookie failed for '{cookie.Name}' on '{cookie.Domain}': {ex.Message}");
            }
        }
    }
}
