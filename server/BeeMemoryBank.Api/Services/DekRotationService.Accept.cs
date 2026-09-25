using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Api.Models;
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
                    await AcceptCommitCoreAsync(commitEventId, masterPassword, initiatorUserId);
                    runPostCompaction = true;
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

    private async Task AcceptCommitCoreAsync(string commitEventId, string masterPassword, int? initiatorUserId)
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
            var (agentsDeleted, _, _) = await RewrapDestructiveCoreAsync(
                oldDek, newDek, payload.NewDekEpoch, commitEventId,
                isInitiator: true, initiatorSlot.SlotId, newEncDek, newIv,
                chainEncryptedNewDekB64: chainEncB64,
                chainIvB64: chainIvB64);
            rewrapped = true;

            var auditRepo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
            await auditRepo.LogAsync(
                "dek_rotation",
                commitEventId,
                "dek_rotation_completed",
                "web",
                $"DEK rotation completed; epoch {payload.NewDekEpoch - 1}\u2192{payload.NewDekEpoch}; initiator={initiator.Id} ({initiator.DisplayName}); pre-rotation snapshot={snap.FileName}; auto-unlock agent keys removed={agentsDeleted}");

            _logger.LogInformation(
                "DEK rotation completed. Epoch {OldEpoch}\u2192{NewEpoch}. Initiator={Initiator} ({InitiatorName}). Snapshot={Snap}. AutoUnlockAgentsRemoved={Agents}.",
                payload.NewDekEpoch - 1, payload.NewDekEpoch, initiator.Id, initiator.DisplayName, snap.FileName, agentsDeleted);
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
