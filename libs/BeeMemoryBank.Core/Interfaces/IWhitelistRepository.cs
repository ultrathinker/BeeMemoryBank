using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IWhitelistRepository
{
    Task<WhitelistEntry?> GetByNodeIdAsync(Guid nodeId, bool includeDeleted = false);
    Task<List<WhitelistEntry>> GetAllActiveAsync();

    /// <summary>
    /// The revoked rows, tombstones included (<see cref="CreateTombstoneAsync"/>): what the host of a join hands the joiner so it
    /// refuses the adds those revokes beat. Never a list of peers: nothing that dials, shows or counts peers reads it.
    /// </summary>
    Task<List<WhitelistEntry>> GetAllRevokedAsync();

    Task<bool> GetAutoAcceptRestoreAsync(string nodeId);
    Task SetAutoAcceptRestoreAsync(string nodeId, bool autoAccept);
    Task<bool> GetAutoAcceptDekRotationAsync(string nodeId);
    Task SetAutoAcceptDekRotationAsync(string nodeId, bool autoAccept);
    /// <summary>
    /// Inserts the row, including the <see cref="WhitelistEntry.LamportTs"/> /
    /// <see cref="WhitelistEntry.SourceNodeId"/> the caller set on <paramref name="entry"/>.
    /// A caller that leaves them unset writes an unversioned row, which loses every later
    /// comparison — correct for a bootstrap row written before any event exists (join, setup),
    /// wrong for anything that also emits a whitelist event, which must stamp what it published.
    /// </summary>
    Task CreateAsync(WhitelistEntry entry);

    /// <summary>
    /// Writes the row as <paramref name="entry"/> has it, its versions included (see <see cref="CreateAsync"/>; the row's and each concern's,
    /// <see cref="WhitelistEntry.VersionOf"/>). A row written active loses its <c>deleted_at</c>: a revoked row (a tombstone too) that is added
    /// again is not "deleted" to the code that reads that column; a row written revoked keeps the one it has, or takes the entity's.
    /// A writer that set only the row's version writes the row whole at it (<see cref="WhitelistEntry.NormalizeVersionsForWrite"/>).
    /// </summary>
    Task UpdateAsync(WhitelistEntry entry);

    /// <summary>
    /// Marks the peer revoked and stamps the version of the revoke onto the row: its membership version (migration 045) and the row's own. For a
    /// revoke made on this node, whose version is newer than every version on the row.
    ///
    /// <para>
    /// The version is required rather than optional because this row is what a later
    /// <c>whitelist_add</c> is compared against. Leaving it at the version of some earlier rename
    /// would let an add issued BEFORE the revoke win that comparison and put a revoked peer back
    /// into the mesh.
    /// </para>
    /// </summary>
    Task RevokeAsync(Guid nodeId, RowVersion version);

    /// <summary>
    /// A revoked row for a node this node has no row for (a tombstone): status <c>R</c> at <paramref name="version"/> (its membership; the other
    /// concerns unversioned, so the first add or update that names them fills them in), no name, no
    /// address, no authority, and the key when the caller knows it (else an empty one, filled by the first <c>whitelist_add</c>
    /// that names the node). It exists so a <c>whitelist_add</c> older than the revoke, arriving after it, is judged against the
    /// revoke and refused, as on a node that saw them in the other order. Like any revoked row it is never a peer: every read of
    /// peers asks for active rows. False when a row for the id is there already (nothing is written then).
    /// </summary>
    Task<bool> CreateTombstoneAsync(Guid nodeId, RowVersion version, byte[]? publicKey = null);

    /// <summary>
    /// Writes only the version columns (the row's and its membership's: the version of a revoke), leaving every other column — including
    /// <c>updated_at</c> and <c>deleted_at</c> — exactly as they are.
    ///
    /// <para>
    /// For the backfill that emits the missing <c>whitelist_revoke</c> event for a row revoked
    /// before this node logged such events. That row still needs the version of the event now being
    /// published, or a stale add beats it — but re-running the revoke to get it would stamp
    /// <c>deleted_at</c> with today's date for a revocation that happened months ago.
    /// </para>
    /// </summary>
    Task SetVersionAsync(Guid nodeId, RowVersion version);

    /// <summary>
    /// Records the sync protocol an authenticated peer just declared (see
    /// <see cref="WhitelistEntry.LastProtocolVersion"/>). Touches nothing else on the row, the
    /// version columns included: this is an observation, not a replicated change.
    /// </summary>
    Task RecordProtocolVersionAsync(Guid nodeId, int protocolVersion, DateTime seenAt);

    /// <summary>
    /// Records the capabilities and application version a peer just showed (see <see cref="WhitelistEntry.LastCapabilities"/>,
    /// docs/design/step-a-spec.md 5.2). An observation like <see cref="RecordProtocolVersionAsync"/>: nothing replicated.
    /// </summary>
    Task RecordCapabilitiesAsync(Guid nodeId, IReadOnlyCollection<string> capabilities, string? appVersion, DateTime seenAt);

    /// <summary>
    /// A removed node is added again: its row starts over as far as this node's own bookkeeping goes (<c>created_at</c>,
    /// <c>deleted_at</c>, the protocol it last declared and when), so the alarms judge it from now and not from the first
    /// pairing. Touches no version column, so nothing is replicated.
    /// </summary>
    Task ResetLocalBookkeepingAsync(Guid nodeId, DateTime now);

    /// <summary>
    /// Gives an ACTIVE row the TLS mode and key a trusted node reported for it, but only when the row has neither (<c>tls_trust</c> and
    /// <c>tls_spki</c> both empty) AND nothing ever wrote its TLS trust on purpose: the TLS concern is still undated (<see cref="WhitelistEntry.UndatedFields"/>,
    /// migration 045: what a join, a restore or an older build left, whose writer nobody knows). Empty columns alone are not enough: a whitelist event that
    /// turned the setting off (<c>tls_trust: none</c>) stores the same empty columns, with the concern dated by that event, and must stay off.
    /// The node the key comes from (<paramref name="trustedResponder"/>) must be an active superadmin row at the moment of the write, and not a blind node.
    /// One conditional statement for all of it, so it cannot overwrite what a whitelist event wrote a moment before, nor write after the responder lost its
    /// authority, and a mode or key that is there (a pin, <c>public-ca</c>, a mode this build does not know) is never changed. Local bookkeeping like
    /// <see cref="RecordProtocolVersionAsync"/>: it writes no event and touches no version column, the row's and each concern's (the concern stays undated),
    /// so any later whitelist event still wins over it as it would over an empty value. True when this call wrote the row.
    /// </summary>
    Task<bool> FillMissingTlsTrustAsync(Guid nodeId, string trust, string tlsSpki, Guid trustedResponder);
}
