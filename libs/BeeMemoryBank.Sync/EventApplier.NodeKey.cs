using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>An event of a node in an identity conflict (plan WS-D D2): refused until a superadmin's confirm settles it, then it applies.</summary>
/// <summary>
/// A <c>node_key_confirm</c> whose rotation is not in this node's log yet: the confirm overtook it on another path.
/// Deferrable: retried once the rotation has landed.
/// </summary>
public sealed class NodeKeyConfirmEarlyException(Guid confirmEventId, Guid rotationEventId)
    : InvalidOperationException($"node_key_confirm {confirmEventId}: the rotation it names ({rotationEventId}) has not arrived yet."), IDeferrableSyncFailure;

public sealed class NodeIdentityConflictException(Guid nodeId)
    : UnauthorizedAccessException($"Node {nodeId} is in an identity conflict: two different identity keys were announced for it."), IDeferrableSyncFailure
{
    public Guid NodeId { get; } = nodeId;
}

/// <summary>
/// The node identity re-key (release 2.12, plan WS-D): <c>node_key_rotated</c>, <c>node_key_confirm</c>, and the signature check of every event
/// through <see cref="NodeEventVerifier"/>.
/// </summary>
public partial class EventApplier
{
    /// <summary>The attention flag a node raises when it holds events of a re-keyed node that the rotation did not acknowledge (plan D3).</summary>
    public const string RetiredKeyAttentionFlag = "retired_key_unacknowledged_events";

    /// <summary>The prefix of the node-local marker (tbl_migration_marker) that holds that flag, by node id.</summary>
    public const string RetiredKeyAttentionMarkerPrefix = "attention-retired-key:";

    /// <summary>
    /// The signature check of <see cref="ApplyCoreAsync"/>: refused for a node in an identity conflict; a rotation and a confirm by the rules of their
    /// own; every other event through <see cref="NodeEventVerifier"/> (live sync: the list rule; a restore replay: the package rule).
    /// </summary>
    private async Task<bool> VerifyOriginatorSignatureAsync(SyncEvent evt, Originator originator, ApplyContext? context)
    {
        using (var conn = connFactory.CreateConnection())
        {
            // While a conflict is open only the competing rotations of the conflicted counter pass (a re-delivery, or one more contender recorded);
            // every other event of the node, its later rotations included, waits for the confirm (deferred, retried). A rotation taken from either
            // side now could re-arm a thief or split the genuine node after the confirm (ADR 0013).
            if (await NodeKeyHistoryStore.GetConflictAsync(conn, null, evt.NodeId) is { IsOpen: true } open
                && !(evt.EventType == EventTypes.NodeKeyRotated && TryParseRotation(evt, out var contender, out _) && contender.Counter == open.Counter))
                throw new NodeIdentityConflictException(evt.NodeId);
        }

        var payload = EventSignature.BuildPayload(evt);
        switch (evt.EventType)
        {
            case EventTypes.NodeKeyConfirm:
                // A confirm is signed with its signer's CURRENT key, never a historical one (plan D2) - when it is received. One this node's log
                // already holds and applies again (an older build stored it without knowing the type) passed that check at
                // receipt: the package rule, so a signer that re-keyed since does not make it fail.
                if (NodeEventVerifier.SignatureVerifies(originator.PublicKey, payload, evt.Signature)) return true;
                return context?.KeyMode == NodeKeyMode.Package
                       && await NodeEventVerifier.VerifyAsync(connFactory, evt, originator.PublicKey, NodeKeyMode.Package);
            case EventTypes.NodeKeyRotated:
                // The key this node holds for the author; or, for a second rotation from the same (old key, counter), the key that rotation
                // retired: the handler then records the conflict.
                if (NodeEventVerifier.SignatureVerifies(originator.PublicKey, payload, evt.Signature)) return true;
                if (TryParseRotation(evt, out var rotation, out _) is false) return false;
                using (var conn = connFactory.CreateConnection())
                {
                    var same = (await NodeKeyHistoryStore.ListAsync(conn, null, evt.NodeId)).FirstOrDefault(h => h.Counter == rotation.Counter);
                    return same is not null && same.PublicKey.AsSpan().SequenceEqual(rotation.Old)
                        && NodeEventVerifier.SignatureVerifies(same.PublicKey, payload, evt.Signature);
                }
        }

        var mode = context?.KeyMode ?? (context?.Originators is not null ? NodeKeyMode.Package : NodeKeyMode.Live);
        if (context?.KeyHistory is { } carried)
            return NodeEventVerifier.Verify(evt, originator.PublicKey, carried, mode)
                   || await NodeEventVerifier.VerifyAsync(connFactory, evt, originator.PublicKey, mode);
        return await NodeEventVerifier.VerifyAsync(connFactory, evt, originator.PublicKey, mode);
    }

    /// <summary>
    /// True when <paramref name="evt"/> is this node's own event: signed by <paramref name="ownCurrentKey"/> or by a key this node retired, up to the
    /// rotation that retired it (plan D3: a node recognises its own echoed events, also below its checkpoint). Used by the sync client.
    /// </summary>
    public Task<bool> IsOwnEchoAsync(SyncEvent evt, byte[] ownCurrentKey) =>
        NodeEventVerifier.VerifyAsync(connFactory, evt, ownCurrentKey, NodeKeyMode.Package);

    /// <summary>
    /// An event naming this node that is not in its log: when it carries a valid signature of ANY key this node ever had -
    /// the current one or a retired one, whatever the rotation's bounds, since only a holder of one of its seeds can make that - and this node cannot
    /// have written it (<see cref="SignedAsThisNode.IsForeignAsync"/>), the "somebody signs as this node" mark is raised. Never throws.
    /// </summary>
    public async Task NoteSignedAsThisNodeAsync(SyncEvent evt, NodeIdentity self)
    {
        try
        {
            if (evt.NodeId != self.NodeId) return;
            var payload = EventSignature.BuildPayload(evt);
            var signed = NodeEventVerifier.SignatureVerifies(self.Ed25519PublicKey, payload, evt.Signature);
            if (!signed)
            {
                using var conn = connFactory.CreateConnection();
                signed = (await NodeKeyHistoryStore.ListAsync(conn, null, self.NodeId))
                    .Any(r => NodeEventVerifier.SignatureVerifies(r.PublicKey, payload, evt.Signature));
            }
            if (!signed || !await SignedAsThisNode.IsForeignAsync(connFactory, evt, self.NodeId)) return;
            if (!await SignedAsThisNode.RecordAsync(connFactory, evt)) return;
            logger.LogError("Event {EventId} ({Type}) is signed with this node's identity key, but this node did not write it: somebody holds a copy of " +
                "this node's key. Give this node a new identity key", evt.EventId, evt.EventType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not check event {EventId} signed as this node ({Type})", evt.EventId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// This node's own <c>node_key_confirm</c> (plan D2): the confirm a superadmin's device issues to settle another node's identity conflict.
    /// The superadmin check is the issuer's (<see cref="NodeIdentityRekeyService"/>).
    /// <para>The confirm is appended to the log first, in the transaction that settles the conflict: either both happen or
    /// neither, so a confirm this node settled is always one it will send. With no open conflict to settle it throws and logs nothing.</para>
    /// </summary>
    public Task ApplyOwnNodeKeyConfirmAsync(SyncEvent evt) => evt.EventType == EventTypes.NodeKeyConfirm
        ? ApplyNodeKeyConfirmAsync(evt, own: true)
        : throw new ArgumentException($"{evt.EventType} is not a node key confirm.", nameof(evt));

    /// <summary>The newest links of this node's key transition chain (plan D4), oldest first: what the sync client sends when it authenticates.</summary>
    public async Task<IReadOnlyList<NodeKeyLink>> OwnKeyChainAsync(Guid ownNodeId)
    {
        try { return await new NodeKeyHistoryStore(connFactory).ChainAsync(ownNodeId); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without the chain a peer that already applied the re-key lets this node in anyway; one that has not refuses it, as before.
            logger.LogDebug(ex, "Could not read this node's key transition chain");
            return [];
        }
    }

    /// <summary>The rotations whose fresh envelope is to be fetched from <paramref name="peer"/> (plan D4; for the sync client).</summary>
    public Task<IReadOnlyList<Guid>> EnvelopeRequestsFromAsync(Guid peer) => DekRotation.DekEnvelopeRefresh.RequestedFromAsync(connFactory, peer);

    /// <summary>Keeps a fetched envelope for the retried rotation (plan D4; for the sync client).</summary>
    public Task StoreRefreshedEnvelopeAsync(Guid commitEventId, DekRotation.DekEnvelopeAnswer answer) =>
        DekRotation.DekEnvelopeRefresh.StoreAsync(connFactory, commitEventId, answer);

    private sealed record ParsedRotation(Guid NodeId, byte[] Old, byte[] New, long Counter, byte[] NewProof, byte[] OldProof, long Checkpoint,
        string OwnEventIds);

    /// <summary>Parses and checks the shape and both proofs of a rotation; false with the reason when anything is wrong.</summary>
    private static bool TryParseRotation(SyncEvent evt, out ParsedRotation rotation, out string reason)
    {
        rotation = null!;
        NodeKeyRotatedPayload p;
        try { p = Deserialize<NodeKeyRotatedPayload>(evt.Payload); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        {
            reason = "payload does not parse";
            return false;
        }
        if (!Guid.TryParseExact(p.NodeId, "D", out var subject) || subject != evt.NodeId)
        {
            reason = "a node announces only its own key";
            return false;
        }
        byte[] old, @new, newProof, oldProof;
        try
        {
            old = Convert.FromBase64String(p.OldPublicKey ?? "");
            @new = Convert.FromBase64String(p.NewPublicKey ?? "");
            newProof = Convert.FromBase64String(p.NewKeyProof ?? "");
            oldProof = Convert.FromBase64String(p.OldKeyProof ?? "");
        }
        catch (FormatException)
        {
            reason = "a key or proof is not base64";
            return false;
        }
        if (p.Counter < 1 || p.CheckpointLamportTs < 0 || p.CheckpointLamportTs >= evt.LamportTs)
        {
            reason = "counter or checkpoint out of range";
            return false;
        }
        if (!NodeKeyTransition.VerifyLink(subject, new NodeKeyLink(old, @new, p.Counter, oldProof, newProof)))
        {
            reason = "the proofs of the old and the new key do not verify";
            return false;
        }
        try { _ = EventIdSet.Unpack(p.OwnEventIds); }
        catch (FormatException)
        {
            reason = "own_event_ids is malformed";
            return false;
        }
        rotation = new ParsedRotation(subject, old, @new, p.Counter, newProof, oldProof, p.CheckpointLamportTs, p.OwnEventIds ?? "");
        reason = "";
        return true;
    }

    private async Task ApplyNodeKeyRotatedAsync(SyncEvent evt)
    {
        if (!TryParseRotation(evt, out var r, out var reason))
            throw new InvalidDataException($"node_key_rotated {evt.EventId}: {reason}.");

        bool rotatedNow;
        using (var conn = connFactory.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            rotatedNow = await ApplyRotationRowsAsync(conn, tx, evt, r);
            tx.Commit();
        }
        if (!rotatedNow) return;

        logger.LogWarning("Node {NodeId} has a new identity key (rotation {Counter}); it must authenticate again with it", r.NodeId, r.Counter);
        await CleanupRekeyedPeerAsync(r.NodeId);
        await CheckRetiredKeyEventsAsync(r, evt);
    }

    /// <returns>True when the key changed now (not a re-delivery, not a conflict).</returns>
    private async Task<bool> ApplyRotationRowsAsync(IDbConnection conn, IDbTransaction tx, SyncEvent evt, ParsedRotation r)
    {
        // Only for an active row: never for a revoked or unconfirmed node (a revoked one was refused before; this is the transaction's own check).
        var current = await conn.QueryFirstOrDefaultAsync<byte[]?>(
            "SELECT ed25519_public_key FROM tbl_whitelist WHERE node_id = @id COLLATE NOCASE AND status = 'A'", new { id = Id(r.NodeId) }, tx);
        if (current is null) throw new UnauthorizedAccessException($"node_key_rotated {evt.EventId}: node {r.NodeId} is not an active peer here.");

        // The same gate as the signature check, inside the transaction: no rotation of another counter while the node's conflict is open.
        if (await NodeKeyHistoryStore.GetConflictAsync(conn, tx, r.NodeId) is { IsOpen: true } open && open.Counter != r.Counter)
            throw new NodeIdentityConflictException(r.NodeId);

        var history = await NodeKeyHistoryStore.ListAsync(conn, tx, r.NodeId);
        var sameCounter = history.FirstOrDefault(h => h.Counter == r.Counter);
        if (sameCounter is not null)
        {
            if (sameCounter.RotationEventId == evt.EventId) return false;   // re-delivery
            if (sameCounter.PublicKey.AsSpan().SequenceEqual(r.Old) && !sameCounter.NewPublicKey.AsSpan().SequenceEqual(r.New))
            {
                // Two valid rotations from the same (old key, counter): somebody else holds the old seed. Refuse the node until a confirm.
                await NodeKeyHistoryStore.RecordConflictAsync(conn, tx, r.NodeId, r.Counter, r.Old, sameCounter.RotationEventId,
                    sameCounter.NewPublicKey, evt.EventId, r.New);
                logger.LogError("Identity conflict: node {NodeId} announced two different keys for rotation {Counter} (fingerprints {First} and {Second}); its events are refused until a superadmin confirms one",
                    r.NodeId, r.Counter, NodeKeyTransition.Fingerprint(sameCounter.NewPublicKey), NodeKeyTransition.Fingerprint(r.New));
                return false;
            }
            throw new InvalidDataException($"node_key_rotated {evt.EventId}: rotation {r.Counter} of node {r.NodeId} is already known with another old key.");
        }

        var stored = history.Count == 0 ? 0 : history.Max(h => h.Counter);
        if (r.Counter > stored + 1) throw new NodeKeyRotationGapException(r.NodeId, stored, r.Counter);
        if (r.Counter <= stored)
            throw new InvalidDataException($"node_key_rotated {evt.EventId}: rotation {r.Counter} of node {r.NodeId} is older than the one held ({stored}).");
        if (!current.AsSpan().SequenceEqual(r.Old))
            throw new InvalidDataException($"node_key_rotated {evt.EventId}: it does not retire the key this node holds for {r.NodeId}.");

        await conn.ExecuteAsync(
            "UPDATE tbl_whitelist SET ed25519_public_key = @key, updated_at = @now WHERE node_id = @id COLLATE NOCASE AND status = 'A'",
            new { key = r.New, now = DateTime.UtcNow.ToString("O"), id = Id(r.NodeId) }, tx);
        await NodeKeyHistoryStore.InsertIfAbsentAsync(conn, tx, new NodeKeyHistoryEntry(r.NodeId, r.Counter, r.Old, r.New, evt.LamportTs,
            evt.EventId, r.Checkpoint, r.OwnEventIds, r.OldProof, r.NewProof));
        return true;
    }

    /// <summary>
    /// What a re-keyed peer loses, as a revoked one does (plan D1): its sync tokens and cached contact state. It stays a peer and keeps its id and its
    /// network settings: the whitelist row changes only its key, and its channel rows (the addresses a person typed for it among them) stay, with
    /// the failures noted against its old key forgotten.
    /// </summary>
    private async Task CleanupRekeyedPeerAsync(Guid nodeId)
    {
        peerState?.PeerChanged(nodeId);
        contactLog?.Forget(nodeId);
        reachability?.Forget(nodeId);
        if (peerChannels is not null)
            await peerChannels.ResetFailuresForPeerAsync(nodeId);
        // What a closed node queued for it (plan B3): its old-key events and position reports, as a revoke drops them.
        if (relayInbox is not null)
            await relayInbox.DeleteForPeerAsync(nodeId);
        if (revocationCleanups is not null)
            foreach (var cleanup in revocationCleanups)
                await cleanup.OnPeerRevokedAsync(nodeId);
        // The superadmin doors list is sealed to each shared peer's whitelist key: on the accounts node, the next list goes to the peer's NEW
        // key, and a list sealed to the retired one (published before this rotation arrived) is replaced. The publisher's digest includes the
        // recipients' keys, so a peer that is no shared recipient changes nothing.
        if (superadminDoorsNotifier is not null)
            await superadminDoorsNotifier.NotifyChangedAsync(immediate: false);
    }

    /// <summary>
    /// Plan D3: the events of the re-keyed node this node already holds, above the rotation's checkpoint, signed by the retired key and NOT on the
    /// rotation's list, were accepted from somebody who held the old seed (or relayed by a peer that still trusted it). Raises the attention flag.
    /// <para>
    /// Below the checkpoint: an event an older build stored and that never passed a Live check (it waits for the one-time replay
    /// of release 2.12, <see cref="LateEventReplay"/>) counts as well when it is not on the list - a back-dated forgery is below the checkpoint by
    /// choice. Other events below the checkpoint do not: they passed a Live check under the key that was current then, and a genuine one the author
    /// compacted away is missing from its list as well (counting those would raise the flag after every re-key of a node that compacted).
    /// </para>
    /// </summary>
    private async Task CheckRetiredKeyEventsAsync(ParsedRotation r, SyncEvent rotation)
    {
        try
        {
            var listed = EventIdSet.Unpack(r.OwnEventIds);
            using var conn = connFactory.CreateConnection();
            // The rotation itself is left out by its id as a Guid, not by text: event ids are stored in whatever case the writer bound them
            // (the provider's Guid binding writes upper case), and a text comparison with a lower-case id would not leave it out.
            var candidates = (await conn.QueryAsync<string>(
                "SELECT event_id FROM tbl_event WHERE node_id = @id COLLATE NOCASE AND lamport_ts > @cp",
                new { id = Id(r.NodeId), cp = r.Checkpoint })).ToList();
            candidates.AddRange(await conn.QueryAsync<string>(
                @"SELECT e.event_id FROM tbl_event e
                  JOIN tbl_migration_marker m ON m.key = @prefix || lower(e.event_id)
                  WHERE e.node_id = @id COLLATE NOCASE AND e.lamport_ts <= @cp AND m.value LIKE @live",
                new { id = Id(r.NodeId), cp = r.Checkpoint, prefix = LateEventReplay.UnknownMarkerPrefix, live = "%" + LateEventReplay.LiveCheckSuffix }));
            var unacknowledged = 0;
            foreach (var candidate in candidates)
            {
                if (!Guid.TryParse(candidate, out var id) || id == rotation.EventId || listed.Contains(id)) continue;
                var stored = await eventLogRepo.GetByIdAsync(candidate);
                if (stored is null) continue;
                // A type its author prunes from its own log on its own (a superseded node_state, recovery box material it retired, a plaintext
                // comment): the rotation lists only what the author still holds, so such an event's absence from the list proves nothing. A
                // genuine recovery_box_set the author had purged before it re-keyed raised the flag on every receiver that still held it.
                if (SignedAsThisNode.PrunedOnTheirOwn.Contains(stored.EventType, StringComparer.Ordinal)) continue;
                if (NodeEventVerifier.SignatureVerifies(r.Old, EventSignature.BuildPayload(stored), stored.Signature)) unacknowledged++;
            }
            if (unacknowledged == 0) return;
            await RetiredKeyAttention.RaiseAsync(connFactory, r.NodeId, unacknowledged);
            logger.LogError("This node accepted {Count} event(s) of node {NodeId} signed by its retired identity key that the node's rotation does not acknowledge; somebody held the old key. Join that node again",
                unacknowledged, r.NodeId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The check for unacknowledged events of re-keyed node {NodeId} failed ({Type})", r.NodeId, ex.GetType().Name);
        }
    }

    private async Task ApplyNodeKeyConfirmAsync(SyncEvent evt, bool own = false)
    {
        var p = Deserialize<NodeKeyConfirmPayload>(evt.Payload);
        if (!Guid.TryParseExact(p.NodeId, "D", out var subject))
            throw new InvalidDataException($"node_key_confirm {evt.EventId}: bad node id.");
        if (subject == evt.NodeId)
            throw new UnauthorizedAccessException($"node_key_confirm {evt.EventId}: a node never confirms its own key.");
        if (!Guid.TryParseExact(p.RotationEventId, "D", out var rotationId))
            throw new InvalidDataException($"node_key_confirm {evt.EventId}: bad rotation event id.");
        byte[] confirmed;
        try { confirmed = Convert.FromBase64String(p.ConfirmedPublicKey ?? ""); }
        catch (FormatException) { throw new InvalidDataException($"node_key_confirm {evt.EventId}: the confirmed key is not base64."); }

        // The rotation it names must be in this node's log and carry that key and counter: a confirm cannot introduce a key no rotation carried.
        var rotation = await eventLogRepo.GetByIdAsync(rotationId.ToString("D"));
        if (rotation is null && !own) throw new NodeKeyConfirmEarlyException(evt.EventId, rotationId);
        if (rotation is null || rotation.EventType != EventTypes.NodeKeyRotated || rotation.NodeId != subject
            || !TryParseRotation(rotation, out var r, out _) || r.Counter != p.Counter || !r.New.AsSpan().SequenceEqual(confirmed))
            throw new InvalidDataException($"node_key_confirm {evt.EventId}: the rotation it names is not in this node's log with that key.");

        using var conn = connFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        // This node's own confirm: logged first, in this transaction.
        if (own) await eventLogRepo.AppendAsync(evt, tx);
        var conflict = await NodeKeyHistoryStore.GetConflictAsync(conn, tx, subject);
        if (conflict is not { IsOpen: true } || conflict.Counter != p.Counter)
        {
            if (own)
            {
                tx.Rollback();
                throw new InvalidOperationException($"node_key_confirm {evt.EventId}: there is no open conflict of rotation {p.Counter} for node {subject} to settle.");
            }
            tx.Commit();
            logger.LogInformation("node_key_confirm {EventId} for node {NodeId}: no open conflict for rotation {Counter} here; nothing to settle", evt.EventId, subject, p.Counter);
            return;
        }

        // The rule (ADR 0013): when the confirmed key is the one this node already took for that counter, nothing rolls back -
        // the rows after it chain from the genuine key, and the node's key stays its newest. Otherwise every row from that counter on came from the
        // wrong side (a later row chains from the wrong key): they are deleted, the confirmed rotation's row takes their place and its key is the
        // node's key. Rotations of the node that arrived while the conflict was open were deferred, and apply on their retry.
        var held = (await NodeKeyHistoryStore.ListAsync(conn, tx, subject)).FirstOrDefault(h => h.Counter == p.Counter);
        if (held is null || !held.NewPublicKey.AsSpan().SequenceEqual(confirmed))
        {
            await conn.ExecuteAsync("DELETE FROM tbl_node_key_history WHERE node_id = @id COLLATE NOCASE AND counter >= @counter",
                new { id = Id(subject), counter = p.Counter }, tx);
            await NodeKeyHistoryStore.InsertIfAbsentAsync(conn, tx, new NodeKeyHistoryEntry(subject, r.Counter, r.Old, r.New, rotation.LamportTs,
                rotation.EventId, r.Checkpoint, r.OwnEventIds, r.OldProof, r.NewProof));
            await conn.ExecuteAsync(
                "UPDATE tbl_whitelist SET ed25519_public_key = @key, updated_at = @now WHERE node_id = @id COLLATE NOCASE AND status = 'A'",
                new { key = confirmed, now = DateTime.UtcNow.ToString("O"), id = Id(subject) }, tx);
        }
        await NodeKeyHistoryStore.SettleConflictAsync(conn, tx, subject, evt.EventId);
        tx.Commit();

        logger.LogWarning("Identity conflict of node {NodeId} settled by {Signer}: its key is the one of rotation {Rotation}", subject, evt.NodeId, rotationId);
        await CleanupRekeyedPeerAsync(subject);
    }
}

/// <summary>Raises <see cref="EventApplier.RetiredKeyAttentionFlag"/> in this node's <c>node_state</c> while the marker of any node is set (plan D3).</summary>
public sealed class RetiredKeyAttentionContributor(IDbConnectionFactory db) : INodeStateContributor
{
    public int Order => 50;

    public async Task ContributeAsync(NodeStateDraft draft, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var any = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key LIKE @prefix",
            new { prefix = EventApplier.RetiredKeyAttentionMarkerPrefix + "%" });
        if (any > 0 && !draft.AttentionFlags.Contains(EventApplier.RetiredKeyAttentionFlag))
            draft.AttentionFlags.Add(EventApplier.RetiredKeyAttentionFlag);
    }
}
