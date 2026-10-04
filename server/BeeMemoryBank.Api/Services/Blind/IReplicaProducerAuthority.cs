namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Whether this node may say "superadmin" about itself in the replica package it hands to a blind node. Only a full node can know
/// it (it asks its full peers; <c>BlindPreflightReplicaAuthority</c>, full-node code). A blind node never is a superadmin and registers
/// none, so <c>GET /api/blind/replica</c> then answers false without consulting anyone.
/// </summary>
public interface IReplicaProducerAuthority
{
    Task<bool> IsSuperadminAsync(CancellationToken ct);
}
