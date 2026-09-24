using System.Net.Http.Json;
using BeeMemoryBank.Web.Models;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    // The role CRUD + folder-rule methods went with their proxy routes into ProxyRouteTable; only
    // the roles LIST (rendered server-side by the Admin page) still flows through ApiClient.

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
}
