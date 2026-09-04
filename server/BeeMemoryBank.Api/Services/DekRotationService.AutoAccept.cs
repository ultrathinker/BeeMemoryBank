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
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

public partial class DekRotationService
{
    /// <summary>
    /// Scans tbl_dek_rotation_state for Committing rows whose originator has
    /// auto_accept_dek_rotation enabled, and re-dispatches AutoAcceptCommitAsync for each.
    /// Called after a successful unlock to handle the case where COMMIT arrived while the
    /// session was locked. (Claude R2 prod review CRIT-1.)
    /// </summary>
    public async Task RetryPendingAutoAcceptsAsync()
    {
        if (!_sessionService.IsUnlocked) return;

        using var scope = _scopeFactory.CreateScope();
        var stateRepo = scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>();
        var eventRepo = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var whitelistRepo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();

        var pending = await stateRepo.GetByStateAsync(DekRotationState.Committing);
        if (pending.Count == 0) return;

        var localIdentity = await nodeRepo.GetAsync();
        var localNodeId = localIdentity?.NodeId.ToString();

        foreach (var row in pending)
        {
            try
            {
                var commit = await eventRepo.GetByIdAsync(row.EventId);
                if (commit == null) continue;
                if (commit.NodeId.ToString().Equals(localNodeId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var autoAccept = await whitelistRepo.GetAutoAcceptDekRotationAsync(commit.NodeId.ToString());
                if (!autoAccept) continue;

                _logger.LogInformation(
                    "Retrying auto-accept for previously-deferred DEK rotation commit {EventId} from {NodeId}",
                    row.EventId, commit.NodeId);
                await AutoAcceptCommitAsync(commit);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RetryPendingAutoAcceptsAsync failed for event {EventId}", row.EventId);
            }
        }
    }

    public async Task AutoAcceptCommitAsync(SyncEvent commitEvent)
    {
        if (!await _executeLock.WaitAsync(TimeSpan.Zero))
            throw new ConflictException("Another rotation is in progress.");

        try
        {
            if (!_sessionService.IsUnlocked)
                throw new SessionLockedException("Session is locked; auto-accept requires unlocked session.");

            var payload = JsonSerializer.Deserialize<DekRotationCommitPayload>(commitEvent.Payload, JsonOpts)
                ?? throw new InvalidOperationException("Failed to deserialize commit payload.");

            using var verifyScope = _scopeFactory.CreateScope();
            var whitelistRepo = verifyScope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var originator = await whitelistRepo.GetByNodeIdAsync(commitEvent.NodeId)
                ?? throw new InvalidOperationException($"Originator node {commitEvent.NodeId} not in whitelist.");

            var sigPayload = EventSignature.BuildPayload(commitEvent);
            if (!Ed25519Signer.Verify(originator.Ed25519PublicKey, sigPayload, commitEvent.Signature))
                throw new InvalidOperationException("Commit event signature verification failed (originator key).");

            await HeavyOperationLock.Instance.WaitAsync();
            bool runPostCompaction = false;
            try
            {
                _maintenance.Enter("DEK rotation auto-accept in progress\u2026");
                try
                {
                    await AutoAcceptCommitCoreAsync(commitEvent, payload);
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
            {
                try
                {
                    using var compactionScope = _scopeFactory.CreateScope();
                    var compactionService = compactionScope.ServiceProvider.GetRequiredService<CompactionService>();
                    await compactionService.ExecuteAsync(reason: "dek-rotation-auto-accept");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DEK rotation auto-accept: post-rotation compaction failed (non-fatal)");
                }
            }
        }
        finally
        {
            _executeLock.Release();

            // After releasing our lock, scan for any other pending auto-accept rows that
            // arrived in the same sync batch. Without this, two consecutive COMMITs delivered
            // together would only apply the first; the second would throw "Another rotation
            // in progress" and never retry (its event is already in tbl_event so sync won't
            // redeliver). Fire-and-forget — recursion is bounded by the lock + state row count.
            // (Found by E2E multi-rotation test on 2026-04-26.)
            _ = Task.Run(async () =>
            {
                try { await RetryPendingAutoAcceptsAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Post-auto-accept retry sweep failed"); }
            });
        }
    }

    private async Task AutoAcceptCommitCoreAsync(SyncEvent commitEvent, DekRotationCommitPayload payload)
    {
        using var scope = _scopeFactory.CreateScope();
        var stateRepo = scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>();

        _progress.Update(DekRotationFlowStep.Committing, 15, "Auto-accept: decrypting new DEK...", eventId: commitEvent.EventId.ToString());
        _progress.ClearError();

        // Decrypt new DEK INSIDE the state-setting try-catch (parallel to AcceptCommitCoreAsync
        // fix). A CryptographicException from a corrupted payload otherwise stuck _progress at
        // Committing and leaked oldDek. (Gemini R3 reviewer of god-class refactor.)
        byte[]? oldDek = null;
        byte[]? newDek = null;

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

            var (agentsDeleted, recoveryDeleted) = await RewrapDestructiveCoreAsync(
                oldDek, newDek, payload.NewDekEpoch, commitEvent.EventId.ToString(),
                isInitiator: false);

            _logger.LogInformation(
                "DEK rotation auto-accept completed. Epoch {OldEpoch}\u2192{NewEpoch}. Agents={Agents}. RecoverySlots={Recovery}.",
                payload.NewDekEpoch - 1, payload.NewDekEpoch, agentsDeleted, recoveryDeleted);
        }
        catch (Exception ex)
        {
            _progress.Update(DekRotationFlowStep.Failed, err: ex.Message, msg: "DEK rotation auto-accept failed.");
            await stateRepo.UpdateStateAsync(commitEvent.EventId.ToString(), DekRotationState.Failed, ex.Message);
            _logger.LogError(ex, "DEK rotation auto-accept failed for commit event {CommitEventId}", commitEvent.EventId);
            throw;
        }
        finally
        {
            // Clear key material on the error path. (Kilo R1 security review CRIT-1.)
            // Success path already cleared oldDek + transferred newDek to SessionService.
            if (_progress.Step != DekRotationFlowStep.Completed)
            {
                if (oldDek != null) Array.Clear(oldDek);
                if (newDek != null) Array.Clear(newDek);
            }
        }
    }
}
