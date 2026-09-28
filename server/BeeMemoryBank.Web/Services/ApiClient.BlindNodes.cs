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

    /// <summary>
    /// Pairs and seeds a blind node from its code; the error lists every pre-flight problem, the
    /// warnings what the pre-flight said without stopping the add (no peer could confirm this PC).
    /// </summary>
    public async Task<(bool Ok, string? Error, IReadOnlyList<string> Warnings)> AddBlindNodeAsync(string code)
    {
        var resp = await http.PostAsJsonAsync("/api/blind-nodes/", new { code }, JsonOpts);
        var body = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode)
        {
            try
            {
                using var added = JsonDocument.Parse(body);
                return (true, null, added.RootElement.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
                    ? w.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                    : []);
            }
            catch (JsonException)
            {
                return (true, null, []);
            }
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            if (doc.RootElement.TryGetProperty("problems", out var problems))
                error = string.Join(" ", problems.EnumerateArray().Select(p => p.GetString()));
            return (false, error ?? "Could not add the blind node.", []);
        }
        catch (JsonException)
        {
            return (false, "Could not add the blind node.", []);
        }
    }

    public async Task<(bool Ok, string? Error)> ReseedBlindNodeAsync(Guid nodeId)
    {
        var resp = await http.PostAsync($"/api/blind-nodes/{nodeId}/reseed", null);
        return resp.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(resp));
    }
}
