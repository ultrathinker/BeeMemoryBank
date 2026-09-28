using System.Net.Http.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// This node's standing as its peers see it (plan 4.2, 5.5): <c>GET /api/sync/my-standing</c> on every peer
/// with an address, over the authenticated, pinned sync client — the question the blind pre-flight asks
/// before a PC adds a blind node. Superadmin-only work this node starts itself (anchors, retiring other
/// nodes' boxes) goes ahead only when at least one peer answered and every peer that answered sees this
/// node as a superadmin: a receiver that does not would refuse the event. Peers that do not answer are
/// left out, as in the pre-flight; they defer such events until they learn of a promotion.
/// </summary>
public sealed class PeerOwnStanding(
    IServiceScopeFactory scopes, IHttpClientFactory httpFactory, TimeProvider time, ILogger<PeerOwnStanding> logger)
    : IOwnStandingProvider
{
    /// <summary>A promotion or demotion is seen this long after it happened, at the latest.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (bool Superadmin, DateTimeOffset At)? _cached;

    public async Task<bool> IsSuperadminAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } c && time.GetUtcNow() - c.At < CacheFor) return c.Superadmin;
            var answer = await AskPeersAsync(ct);
            _cached = (answer, time.GetUtcNow());
            return answer;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> AskPeersAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var self = await sp.GetRequiredService<INodeIdentityRepository>().GetAsync();
        if (self == null) return false;
        var signer = sp.GetRequiredService<INodeAuthSigner>();
        var peers = (await sp.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync())
            .Where(p => !string.IsNullOrEmpty(p.ApiAddress)).ToList();
        var http = httpFactory.CreateClient(SyncScheduler.HttpClientName);

        var answered = 0;
        foreach (var peer in peers)
        {
            MyStanding? standing;
            try
            {
                var baseUrl = peer.ApiAddress!.TrimEnd('/');
                var token = await PeerAuthenticator.AuthenticateAsync(signer, http, baseUrl, self, peer.NodeId, ct);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/sync/my-standing");
                req.Headers.Authorization = new("Bearer", token);
                using var resp = await http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                standing = await resp.Content.ReadFromJsonAsync<MyStanding>(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException
                                           or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
            {
                logger.LogDebug(ex, "my-standing from {Peer} not available", peer.DisplayName);
                continue;
            }
            if (standing == null || standing.ResponderNodeId != peer.NodeId) continue;
            if (!standing.CallerIsSuperadmin)
            {
                logger.LogInformation("{Peer} does not see this node as superadmin; superadmin-only events are not published", peer.DisplayName);
                return false;
            }
            answered++;
        }
        return answered > 0;
    }
}
