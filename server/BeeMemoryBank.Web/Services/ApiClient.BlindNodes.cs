using System.Text.Json;
using BeeMemoryBank.Web.Models;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    public async Task<List<BlindNodeDto>?> ListBlindNodesAsync()
    {
        try
        {
            var resp = await http.GetAsync("/api/blind-nodes/");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<BlindNodeDto>>(JsonOpts);
        }
        catch { return null; }
    }

    /// <summary>Pairs and seeds a blind node from its code; the error lists every pre-flight problem.</summary>
    public async Task<(bool Ok, string? Error)> AddBlindNodeAsync(string code)
    {
        var resp = await http.PostAsJsonAsync("/api/blind-nodes/", new { code }, JsonOpts);
        if (resp.IsSuccessStatusCode) return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            if (doc.RootElement.TryGetProperty("problems", out var problems))
                error = string.Join(" ", problems.EnumerateArray().Select(p => p.GetString()));
            return (false, error ?? "Could not add the blind node.");
        }
        catch (JsonException)
        {
            return (false, "Could not add the blind node.");
        }
    }

    public async Task<(bool Ok, string? Error)> ReseedBlindNodeAsync(Guid nodeId)
    {
        var resp = await http.PostAsync($"/api/blind-nodes/{nodeId}/reseed", null);
        return resp.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(resp));
    }
}
