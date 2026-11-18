using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Services.Offboarding;

/// <summary>The identity key fingerprint of a node as this node knows it: its own key, or the key a peer's whitelist row holds.</summary>
public static class OffboardingNodeKeys
{
    public static async Task<string?> FingerprintAsync(INodeIdentityRepository nodes, IWhitelistRepository whitelist, Guid nodeId)
    {
        var self = await nodes.GetAsync();
        if (self is not null && self.NodeId == nodeId)
            return NodeStateDraft.IdentityFingerprint(self.Ed25519PublicKey);
        var row = await whitelist.GetByNodeIdAsync(nodeId, includeDeleted: true);
        return row is { Ed25519PublicKey.Length: > 0 } ? NodeStateDraft.IdentityFingerprint(row.Ed25519PublicKey) : null;
    }
}
