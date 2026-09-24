using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    public async Task<List<object>?> ListRemoteAccountsAsync()
    {
        try
        {
            var resp = await http.GetAsync("/api/remote-accounts/");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<object>>(JsonOpts);
        }
        catch { return null; }
    }

    public async Task<(bool ok, int status, object? body, string? error)> ListAccessibleRemoteFoldersAsync(Guid id)
    {
        var resp = await http.GetAsync($"/api/remote-accounts/{id}/accessible");
        if (resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadFromJsonAsync<object>(JsonOpts);
            return (true, 200, body, null);
        }
        return (false, (int)resp.StatusCode, null, await ReadErrorAsync(resp));
    }

    public async Task<List<object>?> ListRemoteSubscriptionsAsync(Guid id)
    {
        try
        {
            var resp = await http.GetAsync($"/api/remote-accounts/{id}/subscriptions");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<object>>(JsonOpts);
        }
        catch { return null; }
    }

}
