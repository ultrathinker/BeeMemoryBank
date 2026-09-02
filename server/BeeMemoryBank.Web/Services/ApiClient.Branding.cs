using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Web.Models;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    /// <param name="ct">
    /// Bounds the call independently of the client's own (very long) timeout — the page header
    /// waits on this, so it must fail fast rather than hang a render.
    /// </param>
    public async Task<BrandingDto?> GetBrandingAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<BrandingDto>("/api/branding", JsonOpts, ct);
        }
        catch
        {
            return null; // caller falls back to the built-in name
        }
    }

    public async Task<(bool Ok, int Status, string? Error, BrandingDto? Result)> SetBrandingAsync(string? name)
    {
        try
        {
            var resp = await http.PutAsJsonAsync("/api/branding", new { name });
            if (resp.IsSuccessStatusCode)
                return (true, (int)resp.StatusCode, null, await resp.Content.ReadFromJsonAsync<BrandingDto>(JsonOpts));

            return (false, (int)resp.StatusCode,
                await ReadErrorAsync(resp) ?? $"Request failed ({(int)resp.StatusCode})", null);
        }
        catch
        {
            return (false, (int)HttpStatusCode.BadGateway, "The API is unreachable.", null);
        }
    }
}
