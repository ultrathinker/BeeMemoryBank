using System.Net.Http.Json;
using System.Text.Json;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    // ─── Metadata encryption (Step A) ───────────────────────────────────────────
    // The Admin area's "Metadata encryption" page (docs/design/step-a-spec.md 5.3, 5.4). Null on any transport error or
    // non-2xx, so the card says "unavailable" instead of failing the page.

    /// <param name="askPeers">False: this node's own observations only (the page load); true: every reachable device too.</param>
    public async Task<JsonElement?> GetMetadataReadinessAsync(bool askPeers)
    {
        try
        {
            return await http.GetFromJsonAsync<JsonElement>($"/api/sync/readiness?peers={(askPeers ? "true" : "false")}", JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The one-way switch. On a 409 the blockers come back as the answer's JSON.</summary>
    public async Task<(bool Ok, string? Error, JsonElement? Answer)> ActivateMetadataAsync(string password)
    {
        try
        {
            var resp = await http.PostAsJsonAsync("/api/sync/activation", new { password }, JsonOpts);
            var answer = resp.Content.Headers.ContentLength is 0 ? (JsonElement?)null
                : await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
            if (resp.IsSuccessStatusCode) return (true, null, answer);
            var error = (int)resp.StatusCode switch
            {
                401 => "Wrong master password.",
                423 => "The node is locked. Unlock it first.",
                409 => "Not every condition is met yet; see the list below.",
                _ => $"The activation failed ({(int)resp.StatusCode}).",
            };
            return (false, error, answer);
        }
        catch (Exception ex)
        {
            return (false, BeeMemoryBank.Sync.FailureText.Say(ex), null);
        }
    }

    /// <summary>The held part of /api/sync/status (spec 6.9), or null.</summary>
    public async Task<JsonElement?> GetHeldEventsAsync()
    {
        try
        {
            var status = await http.GetFromJsonAsync<JsonElement>("/api/sync/status", JsonOpts);
            return status.ValueKind == JsonValueKind.Object && status.TryGetProperty("held", out var held) ? held : null;
        }
        catch
        {
            return null;
        }
    }
}
