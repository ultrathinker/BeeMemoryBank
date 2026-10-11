using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;
using Dapper;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// <see cref="IReplicaProducerAuthority"/> of a full node: asks its full peers whether this node is a superadmin in the network, with the
/// same named HTTP client the replica handler always used. Full-node code (it depends on <see cref="BlindPreflight"/>, the PC side of adding
/// a blind node); a blind node does not contain it.
///
/// <para>One case no peer can answer (docs/design/step-a-spec.md 8.6): a node with no full peer at all, the only computer of its network, which
/// serves its own phones as a hub. It has nobody to ask, yet a copy installing the baseline it produced needs the producer to be a superadmin
/// in the package. Such a node says yes when it authored the metadata activation: only a superadmin can (<c>POST /api/sync/activation</c> is
/// superadmin-only, and every receiver applies <c>metadata_activation</c> as a superadmin-only event). A node that has peers still asks them,
/// and one whose peers do not answer is still not trusted for that.</para>
/// </summary>
public sealed class BlindPreflightReplicaAuthority(
    BlindPreflight preflight, IHttpClientFactory httpClients, INodeIdentityRepository nodeRepo, IWhitelistRepository whitelist,
    IDbConnectionFactory db) : IReplicaProducerAuthority
{
    public async Task<bool> IsSuperadminAsync(CancellationToken ct)
    {
        using var http = httpClients.CreateClient("SyncScheduler");
        if (await preflight.IsSuperadminInNetworkAsync(http, ct)) return true;
        return await ActivatedItselfWithoutPeersAsync();
    }

    private async Task<bool> ActivatedItselfWithoutPeersAsync()
    {
        if ((await whitelist.GetAllActiveAsync()).Any(p => !BlindNodeId.IsBlind(p.NodeId))) return false;
        var self = await nodeRepo.GetAsync();
        if (self is null) return false;
        using var conn = db.CreateConnection();
        var author = await conn.ExecuteScalarAsync<string?>("SELECT author_node_id FROM tbl_metadata_activation LIMIT 1");
        return Guid.TryParse(author, out var authorId) && authorId == self.NodeId;
    }
}
