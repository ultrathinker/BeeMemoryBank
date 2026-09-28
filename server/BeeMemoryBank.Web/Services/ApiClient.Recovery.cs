using System.Net.Http.Json;
using BeeMemoryBank.Web.Models;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    // ─── Recovery boxes (blind nodes) ─────────────────────────────────────────

    /// <summary>
    /// The recovery-box warnings' source. Null when the API cannot say (older node, not a superadmin):
    /// the page then shows no recovery warning rather than an error.
    /// </summary>
    public async Task<RecoveryStatusDto?> GetRecoveryStatusAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<RecoveryStatusDto>("/api/recovery/status", JsonOpts);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public Task<(bool Ok, string? Error)> StartRestoreFromBlindAsync(string? address, string code, string password, string adminUsername, string displayName) =>
        StartRestoreAsync("/api/restore/blind", new { address, code, password, adminUsername, displayName });

    public Task<(bool Ok, string? Error)> StartRestoreFromBackupAsync(string path, string password, string adminUsername, string displayName) =>
        StartRestoreAsync("/api/restore/backup", new { path, password, adminUsername, displayName });

    public Task<(bool Ok, string? Error)> ContinueRestoreAsync(string boxes) =>
        StartRestoreAsync("/api/restore/continue", new { boxes });

    /// <summary>Devices a restore kept inactive (no anchor vouched for them). Superadmin (Admin page).</summary>
    public async Task<List<RestoredPeerDto>> GetRestoredPeersAsync()
    {
        try { return await http.GetFromJsonAsync<List<RestoredPeerDto>>("/api/recovery/restored-peers", JsonOpts) ?? []; }
        catch (HttpRequestException) { return []; }
    }

    public async Task<bool> ConfirmRestoredPeerAsync(Guid nodeId) =>
        (await http.PostAsync($"/api/recovery/restored-peers/{nodeId}/confirm", null)).IsSuccessStatusCode;

    /// <summary>The wizard's bootstrap after a restore: confirms devices with the master password.</summary>
    public async Task<(bool Ok, string? Error)> ConfirmRestoredPeersWithPasswordAsync(string password, List<Guid> nodeIds)
    {
        var resp = await http.PostAsJsonAsync("/api/restore/confirm-peers", new { password, nodeIds }, JsonOpts);
        return resp.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(resp));
    }

    public async Task CancelRestoreAsync()
    {
        try { await http.PostAsync("/api/restore/cancel", null); }
        catch (HttpRequestException) { }
    }

    public async Task<BlindRestoreProgressDto?> GetBlindRestoreProgressAsync()
    {
        try { return await http.GetFromJsonAsync<BlindRestoreProgressDto>("/api/restore/progress", JsonOpts); }
        catch (HttpRequestException) { return null; }
    }

    private async Task<(bool Ok, string? Error)> StartRestoreAsync(string route, object body)
    {
        var resp = await http.PostAsJsonAsync(route, body, JsonOpts);
        return resp.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(resp));
    }
}
