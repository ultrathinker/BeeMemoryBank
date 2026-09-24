using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    // ─── Keys ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Changes THIS node's master password and returns what the API says actually happened.
    ///
    /// <para>Named "Master" rather than plain "ChangePassword" so it is not confused with the
    /// per-user password routes under /api/users, which live in the same file and mean something
    /// entirely different. It returns the message, not a bare bool, because the message is the
    /// point: key slots are node-local, so a successful change leaves every peer still accepting
    /// the old password — including at its own join endpoint — and the API names how many.</para>
    /// </summary>
    public async Task<(bool Ok, string? Message)> ChangeMasterPasswordAsync(string oldPassword, string newPassword)
    {
        var resp = await http.PostAsync("/api/keys/change-password",
            Body(new { oldPassword, newPassword }));
        if (!resp.IsSuccessStatusCode)
            return (false, null);

        try
        {
            var body = await resp.Content.ReadFromJsonAsync<ChangeMasterPasswordDto>(JsonOpts);
            return (true, body?.Message);
        }
        catch
        {
            // Changed successfully but the body did not parse. Do not turn a password change that
            // actually happened into a reported failure over a response shape.
            return (true, null);
        }
    }

    // ─── Whitelist (sync nodes) ───────────────────────────────────────────────

    /// <summary>Promote or demote a peer. Every joined node starts as a superadmin.</summary>
    public async Task<(bool Ok, string? Error)> SetPeerSuperadminAsync(Guid nodeId, bool isSuperadmin)
    {
        var resp = await http.PutAsJsonAsync($"/api/whitelist/{nodeId}/superadmin",
            new { isSuperadmin });
        return resp.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(resp));
    }

    /// <summary>Has the master password been changed on another node while this one kept its slot?</summary>
    public async Task<MasterPasswordNoticeDto?> GetMasterPasswordNoticeAsync() =>
        await http.GetFromJsonAsync<MasterPasswordNoticeDto>("/api/keys/password-notice", JsonOpts);

    public async Task<List<WhitelistEntryDto>?> GetWhitelistAsync() =>
        await http.GetFromJsonAsync<List<WhitelistEntryDto>>("/api/whitelist", JsonOpts);

    public async Task<bool> RevokeNodeAsync(Guid nodeId)
    {
        var resp = await http.DeleteAsync($"/api/whitelist/{nodeId}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<(bool ok, string? error)> ChangeNodeAddressAsync(Guid nodeId, string newApiAddress, string password)
    {
        var resp = await http.PutAsync($"/api/whitelist/{nodeId}/address",
            Body(new { newApiAddress, password }));
        if (resp.IsSuccessStatusCode) return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        return (false, body);
    }

    public async Task<(bool ok, string? error)> SetAutoAcceptRestoreAsync(Guid nodeId, bool autoAccept)
    {
        var resp = await http.PutAsync($"/api/whitelist/{nodeId}/auto-accept-restore",
            Body(new { autoAccept }));
        if (resp.IsSuccessStatusCode) return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        return (false, body);
    }

    public async Task<(bool ok, string? error)> SetAutoAcceptDekRotationAsync(Guid nodeId, bool autoAccept)
    {
        var resp = await http.PutAsync($"/api/whitelist/{nodeId}/auto-accept-dek-rotation",
            Body(new { autoAccept }));
        if (resp.IsSuccessStatusCode) return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        return (false, body);
    }

    public async Task<NodeIdentityDto?> GetIdentityAsync() =>
        await http.GetFromJsonAsync<NodeIdentityDto>("/api/sync/identity", JsonOpts);

    public async Task<Dictionary<Guid, DateTime>?> GetNodeSyncStatusAsync()
    {
        try
        {
            var list = await http.GetFromJsonAsync<List<SyncStatusEntry>>("/api/whitelist/sync-status", JsonOpts);
            return list?.ToDictionary(e => e.NodeId, e => e.UpdatedAt);
        }
        catch { return null; }
    }

    private record SyncStatusEntry(Guid NodeId, DateTime UpdatedAt);

    // ─── Agents ───────────────────────────────────────────────────────────────

    /// <param name="all">
    /// Superadmin-only: include every user's agents (the Admin page's agent
    /// table). Left false everywhere else so a page only ever shows the signed-in
    /// user their own agents.
    /// </param>
    public async Task<List<AgentDto>?> GetAgentsAsync(bool all = false) =>
        await http.GetFromJsonAsync<List<AgentDto>>($"/api/agents?all={(all ? "true" : "false")}", JsonOpts);

    public async Task<bool> DeleteAgentAsync(int id)
    {
        var resp = await http.DeleteAsync($"/api/agents/{id}");
        return resp.IsSuccessStatusCode;
    }

    // ─── Users ────────────────────────────────────────────────────────────────

    public async Task<List<UserDto>?> GetUsersAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/users");

        var resp = await http.SendAsync(request);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<List<UserDto>>(JsonOpts);
    }

    public async Task<(UserDto? User, string? Error, int StatusCode)> CreateUserAsync(string username, string displayName, string password, string role, bool chatAccess = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users")
        {
            Content = Body(new { username, displayName, password, role, chatAccess })
        };

        var resp = await http.SendAsync(request);
        if (resp.IsSuccessStatusCode)
        {
            var user = await resp.Content.ReadFromJsonAsync<UserDto>(JsonOpts);
            return (user, null, (int)resp.StatusCode);
        }
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(JsonOpts);
            return (null, err?.Error ?? "Failed to create user", (int)resp.StatusCode);
        }
        catch { return (null, "Failed to create user", (int)resp.StatusCode); }
    }

    public async Task<(bool Ok, string? Error, int StatusCode)> DeleteUserAsync(int id)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/users/{id}");
        var resp = await http.SendAsync(request);
        if (resp.IsSuccessStatusCode) return (true, null, (int)resp.StatusCode);
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(JsonOpts);
            return (false, err?.Error ?? "Failed to delete user", (int)resp.StatusCode);
        }
        catch { return (false, "Failed to delete user", (int)resp.StatusCode); }
    }

    public async Task<(bool Ok, string? Error)> ChangeOwnPasswordAsync(string oldPassword, string newPassword)
    {
        var resp = await http.PostAsync("/api/users/me/change-password",
            Body(new { oldPassword, newPassword }));
        if (resp.IsSuccessStatusCode) return (true, null);
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(JsonOpts);
            return (false, err?.Error ?? "Failed to change password");
        }
        catch { return (false, "Failed to change password"); }
    }

    public async Task<(bool Ok, string? Error, int StatusCode)> ChangeUserPasswordAsync(int id, string newPassword)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/users/{id}/change-password")
        {
            Content = Body(new { newPassword })
        };

        var resp = await http.SendAsync(request);
        if (resp.IsSuccessStatusCode) return (true, null, (int)resp.StatusCode);
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(JsonOpts);
            return (false, err?.Error ?? "Failed to change password", (int)resp.StatusCode);
        }
        catch { return (false, "Failed to change password", (int)resp.StatusCode); }
    }
}
