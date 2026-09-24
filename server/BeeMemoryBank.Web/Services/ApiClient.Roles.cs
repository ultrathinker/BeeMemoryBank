using System.Net.Http.Json;
using BeeMemoryBank.Web.Models;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    // Role CRUD + folder rules go through ProxyRouteTable from the browser; only the roles LIST
    // (rendered server-side by the Admin page) flows through ApiClient.

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
