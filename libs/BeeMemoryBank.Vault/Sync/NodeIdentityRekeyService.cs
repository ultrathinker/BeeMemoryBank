using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>The outcome of <see cref="NodeIdentityRekeyService.RekeyAsync"/>: done, or refused with a sentence that can be shown as it is.</summary>
public sealed record NodeRekeyResult(bool Done, string? Refusal, string? NewFingerprint = null, long Counter = 0, Guid? RotationEventId = null)
{
    public static NodeRekeyResult Refused(string sentence) => new(false, sentence);
}

/// <summary>
/// Gives this full node a new identity key in place (release 2.12, plan WS-D): the node keeps its id, its data and its peers, and the old key stops
/// working everywhere once the peers have applied the <c>node_key_rotated</c> event. Callable without the page: the removal process
/// calls it too.
/// <para>
/// Order: the checks (unlocked, a full node with its seed in the database, every active peer able to follow), the node-local key-operation lock,
/// the list of this node's own events above its checkpoint, then ONE transaction that writes the event (signed by the old key), the new identity
/// row (the new seed wrapped under the current master key) and the history row. The old seed is overwritten in that transaction (secure_delete);
/// the buffers are cleared after it. Then the node announces its new key fingerprint (<c>node_state</c>) and a fresh state anchor.
/// </para>
/// </summary>
public sealed class NodeIdentityRekeyService(
    SessionService session,
    INodeIdentityRepository nodeRepo,
    IWhitelistRepository whitelist,
    IEventLogRepository eventLog,
    ILamportClock clock,
    INodeAuthSigner signer,
    IDbConnectionFactory db,
    ISyncProtocolPolicy protocolPolicy,
    ISyncTrigger syncTrigger,
    IServiceScopeFactory scopes,
    ILogger<NodeIdentityRekeyService> logger,
    IAuditLogRepository? audit = null,
    string? dataPath = null)
{
    /// <summary>The name of this operation in <see cref="KeyOperationLock"/>.</summary>
    public const string OperationName = "an identity re-key";

    /// <summary>Test seam only: runs right after the re-key transaction committed (a crash there).</summary>
    internal Func<Task>? AfterCommitForTests { get; set; }

    /// <summary>Test seam only: runs in the steps after a commit, right before the phone pairing records are signed again.</summary>
    internal Func<Task>? BeforeResealForTests { get; set; }

    /// <summary>
    /// The node-local marker (<c>tbl_migration_marker</c>, key prefix + node id + ":" + rotation counter) the re-key transaction writes: the steps after
    /// the commit (audit line, phone pairing records signed again, announcement) are still to do. Removed once they all held; while it stands, every
    /// unlock runs them again (<see cref="FinishInterruptedFollowUpAsync"/>).
    /// </summary>
    public const string FollowUpMarkerPrefix = "identity-rekey-followup:";

    private sealed record FollowUp(long Counter, string Fingerprint, string? Reason, string Actor);

    private static readonly System.Text.Json.JsonSerializerOptions FollowUpJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    public const string LockedRefusal = "The vault is locked. Unlock it first.";
    public const string NotAFullNodeRefusal = "Only a full node can give itself a new identity key here.";
    // say what the administrator can actually do. No tool replaces a key kept outside the database.
    public const string ExternalKeyRefusal =
        "This node's identity key is kept outside its database, and it cannot be replaced from here. If you think that key was copied, " +
        "remove this node from the network on another superadmin's device and set it up again as a new node.";
    public const string TooManyEventsRefusal =
        "This node has more than 50000 own events since its last checkpoint. Let the next compaction run (it needs every peer to have caught up), then try again.";

    /// <summary>The over-cap refusal, naming the active peer furthest behind when there is one: the one to wait for, or to remove.</summary>
    public static string TooManyEvents(string? furthestBehind) => furthestBehind is null
        ? TooManyEventsRefusal
        : "This node has more than 50000 own events since its last checkpoint. A compaction has to run first, and it waits for every peer to " +
          $"catch up; \"{furthestBehind}\" is furthest behind. Let it sync, or remove it from the network, run a compaction, then try again.";

    public const string NotASuperadminRefusal = "Only a superadmin's device can settle an identity conflict.";
    public const string NoConflictRefusal = "There is no open identity conflict for that device.";
    public const string NotOneOfTheTwoRefusal = "That key is not one of the two keys the device announced.";
    public const string OwnConflictRefusal = "A device never settles a conflict about its own key; do it from another superadmin's device.";

    /// <summary>
    /// Settles another node's identity conflict (plan WS-D D2): this superadmin's device says which of the two keys the node announced for the same
    /// rotation is the genuine one (<paramref name="rotationEventId"/>, one of the two rotations). Issues <c>node_key_confirm</c>, signed by this node,
    /// applies it here and sends it to the network; the node's events apply again under the confirmed key.
    /// </summary>
    public async Task<NodeRekeyResult> ConfirmAsync(Guid subject, Guid rotationEventId, string actor, CancellationToken ct = default)
    {
        if (!session.IsUnlocked) return NodeRekeyResult.Refused(LockedRefusal);
        var identity = await nodeRepo.GetAsync();
        if (identity is null || BlindNodeId.IsBlind(identity.NodeId)) return NodeRekeyResult.Refused(NotAFullNodeRefusal);
        if (subject == identity.NodeId) return NodeRekeyResult.Refused(OwnConflictRefusal);
        using var scope = scopes.CreateScope();
        if (scope.ServiceProvider.GetService<IOwnStandingProvider>() is not { } standing || !await standing.IsSuperadminAsync(ct))
            return NodeRekeyResult.Refused(NotASuperadminRefusal);
        NodeKeyConflict? conflict;
        using (var conn = db.CreateConnection())
            conflict = await NodeKeyHistoryStore.GetConflictAsync(conn, null, subject);
        if (conflict is not { IsOpen: true }) return NodeRekeyResult.Refused(NoConflictRefusal);
        var confirmed = rotationEventId == conflict.FirstEventId ? conflict.FirstNewKey
            : rotationEventId == conflict.SecondEventId ? conflict.SecondNewKey
            : null;
        if (confirmed is null) return NodeRekeyResult.Refused(NotOneOfTheTwoRefusal);

        var evt = NodeKeyRotationEvents.Confirm(identity.NodeId, bytes => signer.SignChallenge(identity, bytes), subject, rotationEventId,
            conflict.Counter, confirmed, clock.Tick(), protocolPolicy.Declared, identity.DisplayName);
        // Logged and settled in one transaction: a confirm this node settled is always one it sends.
        var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
        await applier.ApplyOwnNodeKeyConfirmAsync(evt);
        var fingerprint = NodeKeyTransition.Fingerprint(confirmed);
        logger.LogWarning("Identity conflict of node {NodeId} settled here: its key is {Fingerprint}", subject, fingerprint);
        if (audit is not null)
            await audit.LogAsync("node", subject.ToString("D"), "node_identity_conflict_settled", actor,
                $"Confirmed key {fingerprint} of rotation {conflict.Counter}");
        syncTrigger.Signal();
        return new NodeRekeyResult(true, null, fingerprint, conflict.Counter, evt.EventId);
    }

    /// <summary>The nodes that do not show the capability yet (the readiness card names them), or empty.</summary>
    public async Task<IReadOnlyList<string>> PeersHoldingBackAsync()
    {
        var peers = await whitelist.GetAllActiveAsync();
        return peers.Where(p => !MetadataCapabilities.Parse(p.LastCapabilities).Contains(MetadataCapabilities.IdentityRekeyV1))
            .Select(p => string.IsNullOrWhiteSpace(p.DisplayName) ? p.NodeId.ToString("D") : p.DisplayName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <param name="despiteLaggards">
    /// A removal's re-key: this node re-keys even while some devices cannot follow a new key yet. Those devices then trust only
    /// the old key and are cut off from this node until they are updated or revoked - the removal lists them in its step 1 and waits for exactly
    /// that. Waiting for them first would leave the old key, which a stolen backup holds, the current key everywhere for as long as they lag. The
    /// ordinary re-key from the identity page keeps the gate.
    /// </param>
    public async Task<NodeRekeyResult> RekeyAsync(string? reason, string actor, CancellationToken ct = default, bool despiteLaggards = false)
    {
        if (!session.IsUnlocked) return NodeRekeyResult.Refused(LockedRefusal);
        var identity = await nodeRepo.GetAsync();
        if (identity is null || BlindNodeId.IsBlind(identity.NodeId)) return NodeRekeyResult.Refused(NotAFullNodeRefusal);
        if (identity.Ed25519PrivateKeyV is not (0 or 1)) return NodeRekeyResult.Refused(ExternalKeyRefusal);

        var holding = despiteLaggards ? [] : await PeersHoldingBackAsync();
        if (holding.Count > 0)
            return NodeRekeyResult.Refused(
                $"Not every device can follow a new identity key yet. Update or remove these first: {string.Join(", ", holding)}.");

        if (dataPath is not null && BeeMemoryBank.AppPaths.RekeyLock.IsHeld(dataPath))
            return NodeRekeyResult.Refused("An offline re-key of this vault is running. Try again when it has finished.");
        using var keyOperation = KeyOperationLock.TryEnter(db.DatabaseId, OperationName);
        if (keyOperation is null) return NodeRekeyResult.Refused(KeyOperationLock.Busy(db.DatabaseId, "An identity re-key"));

        var (newPublicKey, newSeed) = Ed25519Signer.GenerateKeyPair();
        byte[]? dek = null;
        try
        {
            dek = session.GetMasterDek();
            var (wrapped, iv) = NodeIdentityVault.EncryptPrivateKey(newSeed, dek, identity.NodeId);
            SyncEvent evt;
            long counter;
            FollowUp followUp;

            // No own event is being written while the list is read and the rotation committed: authoring is quiesced (every
            // local event write runs inside the event-write gate), and the list, the counter and the commit are one transaction.
            using (await EventWriteGate.Instance.QuiesceAsync(ct))
            using (EventWriteGate.EnterOwnerFlow())
            using (var conn = db.CreateConnection())
            {
                // The old wrapped seed is overwritten in place, not left in a free page (plan D4: "erased when the event is committed").
                await conn.ExecuteAsync("PRAGMA secure_delete = ON");
                using var tx = conn.BeginTransaction();
                identity = await nodeRepo.GetAsync() ?? identity;

                // The node's own events above its checkpoint (plan D3): everything of this node still in its log. What compaction removed is below
                // the checkpoint, and every active peer already holds it (CompactionService never goes below what a peer was sent). Only events
                // this node WROTE (origin_peer IS NULL): one that names this node but arrived from outside was signed by
                // somebody else holding its seed, and listing it would make every peer accept it as this node's after the re-key.
                var own = (await conn.QueryAsync<(string, long)>(
                    "SELECT event_id, lamport_ts FROM tbl_event WHERE node_id = @id COLLATE NOCASE AND origin_peer IS NULL",
                    new { id = identity.NodeId.ToString("D") }, tx)).ToList();
                if (own.Count > EventIdSet.Cap)
                {
                    // The active peer this node has pushed the least to (never is least): what holds the compaction back.
                    var behind = await conn.ExecuteScalarAsync<string?>(
                        @"SELECT COALESCE(NULLIF(TRIM(w.display_name), ''), w.node_id) FROM tbl_whitelist w
                          LEFT JOIN tbl_sync_push_position p ON p.remote_node_id = w.node_id COLLATE NOCASE
                          WHERE w.status = 'A' AND w.node_id <> @id COLLATE NOCASE
                          ORDER BY COALESCE(p.last_pushed_seq, -1), w.node_id LIMIT 1",
                        new { id = identity.NodeId.ToString("D") }, tx);
                    tx.Rollback();
                    return NodeRekeyResult.Refused(TooManyEvents(behind));
                }
                var checkpoint = own.Count == 0 ? 0 : Math.Max(0, own.Min(e => e.Item2) - 1);
                var ids = own.Select(e => Guid.Parse(e.Item1)).ToList();
                counter = await NodeKeyHistoryStore.CounterAsync(conn, tx, identity.NodeId) + 1;
                var signing = identity;
                (evt, var entry) = NodeKeyRotationEvents.Rotation(identity.NodeId, identity.Ed25519PublicKey,
                    bytes => signer.SignChallenge(signing, bytes), newSeed, counter, clock.Tick(), checkpoint, ids, reason,
                    protocolPolicy.Declared, identity.DisplayName);
                await eventLog.AppendAsync(evt, tx);
                await conn.ExecuteAsync(
                    @"UPDATE tbl_node_identity SET ed25519_public_key = @pk, ed25519_private_key = @key, ed25519_private_key_iv = @iv,
                             ed25519_private_key_v = 1 WHERE node_id = @id COLLATE NOCASE",
                    new { pk = newPublicKey, key = wrapped, iv, id = identity.NodeId.ToString("D") }, tx);
                await NodeKeyHistoryStore.InsertIfAbsentAsync(conn, tx, entry);
                followUp = new FollowUp(counter, NodeKeyTransition.Fingerprint(newPublicKey), reason, actor);
                await conn.ExecuteAsync("INSERT OR REPLACE INTO tbl_migration_marker (key, value, set_at) VALUES (@key, @value, @now)", new
                {
                    key = FollowUpMarkerKey(identity.NodeId, counter),
                    value = System.Text.Json.JsonSerializer.Serialize(followUp, FollowUpJson),
                    now = DateTime.UtcNow.ToString("O"),
                }, tx);
                tx.Commit();
            }
            if (AfterCommitForTests is { } crash) await crash();

            logger.LogWarning("This node has a new identity key (rotation {Counter}, fingerprint {Fingerprint}); peers switch to it as they apply the rotation",
                counter, followUp.Fingerprint);
            await RunFollowUpAsync(identity.NodeId, followUp, newPublicKey, newSeed, ct);
            return new NodeRekeyResult(true, null, followUp.Fingerprint, counter, evt.EventId);
        }
        finally
        {
            Array.Clear(newSeed);
            if (dek is not null) Array.Clear(dek);
        }
    }

    /// <summary>
    /// The phone pairing records that name this node carry its new key (<see cref="AndroidPairingRekey"/>), after the commit; false when it failed or
    /// any record could not be signed again (the marker then stays, and the next unlock signs the records still on the old key).
    /// </summary>
    private async Task<bool> ResealPairingRecordsAsync(Guid self, byte[] newPublicKey, byte[] newSeed)
    {
        try
        {
            using var scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetService<Recovery.SealedSecretService>() is not { } secrets) return true;
            var (count, failed) = await AndroidPairingRekey.ResealAsync(db, session, secrets, self, newPublicKey, newSeed, logger);
            if (count > 0) logger.LogInformation("{Count} phone pairing record(s) signed again with the new identity key", count);
            return failed == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The phone pairing records could not be signed again after the re-key ({Type}); it is tried again at the next unlock",
                ex.GetType().Name);
            return false;
        }
    }

    private static string FollowUpMarkerKey(Guid self, long counter) =>
        FollowUpMarkerPrefix + self.ToString("D") + ":" + counter.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The fixed start of a re-key's audit line; the free reason, when there is one, follows after <see cref="AuditReasonSeparator"/>.</summary>
    private static string AuditHead(FollowUp todo) => $"New identity key, rotation {todo.Counter}, fingerprint {todo.Fingerprint}";

    private const string AuditReasonSeparator = "; reason: ";

    private static string AuditDetails(FollowUp todo) =>
        AuditHead(todo) + (string.IsNullOrWhiteSpace(todo.Reason) ? "" : AuditReasonSeparator + todo.Reason.Trim());

    /// <summary>
    /// The steps after the commit of re-key <paramref name="todo"/>, each one safe to repeat: the audit line (unless it is there), the phone pairing
    /// records (a record already signed with the new key is left alone), the announcement. The marker goes only when all of them held.
    /// <paramref name="newSeed"/> is not kept or cleared here.
    /// </summary>
    private async Task RunFollowUpAsync(Guid self, FollowUp todo, byte[] newPublicKey, byte[] newSeed, CancellationToken ct)
    {
        var held = await AuditOnceAsync(self, todo);
        if (BeforeResealForTests is { } midway) await midway();
        held &= await ResealPairingRecordsAsync(self, newPublicKey, newSeed);
        syncTrigger.Signal();
        held &= await AnnounceAsync(ct);
        if (held) await ForgetFollowUpAsync(self, todo.Counter);
    }

    private async Task<bool> AuditOnceAsync(Guid self, FollowUp todo)
    {
        if (audit is null) return true;
        try
        {
            // The line of this re-key: its fixed start, alone or followed by the reason. Not a match anywhere in the text, which ends with free text.
            var head = AuditHead(todo);
            using (var conn = db.CreateConnection())
                if (await conn.ExecuteScalarAsync<long>(
                        @"SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'node_identity_rekeyed' AND entity_id = @id
                          AND (details = @head OR substr(details, 1, length(@withReason)) = @withReason)",
                        new { id = self.ToString("D"), head, withReason = head + AuditReasonSeparator }) > 0)
                    return true;
            await audit.LogAsync("node", self.ToString("D"), "node_identity_rekeyed", todo.Actor, AuditDetails(todo));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The audit line of the identity re-key could not be written ({Type}); it is tried again at the next unlock", ex.GetType().Name);
            return false;
        }
    }

    private async Task ForgetFollowUpAsync(Guid self, long counter)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_migration_marker WHERE key = @key", new { key = FollowUpMarkerKey(self, counter) });
    }

    /// <summary>
    /// After an unlock: the steps after the commit of a re-key that a crash (or a failure) left undone run now, once (P11 O3). For a re-key whose key
    /// is no longer this node's current one (a later re-key, a restore) only its audit line is written: the later one re-signs and announces.
    /// <para>The steps sign with this node's key, so they run under the vault's key-operation lock, as the re-key itself does: never beside an
    /// offline re-key, a DEK rotation, the doors apply or another re-key, whose commit would make the key read here a retired one. The identity
    /// is read under the lock. Busy: nothing is done, the marker stands and the next unlock tries again.</para>
    /// </summary>
    public async Task FinishInterruptedFollowUpAsync(CancellationToken ct = default)
    {
        if (!session.IsUnlocked) return;
        var self = await nodeRepo.GetAsync();
        if (self is null) return;
        // Nothing to finish (the usual case, asked at every unlock): answered without the lock, so an unlock never holds up a key operation.
        using (var conn = db.CreateConnection())
            if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key LIKE @prefix",
                    new { prefix = FollowUpMarkerPrefix + self.NodeId.ToString("D") + ":%" }) == 0)
                return;

        if (dataPath is not null && BeeMemoryBank.AppPaths.RekeyLock.IsHeld(dataPath)) return;
        using var keyOperation = KeyOperationLock.TryEnter(db.DatabaseId, OperationName);
        if (keyOperation is null)
        {
            logger.LogInformation("The last steps of an interrupted identity re-key wait: {Holder} is running; they run at the next unlock",
                KeyOperationLock.Holder(db.DatabaseId) ?? "another key operation");
            return;
        }
        var identity = await nodeRepo.GetAsync();
        if (identity is null) return;
        List<(string Key, string Value)> markers;
        using (var conn = db.CreateConnection())
            markers = (await conn.QueryAsync<(string, string)>("SELECT key, value FROM tbl_migration_marker WHERE key LIKE @prefix",
                new { prefix = FollowUpMarkerPrefix + identity.NodeId.ToString("D") + ":%" })).ToList();
        if (markers.Count == 0) return;

        var current = NodeKeyTransition.Fingerprint(identity.Ed25519PublicKey);
        foreach (var (key, value) in markers)
        {
            FollowUp? todo;
            try { todo = System.Text.Json.JsonSerializer.Deserialize<FollowUp>(value, FollowUpJson); }
            catch (System.Text.Json.JsonException) { todo = null; }
            if (todo is null)
            {
                using var conn = db.CreateConnection();
                await conn.ExecuteAsync("DELETE FROM tbl_migration_marker WHERE key = @key", new { key });
                continue;
            }
            if (todo.Fingerprint != current || identity.Ed25519PrivateKeyV is not (0 or 1))
            {
                if (await AuditOnceAsync(identity.NodeId, todo)) await ForgetFollowUpAsync(identity.NodeId, todo.Counter);
                continue;
            }
            logger.LogWarning("The identity re-key (rotation {Counter}) was interrupted after its commit; its last steps run now", todo.Counter);
            var dek = session.GetMasterDek();
            byte[]? seed = null;
            try
            {
                seed = NodeIdentityVault.GetDecryptedPrivateKey(identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV,
                    identity.Ed25519PrivateKeyV, identity.NodeId, dek);
                await RunFollowUpAsync(identity.NodeId, todo, identity.Ed25519PublicKey, seed, ct);
            }
            finally
            {
                Array.Clear(dek);
                if (seed is not null) Array.Clear(seed);
            }
        }
    }

    /// <summary>The new key fingerprint in <c>node_state</c> and a state anchor signed by the new key; false when either failed.</summary>
    private async Task<bool> AnnounceAsync(CancellationToken ct)
    {
        var held = true;
        try
        {
            using var scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetService<NodeStatePublisher>() is { } publisher && await publisher.BuildDraftAsync(ct) is { } draft)
                await publisher.PublishAsync(draft, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The node state could not be announced after the re-key ({Type}); the announcer repeats it", ex.GetType().Name);
            held = false;
        }
        try
        {
            using var scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetService<Recovery.StateAnchorService>() is { } anchors)
                await anchors.PublishAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("A state anchor could not be published after the re-key ({Type}); the next one is signed by the new key", ex.GetType().Name);
            held = false;
        }
        return held;
    }
}
