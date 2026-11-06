using System.Collections.Concurrent;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// The blocking recovery-key step a NEW node shows after its first administrator signs in (release 2.7.5). There is
/// no e-mail recovery: the key is the only way back into a vault whose password was forgotten, so a new node's
/// administrator is shown one before the app, with Copy and an "I saved my recovery key" confirmation.
///
/// <para>While the node says the step is required and unconfirmed, <see cref="Middleware.RecoveryKeyGateMiddleware"/>
/// sends every Web page of a superadmin session here. The API, MCP and agents are not affected - unattended nodes
/// keep working.</para>
///
/// <para>A reload must not mint a second recovery slot: the key shown in this Web process is cached per signed-in
/// name for at most 15 minutes, so a reload within that window shows the same key, and confirmation removes the
/// entry. Every access sweeps expired entries first, so an abandoned step does not keep the key in the process.
/// The string itself is not wiped (strings are immutable in .NET); it becomes unreachable once its entry is
/// removed. A restart of the Web process (or another browser) mints a new key, which is an unused extra slot by
/// design - the brief accepts that rather than storing the key anywhere.</para>
/// </summary>
[Authorize(Roles = "superadmin")]
public class RecoveryKeyGateModel(ApiClient api) : PageModel
{
    internal static readonly ConcurrentDictionary<string, (string Key, DateTime FetchedAt)> Shown = new(StringComparer.Ordinal);
    private static readonly TimeSpan ShownLifetime = TimeSpan.FromMinutes(15);

    /// <summary>The key, grouped in fours for reading; empty when it could not be issued.</summary>
    public string RecoveryKey { get; private set; } = "";

    public string? ErrorMessage { get; private set; }

    public string ReturnUrl { get; private set; } = "/Tree";

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        SweepExpired(DateTime.UtcNow);
        ReturnUrl = SafeReturnUrl(returnUrl);

        var status = await api.GetRecoveryKeyStatusAsync();
        if (status is null)
        {
            ErrorMessage = "The node could not be reached to check the recovery-key step. Try again, or open the app if this keeps happening.";
            return Page();
        }
        if (!status.Required || status.Acknowledged)
            return LocalRedirect(ReturnUrl);

        var who = User.Identity?.Name ?? "";
        if (Shown.TryGetValue(who, out var cached) && DateTime.UtcNow - cached.FetchedAt < ShownLifetime)
        {
            RecoveryKey = Group(cached.Key);
            return Page();
        }

        var (key, error) = await api.AddRecoveryKeyAsync();
        if (key is null)
        {
            ErrorMessage = error ?? "This node could not issue a recovery key. Try again in a moment.";
            return Page();
        }
        Shown[who] = (key, DateTime.UtcNow);
        RecoveryKey = Group(key);
        return Page();
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(string? returnUrl = null)
    {
        SweepExpired(DateTime.UtcNow);
        ReturnUrl = SafeReturnUrl(returnUrl);
        if (!await api.AcknowledgeRecoveryKeyAsync())
        {
            ErrorMessage = "The node did not record the confirmation. Try again.";
            var status = await api.GetRecoveryKeyStatusAsync();
            if (status is { Required: true, Acknowledged: false } && Shown.TryGetValue(User.Identity?.Name ?? "", out var cached))
                RecoveryKey = Group(cached.Key);
            return Page();
        }

        Shown.TryRemove(User.Identity?.Name ?? "", out _);
        return LocalRedirect(ReturnUrl);
    }

    /// <summary>Drops entries older than <see cref="ShownLifetime"/>; every GET and POST runs this first.</summary>
    internal static void SweepExpired(DateTime nowUtc)
    {
        foreach (var (who, entry) in Shown)
            if (nowUtc - entry.FetchedAt >= ShownLifetime)
                Shown.TryRemove(who, out _);
    }

    /// <summary>The key in groups of four, the way a person reads it off a screen.</summary>
    private static string Group(string key) =>
        string.Join(" ", Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));

    /// <summary>Only a local path: the step must not become an open redirect.</summary>
    internal static string SafeReturnUrl(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return "/Tree";
        var url = candidate.Trim();
        if (!url.StartsWith('/') || url.StartsWith("//") || url.StartsWith("/\\")) return "/Tree";
        if (url.Contains("://", StringComparison.Ordinal)) return "/Tree";
        return url;
    }
}