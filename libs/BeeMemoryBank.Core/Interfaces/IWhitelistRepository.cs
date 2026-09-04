using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IWhitelistRepository
{
    Task<WhitelistEntry?> GetByNodeIdAsync(Guid nodeId, bool includeDeleted = false);
    Task<List<WhitelistEntry>> GetAllActiveAsync();
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

    /// <inheritdoc cref="CreateAsync"/>
    Task UpdateAsync(WhitelistEntry entry);

    /// <summary>
    /// Marks the peer revoked and stamps the version of the revoke onto the row.
    ///
    /// <para>
    /// The version is required rather than optional because this row is what a later
    /// <c>whitelist_add</c> is compared against. Leaving it at the version of some earlier rename
    /// is how an add issued BEFORE the revoke wins that comparison and puts a revoked peer back
    /// into the mesh.
    /// </para>
    /// </summary>
    Task RevokeAsync(Guid nodeId, RowVersion version);

    /// <summary>
    /// Writes only the version columns, leaving every other column — including
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
}
