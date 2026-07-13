namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    // ─── OS Auto-Unlock ───────────────────────────────────────────────────────

    /// <summary>Returns (Enabled, Supported) for the OS auto-unlock feature.</summary>
    public async Task<(bool Enabled, bool Supported)> GetAutoUnlockStatusAsync()
    {
        try
        {
            var resp = await http.GetFromJsonAsync<AutoUnlockStatusDto>("/api/keys/auto-unlock/status", JsonOpts);
            return (resp?.Enabled ?? false, resp?.Supported ?? false);
        }
        catch
        {
            return (false, false);
        }
    }

    /// <summary>Enables OS auto-unlock. Returns (ok, errorMessage).</summary>
    public async Task<(bool Ok, string? Error)> EnableAutoUnlockAsync()
    {
        var resp = await http.PostAsync("/api/keys/auto-unlock/enable", null);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp) ?? "Failed to enable OS auto-unlock.");
    }

    /// <summary>Disables OS auto-unlock. Returns (ok, errorMessage).</summary>
    public async Task<(bool Ok, string? Error)> DisableAutoUnlockAsync()
    {
        var resp = await http.PostAsync("/api/keys/auto-unlock/disable", null);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp) ?? "Failed to disable OS auto-unlock.");
    }

    private sealed record AutoUnlockStatusDto(bool Enabled, bool Supported);
}
