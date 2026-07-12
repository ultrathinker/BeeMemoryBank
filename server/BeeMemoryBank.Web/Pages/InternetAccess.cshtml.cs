using System.Text.Json;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// "Access from the internet" wizard (superplan §5 Ярус 2, Этап 5) — ties together DDNS,
/// ACME cert issuance and the reachability self-test behind one superadmin-only page.
/// Mirrors the Admin page model's conventions: ApiClient is injected, every action is a
/// POST handler that round-trips through TempData + RedirectToPage, and the view renders
/// from a single <see cref="Info"/> payload fetched on GET.
/// </summary>
[Authorize(Roles = "superadmin")]
public class InternetAccessModel(ApiClient api) : PageModel
{
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Raw /api/internet-access/info payload; the view pulls every section out of it.</summary>
    public JsonElement? Info { get; set; }

    public async Task OnGetAsync(string? msg = null, string? err = null)
    {
        SuccessMessage = msg;
        ErrorMessage = err;
        Info = await api.GetInternetAccessInfoAsync();
    }

    // ─── DDNS ─────────────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostSaveDdnsConfigAsync(
        string provider, string? domain, string? token,
        string? zoneId, string? recordId, string? apiToken,
        string ipMode, string? staticIp)
    {
        var (ok, error, _) = await api.SaveDdnsConfigAsync(
            provider, domain, token, zoneId, recordId, apiToken, ipMode, staticIp);
        return ok
            ? RedirectToPage(new { msg = $"DDNS settings saved ({provider}, {ipMode})." })
            : RedirectToPage(new { err = error ?? "Failed to save DDNS settings." });
    }

    public async Task<IActionResult> OnPostCheckDdnsNowAsync()
    {
        var (ok, error, body) = await api.CheckDdnsNowAsync();
        if (!ok)
            return RedirectToPage(new { err = error ?? "DDNS check failed." });

        // The body carries success/changed/message/error from DdnsUpdateResult. Route by `success`
        // so a provider update failure shows as a red error rather than a green success.
        var success = body?.TryGetProperty("success", out var sf) == true && sf.GetBoolean();
        var changed = body?.TryGetProperty("changed", out var c) == true && c.GetBoolean();
        var message = body?.TryGetProperty("message", out var m) == true ? m.GetString() : null;
        var detail = body?.TryGetProperty("error", out var e) == true ? e.GetString() : null;
        if (!success)
            return RedirectToPage(new { err = string.IsNullOrEmpty(detail) ? (message ?? "DDNS check failed.") : $"{message} ({detail})" });
        return RedirectToPage(new { msg = message ?? (changed ? "DDNS record updated." : "DDNS checked — no change.") });
    }

    // ─── ACME ─────────────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostSaveAcmeConfigAsync(string domain, string? contactsEmail, bool useStaging)
    {
        var (ok, error, _) = await api.SaveAcmeConfigAsync(domain, contactsEmail, useStaging);
        return ok
            ? RedirectToPage(new { msg = $"ACME settings saved ({(useStaging ? "staging" : "production")})." })
            : RedirectToPage(new { err = error ?? "Failed to save ACME settings." });
    }

    public async Task<IActionResult> OnPostRequestCertificateAsync(string? domain, string? contactsEmail, bool? useStaging)
    {
        var (ok, error, body) = await api.RequestCertificateAsync(domain, contactsEmail, useStaging);
        if (ok)
        {
            var msg = body?.TryGetProperty("message", out var m) == true ? m.GetString() : null;
            return RedirectToPage(new { msg = msg ?? "Certificate requested." });
        }
        // On failure the body still carries a helpful hint + trace; surface the error verbatim.
        var hint = body?.TryGetProperty("hint", out var h) == true ? h.GetString() : null;
        return RedirectToPage(new { err = string.IsNullOrEmpty(hint) ? (error ?? "Certificate request failed.") : hint });
    }

    // ─── Reachability self-test ───────────────────────────────────────────────────

    public async Task<IActionResult> OnPostProbeAsync(string candidateUrl)
    {
        var (ok, error, body) = await api.ProbeReachabilityAsync(candidateUrl);
        if (!ok)
            return RedirectToPage(new { err = error ?? "Reachability probe failed." });

        // Outcome drives the success/error coloring + the CGNAT hint text in the view.
        var outcome = body?.TryGetProperty("outcome", out var o) == true ? o.GetString() : null;
        var message = body?.TryGetProperty("message", out var m) == true ? m.GetString() : null;
        if (outcome == "Reachable")
            return RedirectToPage(new { msg = message ?? "Reachable — your node is open to the internet." });
        // Unreachable / NoPeersAvailable / PeerUnreachable / InvalidUrl → treat as an error the
        // view expands into the honest CGNAT guidance when appropriate.
        return RedirectToPage(new { err = message ?? "Could not confirm reachability." });
    }

    /// <summary>
    /// Formats an ISO-8601 UTC timestamp (as returned by the API) as a short local-time string.
    /// Returns "—" for null/blank/unparseable values so the view never renders a raw "...T...Z".
    /// </summary>
    public static string FormatTime(string? isoUtc)
    {
        if (string.IsNullOrWhiteSpace(isoUtc)) return "—";
        if (DateTime.TryParse(isoUtc, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var utc))
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        return isoUtc;
    }
}
