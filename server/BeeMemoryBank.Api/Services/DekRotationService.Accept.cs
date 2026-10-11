using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.DekRotation;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

public partial class DekRotationService
{
    // DESIGN NOTE: single giant transaction for the entire re-wrap + sentinel + epoch + slot-delete.
    // Rationale: partial states where some rows are re-wrapped with the new DEK and others still
    // use the old DEK are unrecoverable — we cannot tell which DEK a row uses without the sentinel.
    // A single tx means either ALL rows move to the new DEK atomically, or none do.
    // Resumability is achieved via last_processed_id_* checkpoints INSIDE the transaction
    // (same connection). On crash-and-retry, the caller must re-issue AcceptCommitAsync, which
    // will start over from scratch. This is safe because the tx is either fully committed or
    // fully rolled back — no partial state survives a crash.
    public async Task AcceptCommitAsync(string commitEventId, string masterPassword, int? initiatorUserId = null)
    {
        if (!await _executeLock.WaitAsync(TimeSpan.Zero))
            throw new ConflictException("Another rotation is in progress.");
        await AcceptCommitHoldingLockAsync(commitEventId, masterPassword, initiatorUserId);
    }

    /// <summary>
    /// Background accept for the /accept endpoint. The rotation lock is claimed HERE, before the
    /// caller answers, so a busy node can refuse with 409. Claimed inside the background task
    /// instead, a refusal had nowhere to go: the task only logged it, the caller had already
    /// answered 202, and /progress kept showing the previous accept's result forever. The window
    /// is real — a failed accept publishes Failed before it releases the lock, so a retry clicked
    /// the moment Failed appears lands in it. Returns null, starting nothing, when the lock is busy.
    /// </summary>
    public Task? TryStartAcceptCommit(string commitEventId, string masterPassword, int? initiatorUserId)
    {
        if (!_executeLock.Wait(TimeSpan.Zero))
            return null;
        return Task.Run(() => AcceptCommitHoldingLockAsync(commitEventId, masterPassword, initiatorUserId));
    }

    /// <summary>Runs the accept; the caller has claimed <c>_executeLock</c>, which this releases.</summary>
    private async Task AcceptCommitHoldingLockAsync(string commitEventId, string masterPassword, int? initiatorUserId)
    {
        try
        {
            await HeavyOperationLock.Instance.WaitAsync();
            bool runPostCompaction = false;
            try
            {
                _maintenance.Enter("DEK rotation in progress\u2026");
                try
                {
                    // False: the DEK is swapped but the new metadata key is not published yet (spec 4.4). The rotation is not
                    // complete then: no Completed, no completion record, no compaction until the publication holds.
                    runPostCompaction = await AcceptCommitCoreAsync(commitEventId, masterPassword, initiatorUserId);
                }
                finally
                {
                    _maintenance.Exit();
                }
            }
            finally
            {
                HeavyOperationLock.Instance.Release();
            }

            if (runPostCompaction)
                PublishCompleted();

            // SemaphoreSlim is non-reentrant; otherwise compaction silently no-ops and we lose
            // the post-rotation log compaction. Rotation tx already committed; DB is consistent
            // for normal use even though we are now out of maintenance mode.
            if (runPostCompaction)
            {
                try
                {
                    using var compactionScope = _scopeFactory.CreateScope();
                    var compactionService = compactionScope.ServiceProvider.GetRequiredService<CompactionService>();
                    await compactionService.ExecuteAsync(reason: "dek-rotation");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DEK rotation: post-rotation compaction failed (non-fatal)");
                }
            }
        }
        finally
        {
            _executeLock.Release();
        }
    }

    private int _publishSubscribed;

    /// <summary>
    /// Step A (docs/design/step-a-spec.md 4.4): the rotation's transaction created the next metadata key generation (under the
    /// new DEK) and the durable marker; this logs its <c>metadata_key_set</c> event and ends the marker in one transaction. True
    /// when nothing is outstanding. False is NOT swallowed into "done": the rotation stays <see cref="DekRotationFlowStep.Completing"/>
    /// and the service retries every minute and at every unlock, until the marker is gone. Never throws: the rotation itself
    /// committed and must not be marked failed because its follow-up has not succeeded yet.
    /// </summary>
    private async Task<bool> PublishMetadataKeyAfterRotationAsync(IServiceScope scope)
    {
        // A host without the service has no metadata key to publish (it is registered with the policy that creates the marker).
        if (scope.ServiceProvider.GetService<BeeMemoryBank.Vault.Metadata.MetadataKeyPublishService>() is not { } publisher) return true;
        SubscribeToPublication(publisher);
        return await publisher.TryPublishAsync();
    }

    private void SubscribeToPublication(BeeMemoryBank.Vault.Metadata.MetadataKeyPublishService publisher)
    {
        if (Interlocked.Exchange(ref _publishSubscribed, 1) == 0)
            publisher.Published += OnMetadataKeyPublished;
    }

    /// <summary>
    /// The publication a deferred rotation waited for happened (a retry, or the unlock chain): finish the rotation as the accept
    /// would have. Detached, because the event is raised inside the publication and the finish needs the rotation lock.
    /// </summary>
    private Task OnMetadataKeyPublished(string keyId)
    {
        _ = Task.Run(FinishPendingCompletionAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Finishes the rotation this node started and could not finish at once: the completion audit, the <c>Completed</c> step and the
    /// post-rotation compaction, once its new metadata key is published. What is left to do is read from the durable
    /// <see cref="DekRotationCompletionRecord"/> (written in the DEK transaction), never from the memory of a process, so it works
    /// after a restart and is repeated until it holds: from the unlock chain, from the service's own timer and from the
    /// publication. Idempotent and never throws: a failed finish keeps the record and is tried again; a rotation that has nothing
    /// left, or whose key is not published yet, does nothing.
    /// </summary>
    public async Task FinishPendingCompletionAsync()
    {
        var compact = false;
        if (!await _executeLock.WaitAsync(TimeSpan.FromSeconds(30)))
            return; // an accept is running; it finishes inline or leaves the record for the next try
        try
        {
            if (DekRotationCompletionRecord.Read(_connFactory) is not { } record) return;
            if (!_sessionService.IsUnlocked) return;

            using var scope = _scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<BeeMemoryBank.Vault.Metadata.MetadataKeyPublishService>() is { } publisher)
            {
                SubscribeToPublication(publisher);
                if (publisher.IsPending)
                {
                    // The key of this rotation is not published yet (a restart before the unlock chain got to it): still completing.
                    _progress.Update(DekRotationFlowStep.Completing, 96,
                        "The key is rotated; publishing the new metadata key is still outstanding and is retried every minute and at every unlock.",
                        eventId: record.CommitEventId);
                    return;
                }
            }

            await CompleteRotationAsync(scope, record);
            PublishCompleted();
            compact = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DEK rotation: finishing the rotation failed; it is retried every minute and at every unlock");
        }
        finally
        {
            _executeLock.Release();
        }

        if (compact) await RunPostRotationCompactionAsync();
    }

    /// <summary>The compaction a rotation runs after it (its events were the last to describe the retired DEK). Failure is not fatal.</summary>
    private async Task RunPostRotationCompactionAsync()
    {
        try
        {
            using var compactionScope = _scopeFactory.CreateScope();
            await compactionScope.ServiceProvider.GetRequiredService<CompactionService>().ExecuteAsync(reason: "dek-rotation");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DEK rotation: post-rotation compaction failed (non-fatal)");
        }
    }

    /// <summary>The service's retry of <see cref="FinishPendingCompletionAsync"/> while unlocked and a record waits.</summary>
    private void OnCompletionTimer()
    {
        try
        {
            if (!_sessionService.IsUnlocked || DekRotationCompletionRecord.Read(_connFactory) is null) return;
        }
        catch
        {
            return; // the database is gone (shutdown); nothing to retry
        }
        _ = Task.Run(FinishPendingCompletionAsync);
    }

    /// <summary>
    /// The completion record and the log line of a rotation whose metadata key is published (or that has none to publish), then the
    /// durable record is deleted. Writing the audit line twice would be wrong and losing it worse, so the order is: audit (skipped
    /// when this rotation's line is already there), then the record. A crash in between repeats the step without a second line.
    /// </summary>
    private async Task CompleteRotationAsync(IServiceScope scope, DekRotationCompletionRecord done)
    {
        if (!await CompletionAuditExistsAsync(done.CommitEventId))
        {
            var auditRepo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
            await auditRepo.LogAsync(
                "dek_rotation",
                done.CommitEventId,
                "dek_rotation_completed",
                "web",
                $"DEK rotation completed; epoch {done.NewEpoch - 1}→{done.NewEpoch}; initiator={done.InitiatorId} ({done.InitiatorName}); pre-rotation snapshot={done.SnapshotFileName}; auto-unlock agent keys removed={done.AgentsDeleted}");
        }

        using (var conn = _connFactory.CreateConnection())
            DekRotationCompletionRecord.Clear(conn);

        _logger.LogInformation(
            "DEK rotation completed. Epoch {OldEpoch}→{NewEpoch}. Initiator={Initiator} ({InitiatorName}). Snapshot={Snap}. AutoUnlockAgentsRemoved={Agents}.",
            done.NewEpoch - 1, done.NewEpoch, done.InitiatorId, done.InitiatorName, done.SnapshotFileName, done.AgentsDeleted);
    }

    /// <summary>
    /// Why a new rotation must wait for the previous one, or null: its new metadata key is not published yet (spec 4.4), or its
    /// completion record is not finished. <paramref name="then"/> completes the sentence "Wait until it is, then ...".
    /// </summary>
    private string? PreviousRotationUnfinishedMessage(IServiceScope scope, string then)
    {
        if (scope.ServiceProvider.GetService<BeeMemoryBank.Vault.Metadata.IMetadataKeyPublishGate>() is { IsPending: true })
            return $"The new metadata key of the previous key rotation is not published yet. Wait until it is, then {then}.";
        if (DekRotationCompletionRecord.Read(_connFactory) is not null)
            return $"The previous key rotation is still being completed. Wait a moment, then {then}.";
        return null;
    }

    private async Task<bool> CompletionAuditExistsAsync(string commitEventId)
    {
        using var conn = _connFactory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tbl_audit_log WHERE entity_type = 'dek_rotation' AND entity_id = @commitEventId AND action = 'dek_rotation_completed'",
            new { commitEventId }) > 0;
    }

    private async Task<bool> AcceptCommitCoreAsync(string commitEventId, string masterPassword, int? initiatorUserId)
    {
        using var scope = _scopeFactory.CreateScope();
        var stateRepo = scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var keySlotRepo = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
        var snapshotService = scope.ServiceProvider.GetRequiredService<SnapshotService>();

        _progress.Update(DekRotationFlowStep.Committing, 15, "Loading commit event...", eventId: commitEventId);
        _progress.ClearError();

        SyncEvent commitEvent;
        {
            var eventLogRepo = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
            var rawEvent = await eventLogRepo.GetByIdAsync(commitEventId);

            if (rawEvent == null)
                throw new InvalidOperationException($"Commit event {commitEventId} not found.");
            if (rawEvent.EventType != EventTypes.DekRotationCommit)
                throw new InvalidOperationException($"Event {commitEventId} is not a dek_rotation_commit.");
            commitEvent = rawEvent;
        }

        var payload = JsonSerializer.Deserialize<DekRotationCommitPayload>(commitEvent.Payload, JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize commit payload.");

        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        var sigPayload = EventSignature.BuildPayload(commitEvent);
        if (!Ed25519Signer.Verify(identity.Ed25519PublicKey, sigPayload, commitEvent.Signature))
            throw new InvalidOperationException("Commit event signature verification failed.");

        // A commit this node already applied (or cancelled, or rejected) must not be run again:
        // re-running it would treat the CURRENT DEK as the old one. A Committing or Failed commit
        // may be accepted again — that is how a deferred accept (see the precondition catch
        // below) is retried, with the master password, without a new proposal. Checked before
        // the pre-rotation snapshot so a refused accept leaves nothing behind.
        var existingState = await stateRepo.GetAsync(commitEventId);
        if (existingState?.State is DekRotationState.Applied or DekRotationState.Cancelled or DekRotationState.Rejected)
        {
            var refusal = $"DEK rotation {commitEventId} is already {existingState.State.ToString().ToLowerInvariant()}; it cannot be accepted again.";
            _progress.Update(DekRotationFlowStep.Failed, err: refusal, msg: refusal);
            throw new ConflictException(refusal);
        }

        // Two rotations must not overlap on the metadata key: the marker of the first names the one key whose publication is
        // outstanding, and its completion record the one rotation whose finish is. Refused before anything is changed (the commit
        // stays pending and is accepted again later).
        if (PreviousRotationUnfinishedMessage(scope, "accept this rotation") is { } unfinished)
        {
            _progress.Update(DekRotationFlowStep.Failed, err: unfinished, msg: unfinished);
            throw new ConflictException(unfinished);
        }

        _progress.Update(DekRotationFlowStep.PreRotationBackup, 18, "Creating pre-rotation backup...");

        var snap = await snapshotService.CreateAsync(filterSecrets: false, sign: false, cpSequenceNum: null);
        _logger.LogInformation("DEK rotation: pre-rotation snapshot created: {FileName}", snap.FileName);

        // Decrypt new DEK + run pre-validation INSIDE the state-setting try-catch. Otherwise
        // a CryptographicException from a corrupted payload bubbles past the state machine,
        // leaves _progress.Step stuck at Committing, AND leaks oldDek (no finally reaches it).
        byte[]? oldDek = null;
        byte[]? newDek = null;
        // Node-local chain material for LazySlotRewrap; for a confidential rotation it is computed
        // by ResolveNewDek (wrap of the opened DEK under oldDek), not taken off the wire.
        string? chainEncB64 = null;
        string? chainIvB64 = null;

        User initiator;
        BeeMemoryBank.Core.Models.MasterKeyStore initiatorSlot;
        byte[]? localKek = null;
        try
        {
            oldDek = _sessionService.GetMasterDek();
            // Confidential rotation: open this node's own envelope (initiator gets one too). Legacy
            // events fall back to unwrap-under-old-DEK. (ADR 0006.)
            newDek = DekRotationMaterial.ResolveNewDek(
                payload, commitEvent.EventId, identity, oldDek, out chainEncB64, out chainIvB64);

            if (initiatorUserId.HasValue)
            {
                var user = await userRepo.GetByIdAsync(initiatorUserId.Value)
                    ?? throw new UnauthorizedAccessException("Initiator user not found.");
                if (user.Role != UserRoles.Superadmin)
                    throw new UnauthorizedAccessException("Only superadmins can rotate the DEK.");
                if (!user.IsActive)
                    throw new UnauthorizedAccessException("Initiator user is inactive.");
                initiator = user;
            }
            else
            {
                _logger.LogWarning("DEK rotation initiator not specified; falling back to first active superadmin. This is acceptable for CLI/system calls but should not happen from HTTP endpoints.");
                var users = await userRepo.ListActiveAsync();
                initiator = users.FirstOrDefault(u => u.Role == UserRoles.Superadmin && u.KeySlotId != null)
                    ?? throw new InvalidOperationException(
                        "No active superadmin with a key slot found. A superadmin promoted but " +
                        "not yet logged in has no slot yet — have one log in first.");
            }

            if (initiator.KeySlotId == null)
                throw new InvalidOperationException("Initiator has no key slot.");

            var allSlots = await keySlotRepo.GetAllAsync();
            initiatorSlot = allSlots.FirstOrDefault(s => s.SlotId == initiator.KeySlotId)
                ?? throw new InvalidOperationException("Initiator key slot not found.");

            if (initiatorSlot.Salt == null || !initiatorSlot.ArgonMemory.HasValue
                || !initiatorSlot.ArgonIterations.HasValue || !initiatorSlot.ArgonParallelism.HasValue)
                throw new InvalidOperationException("Initiator key slot missing Argon2 parameters.");

            localKek = KeyDerivation.DeriveKek(
                masterPassword,
                initiatorSlot.Salt,
                initiatorSlot.ArgonMemory.Value,
                initiatorSlot.ArgonIterations.Value,
                initiatorSlot.ArgonParallelism.Value);

            // Verify masterPassword unwraps initiator's slot to the SAME DEK currently held in
            // SessionService. Without this check, a typo on Accept would (a) successfully re-wrap
            // every article body with the new DEK, (b) wrap the new DEK into the initiator slot
            // using a garbage KEK derived from the wrong password, (c) drop all other slots — the
            // node would be unrecoverable except via the pre-rotation snapshot.
            byte[] verifyDek;
            try
            {
                verifyDek = MasterKeyManager.UnwrapMasterDek(initiatorSlot.EncryptedMasterDek, initiatorSlot.IV, localKek);
            }
            catch (CryptographicException)
            {
                Array.Clear(localKek);
                throw new UnauthorizedAccessException("Wrong master password.");
            }
            var sessionDek = _sessionService.GetMasterDek();
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(verifyDek, sessionDek))
                    throw new UnauthorizedAccessException("Wrong master password.");
            }
            finally
            {
                Array.Clear(verifyDek);
                Array.Clear(sessionDek);
            }
        }
        catch (Exception ex)
        {
            _progress.Update(DekRotationFlowStep.Failed, err: ex.Message, msg: "DEK rotation failed: " + ex.Message);
            await stateRepo.UpdateStateAsync(commitEventId, DekRotationState.Failed, ex.Message);
            _logger.LogError(ex, "DEK rotation pre-validation failed for commit event {CommitEventId}", commitEventId);
            // Clear partial key material on the pre-validation failure path. The destructive-
            // section finally (line ~700) only runs if we actually entered destructive code —
            // localKek in particular would leak here when password verify threw
            // UnauthorizedAccessException right after KEK derivation.
            if (oldDek != null) Array.Clear(oldDek);
            if (newDek != null) Array.Clear(newDek);
            if (localKek != null) Array.Clear(localKek);
            throw;
        }

        var rewrapped = false;
        void RemovePreRotationSnapshot()
        {
            // Clean up the pre-rotation snapshot — without this, every failed rotation leaves
            // a ~DBsize .tar.gz behind. With repeated retries on a 1GB DB, the snapshots
            // directory fills and the disk-space pre-check then BLOCKS future rotations.
            // A retried accept takes a fresh one.
            try
            {
                var snapPath = snapshotService.GetSnapshotPath(snap.FileName);
                if (System.IO.File.Exists(snapPath))
                {
                    System.IO.File.Delete(snapPath);
                    _logger.LogInformation("Removed pre-rotation snapshot {Snap} after rotation failure.", snap.FileName);
                }
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Failed to remove pre-rotation snapshot {Snap}", snap.FileName);
            }
        }

        try
        {
            var (newEncDek, newIv) = MasterKeyManager.WrapMasterDek(newDek, localKek);

            // The initiator stores the chain material too. Its own slot is re-wrapped right here
            // so it never needs the chain itself — but a user who logs in on THIS node for the
            // first time after a rotation goes through the same lazy walk, and the initiator is
            // also the node that compacts immediately afterwards, which is what deletes the events
            // the walk used to read.
            // Step A (spec 4.4): on an activated network the next metadata key generation is created in the very transaction that
            // swaps the DEK, so there is no committed state with a new DEK and no new key (a failure rolls the rotation back).
            var createNextMetadataKey = scope.ServiceProvider.GetService<BeeMemoryBank.Sync.Metadata.ISyncProtocolPolicy>()?.SealsMetadata ?? false;
            // What is left to finish after the commit (audit, Completed, compaction) is written in that same transaction.
            var done = new DekRotationCompletionRecord(commitEventId, payload.NewDekEpoch, initiator.Id, initiator.DisplayName, snap.FileName, 0);
            var (agentsDeleted, _, _) = await RewrapDestructiveCoreAsync(
                oldDek, newDek, payload.NewDekEpoch, commitEventId,
                isInitiator: true, initiatorSlot.SlotId, newEncDek, newIv,
                chainEncryptedNewDekB64: chainEncB64,
                chainIvB64: chainIvB64,
                createNextMetadataKey: createNextMetadataKey,
                completionRecord: done);
            rewrapped = true;
            done = done with { AgentsDeleted = agentsDeleted };

            // Blind-node recovery (plan 6.5): the rotation was started here and the password is in
            // memory — device box for the rewrapped slot, chain link, re-seals, new strong box.
            _ = scope.ServiceProvider.GetRequiredService<RecoveryTriggers>()
                .OnRotationAccepted(initiatorSlot.SlotId, masterPassword);

            if (!await PublishMetadataKeyAfterRotationAsync(scope))
            {
                _progress.Update(DekRotationFlowStep.Completing, 96,
                    "The key is rotated; publishing the new metadata key is still outstanding and is retried every minute and at every unlock.");
                _logger.LogWarning("DEK rotation {CommitEventId}: the new metadata key is not published yet; the rotation completes when it is", commitEventId);
                return false;
            }

            // Published. A retry's finisher cannot pass the rotation lock this path holds, so it finds the record gone or waits.
            try
            {
                await CompleteRotationAsync(scope, done);
            }
            catch (Exception ex)
            {
                // The DEK is rotated and the key published: this is not a failed rotation. The record stays and the finish is retried.
                _progress.Update(DekRotationFlowStep.Completing, 97,
                    "The key is rotated; writing the completion record failed and is retried every minute and at every unlock.");
                _logger.LogError(ex, "DEK rotation {CommitEventId}: the completion record could not be written; it is retried", commitEventId);
                return false;
            }
            return true;
        }
        catch (DekRotationPreconditionException ex) when (!rewrapped)
        {
            // The mandatory pre-rewrap hooks refused (they passed at propose, but e.g. chat.db is
            // briefly locked now). Nothing was changed — the rewrap transaction never opened and
            // the session is still on the old DEK. The COMMIT, however, is already public and
            // peers may have applied it, so this must NOT become Failed: the row stays Committing
            // and the same commit is accepted again (master password required) once the cause is
            // gone. Marking it Failed here is what used to split the initiator from its peers.
            _progress.Update(DekRotationFlowStep.Failed, err: ex.Message,
                msg: "DEK rotation not applied yet: the commit is still pending. Fix the cause and accept it again.");
            RemovePreRotationSnapshot();
            _logger.LogError(ex, "DEK rotation accept deferred for commit event {CommitEventId}; it stays pending for a retried accept", commitEventId);
            throw;
        }
        catch (Exception ex)
        {
            _progress.Update(DekRotationFlowStep.Failed, err: ex.Message, msg: "DEK rotation failed.");
            await stateRepo.UpdateStateAsync(commitEventId, DekRotationState.Failed, ex.Message);
            // On failure we do NOT swap DEK, so the old DEK remains active.
            // We DO exit maintenance mode so the node is usable (with old DEK).
            // Re-try requires a new Propose+Accept cycle.
            RemovePreRotationSnapshot();

            _logger.LogError(ex, "DEK rotation failed for commit event {CommitEventId}", commitEventId);
            throw;
        }
        finally
        {
            Array.Clear(localKek, 0, localKek.Length);
            // Clear key material on the error path. On the success path oldDek was already cleared
            // inside RewrapDestructiveCoreAsync and newDek ownership transferred to SessionService.SwapMasterDek.
            // Keyed on the rewrap having returned, not on the progress step: Completed is published
            // later, after maintenance mode ends, and clearing newDek here would zero the live
            // master DEK.
            if (!rewrapped)
            {
                Array.Clear(oldDek, 0, oldDek.Length);
                Array.Clear(newDek, 0, newDek.Length);
            }
        }
    }
}
