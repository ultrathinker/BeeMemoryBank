using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Web.Models;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    // ─── Internet-access wizard (superplan §5 Ярус 2, Этап 5) ────────────────────
    // These wrap the /api/internet-access/* and /api/sync/probe endpoints so the
    // InternetAccess PageModel can call them through InternalKeyHandler (server-side),
    // matching how the Admin update section's calls are wired. No browser-side proxy
    // table entry is needed — the PageModel handlers are the entry points.

    /// <summary>
    /// GET /api/internet-access/info — LAN IP(s), local ports, persisted DDNS config/state,
    /// persisted ACME config + stored certificate. Returns the raw JSON element so the page
    /// model can hand fields straight to the view without a fixed DTO.
    /// </summary>
    public async Task<JsonElement?> GetInternetAccessInfoAsync()
    {
        try { return await http.GetFromJsonAsync<JsonElement>("/api/internet-access/info", JsonOpts); }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error, JsonElement? Body)> SaveDdnsConfigAsync(
        string provider, string? domain, string? token,
        string? zoneId, string? recordId, string? apiToken,
        string ipMode, string? staticIp)
    {
        var resp = await http.PostAsJsonAsync("/api/internet-access/ddns/config", new
        {
            provider, domain, token, zoneId, recordId, apiToken, ipMode, staticIp
        }, JsonOpts);
        return await ReadResultAsync(resp);
    }

    public async Task<(bool Ok, string? Error, JsonElement? Body)> CheckDdnsNowAsync()
    {
        var resp = await http.PostAsync("/api/internet-access/ddns/check", null);
        return await ReadResultAsync(resp);
    }

    public async Task<(bool Ok, string? Error, JsonElement? Body)> SaveAcmeConfigAsync(
        string domain, string? contactsEmail, bool useStaging)
    {
        var resp = await http.PostAsJsonAsync("/api/internet-access/acme/config", new
        {
            domain, contactsEmail, useStaging
        }, JsonOpts);
        return await ReadResultAsync(resp);
    }

    public async Task<(bool Ok, string? Error, JsonElement? Body)> RequestCertificateAsync(
        string? domain, string? contactsEmail, bool? useStaging)
    {
        var resp = await http.PostAsJsonAsync("/api/internet-access/acme/request", new
        {
            domain, contactsEmail, useStaging
        }, JsonOpts);
        return await ReadResultAsync(resp);
    }

    /// <summary>
    /// Reachability self-test — calls the existing internal-key-gated
    /// <c>POST /api/sync/probe</c> (defined in SyncEndpoints) with the candidate public URL.
    /// Returns its <c>SyncProbeResponse</c> body verbatim so the wizard can branch on Outcome.
    /// </summary>
    public async Task<(bool Ok, string? Error, JsonElement? Body)> ProbeReachabilityAsync(string url)
    {
        var resp = await http.PostAsJsonAsync("/api/sync/probe", new { url }, JsonOpts);
        return await ReadResultAsync(resp);
    }

    // Shared by the internet-access methods: returns (ok, error, body) where body is the parsed
    // JSON element on success OR on a structured error (so the caller can read trace/error/hint).
    private static async Task<(bool Ok, string? Error, JsonElement? Body)> ReadResultAsync(HttpResponseMessage resp)
    {
        var bodyText = await resp.Content.ReadAsStringAsync();
        JsonElement? body = null;
        if (!string.IsNullOrWhiteSpace(bodyText))
        {
            try { body = JsonDocument.Parse(bodyText).RootElement.Clone(); }
            catch { /* non-JSON body */ }
        }
        if (resp.IsSuccessStatusCode) return (true, null, body);

        string? error = null;
        if (body is { } b && b.ValueKind == JsonValueKind.Object && b.TryGetProperty("error", out var e))
            error = e.GetString();
        return (false, error ?? $"HTTP {(int)resp.StatusCode}", body);
    }
}
