using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync;

/// <summary>
/// A peer the host of a join has revoked: its node id, the version of the revoke, and its key when the host has one. The join's
/// <c>whitelist</c> carries active peers only, and a joiner of every version makes each of them an active row, so a revoked peer can never
/// travel there; it travels in the join answer's own optional <c>revokedPeers</c>, which a joiner of 2.7.5 or older does not read.
/// </summary>
public sealed record JoinRevokedPeer(Guid NodeId, long LamportTs, Guid? SourceNodeId, string? Ed25519PublicKeyB64 = null)
{
    /// <summary>
    /// The version sent is the row's membership version (migration 045): the revoke's. Not the row's own, which an update applied to the revoked
    /// row since may have raised: a tombstone at that version would refuse a re-add that is newer than the revoke.
    /// </summary>
    public static JoinRevokedPeer From(WhitelistEntry row)
    {
        var revoke = row.VersionOf(WhitelistConcerns.Membership);
        return new(row.NodeId, revoke.LamportTs, revoke.SourceNodeId == Guid.Empty ? null : revoke.SourceNodeId,
            row.Ed25519PublicKey.Length == CryptoConstants.Ed25519PublicKeySize ? Convert.ToBase64String(row.Ed25519PublicKey) : null);
    }
}

/// <summary>
/// One version in a join answer: a Lamport time and the node that wrote it, and whether the host holds the field undated (its writer not known
/// there: <see cref="WhitelistEntry.UndatedFields"/>), which the joiner keeps.
/// </summary>
public sealed record JoinVersion(long LamportTs, Guid? SourceNodeId, bool Undated = false);

/// <summary>
/// The version of each concern of one of the host's active peers (migration 045), in its join answer's <c>whitelist</c> entry as the optional
/// <c>versions</c>: the joiner's row takes them (<see cref="JoinRevocations.Inherit"/>), so it judges every later event about that peer as the
/// host does, field by field. With one version for the whole row the joiner inherited whatever the host had: a demotion the host had dropped
/// for a crossing rename stayed dropped on every node that joined through it. A host of 2.7.5 sends none, and a joiner of 2.7.5 ignores it.
/// </summary>
public sealed record JoinPeerVersions(JoinVersion? Membership, JoinVersion? Superadmin, JoinVersion? Name, JoinVersion? Address, JoinVersion? Tls)
{
    public static JoinPeerVersions From(WhitelistEntry row) => new(
        Of(row, WhitelistConcerns.Membership), Of(row, WhitelistConcerns.Superadmin), Of(row, WhitelistConcerns.Name),
        Of(row, WhitelistConcerns.Address), Of(row, WhitelistConcerns.Tls));

    private static JoinVersion Of(WhitelistEntry row, WhitelistConcerns concern)
    {
        var version = row.VersionOf(concern);
        return new JoinVersion(version.LamportTs, version.SourceNodeId == Guid.Empty ? null : version.SourceNodeId, row.IsUndated(concern));
    }

    /// <summary>The version this answer gives <paramref name="concern"/> (a single flag), or null.</summary>
    public JoinVersion? For(WhitelistConcerns concern) => concern switch
    {
        WhitelistConcerns.Membership => Membership,
        WhitelistConcerns.Superadmin => Superadmin,
        WhitelistConcerns.Name => Name,
        WhitelistConcerns.Address => Address,
        WhitelistConcerns.Tls => Tls,
        _ => null,
    };
}
