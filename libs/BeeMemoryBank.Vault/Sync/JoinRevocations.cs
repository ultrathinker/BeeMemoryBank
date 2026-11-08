using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The joiner's half: each revoked peer of the host becomes a revoked row here (a tombstone at the revoke's version), before the first pull.
/// Without it a joiner knows only the host's active peers, and the first <c>whitelist_add</c> for a revoked peer that reaches it from any other
/// node's log (one older than the revoke) makes an active row of it: the joiner trusts a peer the host revoked, and every node that joins
/// through it after that. The clock is raised past these versions with the rest of the join (<see cref="SnapshotJoin.CompleteAsync"/>), so a
/// re-add made on this node later outranks them.
/// </summary>
public static class JoinRevocations
{
    /// <summary>
    /// The versions a joiner gives a peer of the host's <c>whitelist</c> (<paramref name="row"/>, which this sets): the host's version of each
    /// concern of that row (<see cref="JoinPeerVersions"/>), so an event older than what the host had applied to a field loses to it as it does
    /// on the host, and an event the host never saw is judged field by field as it is there (a field the host holds undated stays undated). A concern the host sends no version for, or one outside what a join accepts, is unversioned (0): a host of 2.7.5 sends
    /// none, and its rows are written at version 0 as before, their fields undated when the row is written (WhitelistEntry.NormalizeVersionsForWrite):
    /// an add that is not a re-add does not change them, and an update or a revoke is judged as before. The row's own version is the newest of them.
    /// </summary>
    public static void Inherit(WhitelistEntry row, JoinPeerVersions? versions)
    {
        var newest = new RowVersion(0, Guid.Empty);
        foreach (var concern in WhitelistConcernSet.Each)
        {
            var sent = versions?.For(concern);
            var version = sent is { LamportTs: > 0 and < SnapshotJoin.MaxAcceptedLamport }
                ? RowVersion.Of(sent.LamportTs, sent.SourceNodeId)
                : new RowVersion(0, Guid.Empty);
            row.SetVersionOf(concern, version);
            if (sent is { Undated: true }) row.MarkUndated(concern);
            if (ConflictResolver.IncomingWins(newest, version)) newest = version;
        }
        row.SetRowVersion(newest);
    }

    /// <returns>The number of revoked rows written.</returns>
    public static async Task<int> ImportAsync(
        IWhitelistRepository whitelist, IReadOnlyCollection<JoinRevokedPeer>? revoked, Guid self, Guid host, ILogger logger)
    {
        var written = 0;
        foreach (var peer in revoked ?? [])
        {
            if (peer.NodeId == Guid.Empty || peer.NodeId == self || peer.NodeId == host) continue;
            // The version is the host's word, held to what a join accepts anywhere else (SnapshotJoin.MaxAcceptedLamport).
            if (peer.LamportTs is < 0 or >= SnapshotJoin.MaxAcceptedLamport)
            {
                logger.LogWarning("Not recording revoked peer {NodeId}: the host sent a version outside what a join accepts.", peer.NodeId);
                continue;
            }
            // A row the same answer made active wins: the host does not send one peer both ways, and this node does not guess which it meant.
            if (await whitelist.GetByNodeIdAsync(peer.NodeId, includeDeleted: true) is not null) continue;

            byte[]? key = null;
            if (peer.Ed25519PublicKeyB64 is { Length: > 0 } b64)
            {
                try { key = Convert.FromBase64String(b64); }
                catch (FormatException) { key = null; }
                if (key is not { Length: CryptoConstants.Ed25519PublicKeySize }) key = null;
            }
            try
            {
                if (await whitelist.CreateTombstoneAsync(peer.NodeId, RowVersion.Of(peer.LamportTs, peer.SourceNodeId), key)) written++;
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException)
            {
                // As for an active peer the answer names (the joiners' own loops): one row that does not go in does not fail the join. If the
                // revoke reaches this node as an event later (from another node's log), it leaves the same row then.
                logger.LogWarning(ex, "Could not record revoked peer {NodeId}", peer.NodeId);
            }
        }
        if (written > 0) logger.LogInformation("Recorded {Count} peer(s) the host has revoked", written);
        return written;
    }
}
