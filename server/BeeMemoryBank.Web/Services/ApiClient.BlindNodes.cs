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
    /// Pairs and seeds a blind node from its code; the error says in plain words what is wrong and what
    /// to do (every pre-flight problem, device by device), the warnings what the pre-flight said without
    /// stopping the add (no peer could confirm this PC).
    /// </summary>
    public async Task<(bool Ok, BlindNodeError? Error, IReadOnlyList<string> Warnings)> AddBlindNodeAsync(string code)
    {
        HttpResponseMessage resp;
        string body;
        try
        {
            resp = await http.PostAsJsonAsync("/api/blind-nodes/", new { code }, JsonOpts);
            body = await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, BlindNodeErrors.AppDidNotAnswer(ex), []);
        }
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
        return (false, BlindNodeErrors.ForAdd(resp.StatusCode, body), []);
    }

    public async Task<(bool Ok, BlindNodeError? Error)> ReseedBlindNodeAsync(Guid nodeId)
    {
        try
        {
            var resp = await http.PostAsync($"/api/blind-nodes/{nodeId}/reseed", null);
            return resp.IsSuccessStatusCode
                ? (true, null)
                : (false, BlindNodeErrors.ForReseed(resp.StatusCode, await resp.Content.ReadAsStringAsync()));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, BlindNodeErrors.AppDidNotAnswer(ex));
        }
    }
}
