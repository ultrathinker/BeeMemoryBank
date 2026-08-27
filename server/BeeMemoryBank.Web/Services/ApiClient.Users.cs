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

    public async Task<bool> ChangePasswordAsync(string oldPassword, string newPassword)
    {
        var resp = await http.PostAsync("/api/keys/change-password",
            Body(new { oldPassword, newPassword }));
        return resp.IsSuccessStatusCode;
    }

    // ─── Whitelist (sync nodes) ───────────────────────────────────────────────

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

    public async Task<List<AgentDto>?> GetAgentsAsync() =>
        await http.GetFromJsonAsync<List<AgentDto>>("/api/agents", JsonOpts);

    public async Task<AgentCreatedDto?> CreateAgentAsync(string name, string? description)
    {
        var resp = await http.PostAsync("/api/agents", Body(new { name, description }));
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AgentCreatedDto>(JsonOpts);
    }

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

    public async Task<(bool Ok, string? Error, int StatusCode)> UpdateUserAsync(int id, string displayName, string? role, bool? chatAccess = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/users/{id}")
        {
            Content = Body(new { displayName, role, chatAccess })
        };

        var resp = await http.SendAsync(request);
        if (resp.IsSuccessStatusCode) return (true, null, (int)resp.StatusCode);
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(JsonOpts);
            return (false, err?.Error ?? "Failed to update user", (int)resp.StatusCode);
        }
        catch { return (false, "Failed to update user", (int)resp.StatusCode); }
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

    // ─── Folder Restrictions ────────────────────────────────────────────────

    public async Task<List<AclEntryDto>?> GetUserRestrictionsAsync(int userId)
    {
        try
        {
            var resp = await http.GetAsync($"/api/restrictions/user/{userId}");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<AclEntryDto>>(JsonOpts);
        }
        catch { return null; }
    }

    // Returns the API's own message and status: adding a per-user rule is refused with a 409 for
    // a user managed by a custom role, and that reason has to reach the operator.
    public async Task<(AclEntryDto? Entry, string? Error, int StatusCode)> AddUserRestrictionAsync(
        int userId, Guid folderId, string effect, bool isReadOnly = false)
    {
        var resp = await http.PostAsync($"/api/restrictions/user/{userId}", Body(new { folderId, effect, isReadOnly }));
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<AclEntryDto>(JsonOpts), null, (int)resp.StatusCode);
        return (null, await ReadErrorAsync(resp, "Failed to add rule"), (int)resp.StatusCode);
    }

    public async Task<(bool Ok, string? Error, int StatusCode)> SetUserRestrictionReadOnlyAsync(
        int userId, Guid folderId, bool isReadOnly)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/restrictions/user/{userId}/{folderId}")
        {
            Content = Body(new { isReadOnly })
        };
        var resp = await http.SendAsync(req);
        if (resp.IsSuccessStatusCode) return (true, null, (int)resp.StatusCode);
        return (false, await ReadErrorAsync(resp, "Failed to update rule"), (int)resp.StatusCode);
    }

    public async Task<(bool Ok, string? Error, int StatusCode)> RemoveUserRestrictionAsync(int userId, Guid folderId)
    {
        var resp = await http.DeleteAsync($"/api/restrictions/user/{userId}/{folderId}");
        if (resp.IsSuccessStatusCode) return (true, null, (int)resp.StatusCode);
        return (false, await ReadErrorAsync(resp, "Failed to remove rule"), (int)resp.StatusCode);
    }
}
