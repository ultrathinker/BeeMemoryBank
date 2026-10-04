namespace BeeMemoryBank.Api.Services;

/// <summary>
/// <see cref="IReplicaProducerAuthority"/> of a full node: asks its full peers whether this node is a superadmin in the network, with the
/// same named HTTP client the replica handler always used. Full-node code (it depends on <see cref="BlindPreflight"/>, the PC side of adding
/// a blind node); a blind node does not contain it.
/// </summary>
public sealed class BlindPreflightReplicaAuthority(BlindPreflight preflight, IHttpClientFactory httpClients) : IReplicaProducerAuthority
{
    public async Task<bool> IsSuperadminAsync(CancellationToken ct)
    {
        using var http = httpClients.CreateClient("SyncScheduler");
        return await preflight.IsSuperadminInNetworkAsync(http, ct);
    }
}
