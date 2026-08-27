using System.Net.Http.Json;
using BeeMemoryBank.Web.Models;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    // Every method here surfaces the API's own error text and status code rather than collapsing
    // failures into null. Role management refuses things for reasons the operator has to read —
    // "that name is reserved", "3 users still have this role", "superadmins bypass folder rules"
    // — and a bare 502 hides all of them.

    public async Task<List<RoleDto>?> GetRolesAsync()
    {
        try
        {
            var resp = await http.GetAsync("/api/roles");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<RoleDto>>(JsonOpts);
        }
        catch { return null; }
    }

    public async Task<(RoleDto? Role, string? Error, int StatusCode)> CreateRoleAsync(
        string name, string displayName, string? description, string basePolicy)
    {
        var resp = await http.PostAsync("/api/roles", Body(new { name, displayName, description, basePolicy }));
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<RoleDto>(JsonOpts), null, (int)resp.StatusCode);
        return (null, await ReadErrorAsync(resp, "Failed to create role"), (int)resp.StatusCode);
    }

    public async Task<(bool Ok, string? Error, int StatusCode)> UpdateRoleAsync(
        string name, string displayName, string? description, string basePolicy)
    {
        var resp = await http.PutAsync($"/api/roles/{Uri.EscapeDataString(name)}",
            Body(new { displayName, description, basePolicy }));
        if (resp.IsSuccessStatusCode) return (true, null, (int)resp.StatusCode);
        return (false, await ReadErrorAsync(resp, "Failed to update role"), (int)resp.StatusCode);
    }

    public async Task<(bool Ok, string? Error, int StatusCode)> DeleteRoleAsync(string name)
    {
        var resp = await http.DeleteAsync($"/api/roles/{Uri.EscapeDataString(name)}");
        if (resp.IsSuccessStatusCode) return (true, null, (int)resp.StatusCode);
        return (false, await ReadErrorAsync(resp, "Failed to delete role"), (int)resp.StatusCode);
    }

    // ─── Role folder rules ──────────────────────────────────────────────────

    public async Task<List<RoleAclEntryDto>?> GetRoleRestrictionsAsync(string roleName)
    {
        try
        {
            var resp = await http.GetAsync($"/api/restrictions/role/{Uri.EscapeDataString(roleName)}");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<RoleAclEntryDto>>(JsonOpts);
        }
        catch { return null; }
    }

    public async Task<(RoleAclEntryDto? Entry, string? Error, int StatusCode)> AddRoleRestrictionAsync(
        string roleName, Guid folderId, string effect, bool isReadOnly)
    {
        var resp = await http.PostAsync($"/api/restrictions/role/{Uri.EscapeDataString(roleName)}",
            Body(new { folderId, effect, isReadOnly }));
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<RoleAclEntryDto>(JsonOpts), null, (int)resp.StatusCode);
        return (null, await ReadErrorAsync(resp, "Failed to add rule"), (int)resp.StatusCode);
    }

    public async Task<(bool Ok, string? Error, int StatusCode)> SetRoleRestrictionReadOnlyAsync(
        string roleName, Guid folderId, bool isReadOnly)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch,
            $"/api/restrictions/role/{Uri.EscapeDataString(roleName)}/{folderId}")
        {
            Content = Body(new { isReadOnly })
        };
        var resp = await http.SendAsync(req);
        if (resp.IsSuccessStatusCode) return (true, null, (int)resp.StatusCode);
        return (false, await ReadErrorAsync(resp, "Failed to update rule"), (int)resp.StatusCode);
    }

    public async Task<(bool Ok, string? Error, int StatusCode)> RemoveRoleRestrictionAsync(string roleName, Guid folderId)
    {
        var resp = await http.DeleteAsync($"/api/restrictions/role/{Uri.EscapeDataString(roleName)}/{folderId}");
        if (resp.IsSuccessStatusCode) return (true, null, (int)resp.StatusCode);
        return (false, await ReadErrorAsync(resp, "Failed to remove rule"), (int)resp.StatusCode);
    }

    private async Task<string> ReadErrorAsync(HttpResponseMessage resp, string fallback)
    {
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(JsonOpts);
            return string.IsNullOrWhiteSpace(err?.Error) ? fallback : err!.Error!;
        }
        catch { return fallback; }
    }
}
