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

    // ── invitations to a blind copy (release 2.7) ─────────────────────────

    public async Task<List<BlindInviteDto>?> ListBlindInvitesAsync()
    {
        try
        {
            var resp = await http.GetAsync("/api/blind-nodes/invites/");
            return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<List<BlindInviteDto>>(JsonOpts) : null;
        }
        catch { return null; }
    }

    public async Task<BlindInviteDefaultsDto?> GetBlindInviteDefaultsAsync()
    {
        try
        {
            var resp = await http.GetAsync("/api/blind-nodes/invites/defaults");
            return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<BlindInviteDefaultsDto>(JsonOpts) : null;
        }
        catch { return null; }
    }

    /// <summary>Makes an invitation. The error is the Api's own sentence (it names what to fix: the address, the lifetime, a certificate).</summary>
    public async Task<(BlindInviteIssuedDto? Invite, string? Error)> IssueBlindInviteAsync(string? label, string? sourceName, int lifetimeMinutes, string? address)
    {
        try
        {
            var resp = await http.PostAsJsonAsync("/api/blind-nodes/invites/", new { label, sourceName, lifetimeMinutes, address, trust = "auto" }, JsonOpts);
            return resp.IsSuccessStatusCode
                ? (await resp.Content.ReadFromJsonAsync<BlindInviteIssuedDto>(JsonOpts), null)
                : (null, await ErrorSentenceAsync(resp));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, BlindNodeErrors.AppDidNotAnswer(ex).Message);
        }
    }

    /// <summary>Confirms a redemption with the master password; the pre-flight's problems (a device that does not see this PC as superadmin) come back as the sentence.</summary>
    public async Task<(bool Ok, string? Error)> ConfirmBlindInviteAsync(string inviteId, string? password)
    {
        try
        {
            var resp = await http.PostAsJsonAsync($"/api/blind-nodes/invites/{Uri.EscapeDataString(inviteId)}/confirm", new { password }, JsonOpts);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict && BlindNodeErrors.ForAdd(resp.StatusCode, body) is { } preflight && preflight.Items.Count > 0)
                return (false, preflight.Message + " " + string.Join(" ", preflight.Items));
            return (false, SentenceOf(body, resp.StatusCode));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, BlindNodeErrors.AppDidNotAnswer(ex).Message);
        }
    }

    public async Task<(bool Ok, string? Error)> RefuseBlindInviteAsync(string inviteId) =>
        await SendInviteDecisionAsync(HttpMethod.Post, $"/api/blind-nodes/invites/{Uri.EscapeDataString(inviteId)}/refuse");

    public async Task<(bool Ok, string? Error)> WithdrawBlindInviteAsync(string inviteId) =>
        await SendInviteDecisionAsync(HttpMethod.Delete, $"/api/blind-nodes/invites/{Uri.EscapeDataString(inviteId)}");

    private async Task<(bool Ok, string? Error)> SendInviteDecisionAsync(HttpMethod method, string url)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            var resp = await http.SendAsync(request);
            return resp.IsSuccessStatusCode ? (true, null) : (false, await ErrorSentenceAsync(resp));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, BlindNodeErrors.AppDidNotAnswer(ex).Message);
        }
    }

    private static async Task<string> ErrorSentenceAsync(HttpResponseMessage resp) => SentenceOf(await resp.Content.ReadAsStringAsync(), resp.StatusCode);

    private static string SentenceOf(string body, System.Net.HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } sentence)
                return sentence;
        }
        catch (JsonException)
        {
        }
        return $"The node answered {(int)status}.";
    }
}
