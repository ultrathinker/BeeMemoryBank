using System.Net.Http.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Api.Services;

public sealed class BlindPreflightFailedException(IReadOnlyList<string> problems)
    : InvalidOperationException("This PC cannot add a blind node yet: " + string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// What the PC checks before it adds a blind node (plan 3.1, 4.2): that every reachable full peer
/// sees it as superadmin — the whitelist_add is superadmin-only and each receiver judges by its own
/// whitelist — and that no active full node is still on a protocol that knows nothing of blind
/// nodes. Each problem names the node and says what to do.
/// </summary>
public sealed class BlindPreflight(
    INodeIdentityRepository nodeRepo,
    IWhitelistRepository whitelist,
    INodeAuthSigner signer,
    TimeProvider time)
{
    /// <summary>
    /// A full node nobody has seen declare a protocol is tolerated this long after it was added —
    /// a new phone that has not synced yet — and blocks afterwards (plan 3.1, "unknown for N days").
    /// </summary>
    public static readonly TimeSpan UnknownProtocolGrace = TimeSpan.FromDays(7);

    public async Task RunAsync(HttpClient http, CancellationToken ct)
    {
        var self = await nodeRepo.GetAsync() ?? throw new InvalidOperationException("Node is not initialized.");
        var peers = (await whitelist.GetAllActiveAsync()).Where(p => !BlindNodeId.IsBlind(p.NodeId)).ToList();
        var problems = new List<string>();

        // The best protocol anyone has seen from each full node — ours first, then every peer's view.
        var seen = peers.ToDictionary(p => p.NodeId, p => p.LastProtocolVersion);
        void See(Guid id, int? version)
        {
            if (version is { } v && seen.TryGetValue(id, out var best) && (best is null || v > best)) seen[id] = v;
        }

        foreach (var peer in peers.Where(p => !string.IsNullOrEmpty(p.ApiAddress)))
        {
            // Plan 4.2 asks the peers that are reachable. One that is not cannot receive the
            // whitelist_add either until it comes back — and then the deferred superadmin check
            // lets the add wait for a promotion instead of dropping it.
            var answer = await AskAsync(http, self, peer, ct);
            if (!answer.Reached) continue;
            if (answer.Standing is not { } standing)
            {
                problems.Add($"\"{peer.DisplayName}\" refused this PC (or answered something unreadable): check that this PC " +
                             $"is an active peer there and that \"{peer.DisplayName}\" runs a current build.");
                continue;
            }

            See(peer.NodeId, standing.Protocol);
            foreach (var view in standing.Peers) See(view.NodeId, view.LastProtocolVersion);
            if (!standing.CallerIsSuperadmin)
                problems.Add($"\"{peer.DisplayName}\" does not see this PC as superadmin: promote this PC on \"{peer.DisplayName}\" " +
                             "(Admin → Nodes → superadmin) and try again.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        foreach (var peer in peers)
        {
            switch (seen[peer.NodeId])
            {
                case { } v when v < 3:
                    problems.Add($"\"{peer.DisplayName}\" runs sync protocol {v}: update it first — an older node would accept " +
                                 "the blind node's events and could hand it the master key.");
                    break;
                case null when now - peer.CreatedAt > UnknownProtocolGrace:
                    problems.Add($"\"{peer.DisplayName}\" has not synced with this build for over {UnknownProtocolGrace.TotalDays:0} days: " +
                                 "update it or remove it from the network first.");
                    break;
            }
        }

        if (problems.Count > 0)
            throw new BlindPreflightFailedException(problems);
    }

    /// <summary>
    /// Whether the network verifiably sees this node as superadmin: at least one full peer answered,
    /// and every one that answered says so. Nothing local can say it — a node keeps no whitelist row
    /// for itself — so without an answer the answer is no (review L-merge #2: a replica must not make
    /// an ordinary node the blind node's reseed authority).
    /// </summary>
    public async Task<bool> IsSuperadminInNetworkAsync(HttpClient http, CancellationToken ct)
    {
        var self = await nodeRepo.GetAsync() ?? throw new InvalidOperationException("Node is not initialized.");
        var answered = 0;
        foreach (var peer in (await whitelist.GetAllActiveAsync())
                     .Where(p => !BlindNodeId.IsBlind(p.NodeId) && !string.IsNullOrEmpty(p.ApiAddress)))
        {
            var answer = await AskAsync(http, self, peer, ct);
            if (!answer.Reached) continue;
            // A peer that answered at all counts — a refusal (401/403/426) or an unreadable answer is a
            // "no", never "offline" (review L-merge round 2 #2).
            if (answer.Standing is not { CallerIsSuperadmin: true }) return false;
            answered++;
        }
        return answered > 0;
    }

    /// <summary>
    /// How <paramref name="peer"/> sees this node. <c>Reached</c> is false only when no HTTP answer came
    /// back at all (a transport failure or a timeout). Any answer counts as reached: a refusal of the
    /// handshake or of my-standing (401, 403, 426…) and an unreadable body come back with no standing,
    /// which every caller reads as "no".
    /// </summary>
    private async Task<(bool Reached, MyStanding? Standing)> AskAsync(
        HttpClient http, NodeIdentity self, WhitelistEntry peer, CancellationToken ct)
    {
        try
        {
            var baseUrl = peer.ApiAddress!.TrimEnd('/');
            var token = await PeerAuthenticator.AuthenticateAsync(signer, http, baseUrl, self, peer.NodeId, ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/sync/my-standing");
            req.Headers.Authorization = new("Bearer", token);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return (true, null);
            return (true, await resp.Content.ReadFromJsonAsync<MyStanding>(ct));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            // The handshake's own EnsureSuccessStatusCode: the peer answered, and refused.
            return (true, null);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            return (true, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (false, null);
        }
    }
}
