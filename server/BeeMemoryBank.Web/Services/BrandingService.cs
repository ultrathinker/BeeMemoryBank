using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// In-memory mirror of the node's product name (source of truth is the API's
/// tbl_node_identity row), read by the layout on every page render. Same "plain mutable
/// singleton, no locking" reasoning as <see cref="WebSessionSettingsService"/>: a torn read
/// costs one page showing a slightly stale name, which self-corrects on the next request.
/// </summary>
/// <remarks>
/// It refreshes itself through a scope of its own rather than taking an <see cref="ApiClient"/>
/// dependency, so the layout needs a single @inject and no page has to remember to prime it.
/// The name is cached for <see cref="Ttl"/> because the header renders on every single page —
/// an API round-trip per render would be pure overhead for a value that changes about once
/// in the life of an installation.
/// </remarks>
public sealed class BrandingService(IServiceScopeFactory scopeFactory)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    // A failed read is retried far sooner than a successful one. Web can come up seconds before
    // Api does, and caching that first failure for the full TTL would pin every page to the
    // default name for five minutes after a restart that actually recovered immediately.
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(15);

    // The layout awaits this on every render, and the API HttpClient carries a 30-minute timeout.
    // A hung (not refused) API would otherwise stall page rendering for half an hour, including the
    // login page, which had no API dependency at all before this cache existed.
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(3);

    private string _name = Branding.DefaultName;
    private DateTime _expiresAt = DateTime.MinValue;

    /// <summary>Last known name without touching the API — for callers that must not await.</summary>
    public string CurrentName => _name;

    public async Task<string> GetNameAsync()
    {
        if (DateTime.UtcNow < _expiresAt) return _name;

        // Claim the refresh slot before making the call, not after it returns: otherwise every
        // request arriving while the first one is still in flight starts a call of its own.
        _expiresAt = DateTime.UtcNow.Add(FailureTtl);

        var loaded = false;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var api = scope.ServiceProvider.GetRequiredService<ApiClient>();
            using var cts = new CancellationTokenSource(RefreshTimeout);
            var branding = await api.GetBrandingAsync(cts.Token);
            if (branding != null)
            {
                _name = branding.Name;
                loaded = true;
            }
        }
        catch
        {
            // API down or still starting: keep showing the last known name (or the default) and
            // retry shortly. A header must never be able to fail a page render.
        }

        _expiresAt = DateTime.UtcNow.Add(loaded ? Ttl : FailureTtl);
        return _name;
    }

    /// <summary>
    /// Called right after an admin saves, so the change is visible on the very next page instead
    /// of up to <see cref="Ttl"/> later.
    /// </summary>
    public void Set(string name)
    {
        _name = string.IsNullOrWhiteSpace(name) ? Branding.DefaultName : name;
        _expiresAt = DateTime.UtcNow.Add(Ttl);
    }
}
