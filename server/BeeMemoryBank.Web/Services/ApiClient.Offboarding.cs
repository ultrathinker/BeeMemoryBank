using System.Net.Http.Json;
using BeeMemoryBank.Web.Models;

namespace BeeMemoryBank.Web.Services;

/// <summary>The removal of a superadmin (release 2.12.0): the API's <c>/api/offboarding</c> routes, for the "People being removed" page.</summary>
public partial class ApiClient
{
    public async Task<List<OffboardingDto>?> GetOffboardingsAsync()
    {
        var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/offboarding"));
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<List<OffboardingDto>>(JsonOpts) : null;
    }

    public async Task<OffboardingDto?> GetOffboardingAsync(Guid id)
    {
        var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/offboarding/{id:D}"));
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<OffboardingDto>(JsonOpts) : null;
    }

    public Task<(bool Ok, string? Error)> ContinueOffboardingAsync(Guid id, string? password) =>
        PostOffboardingAsync($"/api/offboarding/{id:D}/continue", new { password });

    public Task<(bool Ok, string? Error)> ResolveOffboardingReviewAsync(Guid id, IReadOnlyList<OffboardingDecisionDto> decisions, bool acceptDefaults, string? password) =>
        PostOffboardingAsync($"/api/offboarding/{id:D}/review", new { decisions, acceptDefaults, password });

    public Task<(bool Ok, string? Error)> RevokeOffboardingNodeAsync(Guid id, Guid nodeId) =>
        PostOffboardingAsync($"/api/offboarding/{id:D}/nodes/{nodeId:D}/revoke", new { });

    public Task<(bool Ok, string? Error)> TakeOverOffboardingAsync(Guid id, string password) =>
        PostOffboardingAsync($"/api/offboarding/{id:D}/take-over", new { password });

    private async Task<(bool Ok, string? Error)> PostOffboardingAsync(string path, object body)
    {
        var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) });
        if (resp.IsSuccessStatusCode) return (true, null);
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(JsonOpts);
            return (false, err?.Error ?? "The removal could not do this now.");
        }
        catch { return (false, "The removal could not do this now."); }
    }
}
