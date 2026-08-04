using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
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
            throw new InvalidOperationException("Another rotation is in progress.");
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

        _progress.Update(DekRotationFlowStep.PreRotationBackup, 18, "Creating pre-rotation backup...");

        var snap = await snapshotService.CreateAsync(filterSecrets: false, sign: false, cpSequenceNum: null);
        _logger.LogInformation("DEK rotation: pre-rotation snapshot created: {FileName}", snap.FileName);

        // Decrypt new DEK + run pre-validation INSIDE the state-setting try-catch. Otherwise
        // a CryptographicException from a corrupted payload bubbles past the state machine,
        // leaves _progress.Step stuck at Committing, AND leaks oldDek (no finally reaches it).
        // (Found by Gemini R3 reviewer of god-class refactor.)
        byte[]? oldDek = null;
        byte[]? newDek = null;

        User initiator;
        BeeMemoryBank.Core.Models.MasterKeyStore initiatorSlot;
        byte[]? localKek = null;
        try
        {
            var encNewDekBytes = Convert.FromBase64String(payload.EncryptedNewDek);
            var ivBytes = Convert.FromBase64String(payload.Iv);

            oldDek = _sessionService.GetMasterDek();
            try
            {
                newDek = MasterKeyManager.UnwrapMasterDek(encNewDekBytes, ivBytes, oldDek);
            }
            finally
            {
                Array.Clear(encNewDekBytes, 0, encNewDekBytes.Length);
            }

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
                initiator = users.FirstOrDefault(u => u.Role == UserRoles.Superadmin)
                    ?? throw new InvalidOperationException("No active superadmin found.");
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
            // node would be unrecoverable except via the pre-rotation snapshot. Pre-existing in
            // B3, surfaced by Gemini reviewer at p2.
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
            // section finally (line ~700) only runs if we actually entered destructive code.
            // localKek added per Gemini security review of tail-A: was leaked when password
            // verify threw UnauthorizedAccessException after KEK derivation.
            if (oldDek != null) Array.Clear(oldDek);
            if (newDek != null) Array.Clear(newDek);
            if (localKek != null) Array.Clear(localKek);
            throw;
        }

        try
        {
            var (newEncDek, newIv) = MasterKeyManager.WrapMasterDek(newDek, localKek);

            var (agentsDeleted, _) = await RewrapDestructiveCoreAsync(
                oldDek, newDek, payload.NewDekEpoch, commitEventId,
                isInitiator: true, initiatorSlot.SlotId, newEncDek, newIv);

            var auditRepo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
            await auditRepo.LogAsync(
                "dek_rotation",
                commitEventId,
                "dek_rotation_completed",
                "web",
                $"DEK rotation completed; epoch {payload.NewDekEpoch - 1}\u2192{payload.NewDekEpoch}; initiator={initiator.Id} ({initiator.DisplayName}); pre-rotation snapshot={snap.FileName}; agents invalidated={agentsDeleted}");

            _logger.LogInformation(
                "DEK rotation completed. Epoch {OldEpoch}\u2192{NewEpoch}. Initiator={Initiator} ({InitiatorName}). Snapshot={Snap}. Agents invalidated={Agents}.",
                payload.NewDekEpoch - 1, payload.NewDekEpoch, initiator.Id, initiator.DisplayName, snap.FileName, agentsDeleted);
        }
        catch (Exception ex)
        {
            _progress.Update(DekRotationFlowStep.Failed, err: ex.Message, msg: "DEK rotation failed.");
            await stateRepo.UpdateStateAsync(commitEventId, DekRotationState.Failed, ex.Message);
            // AUDIT NOTE: on failure we do NOT swap DEK, so the old DEK remains active.
            // We DO exit maintenance mode so the node is usable (with old DEK).
            // Re-try requires a new Propose+Accept cycle.

            // Clean up the pre-rotation snapshot — without this, every failed rotation leaves
            // a ~DBsize .tar.gz behind. With repeated retries on a 1GB DB, the snapshots
            // directory fills and the disk-space pre-check then BLOCKS future rotations.
            // (Claude R2 prod review HIGH-2.)
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

            _logger.LogError(ex, "DEK rotation failed for commit event {CommitEventId}", commitEventId);
            throw;
        }
        finally
        {
            Array.Clear(localKek, 0, localKek.Length);
            // Clear key material on the error path. On success path, oldDek was already cleared
            // inside RewrapDestructiveCoreAsync and newDek ownership transferred to SessionService.SwapMasterDek.
            // (Found by Kilo R1 security review CRIT-1.)
            if (_progress.Step != DekRotationFlowStep.Completed)
            {
                Array.Clear(oldDek, 0, oldDek.Length);
                Array.Clear(newDek, 0, newDek.Length);
            }
        }
    }
}
