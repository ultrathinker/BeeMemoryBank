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
    /// <summary>
    /// Scans tbl_dek_rotation_state for Committing rows whose originator has
    /// auto_accept_dek_rotation enabled, and re-dispatches AutoAcceptCommitAsync for each.
    /// Called after a successful unlock to handle the case where COMMIT arrived while the
    /// session was locked.
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

                // A removal rotation of a superadmin node is applied whatever the setting says (2.12, DekRotationReasons).
                if (!(await DekRotationReasons.DecideAutoApplyAsync(commit, whitelistRepo)).Apply) continue;

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

        var deferred = false;
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

            // The initiator's current key, or the key it retired for a commit it signed before its re-key (plan WS-D D3): the live rule at first
            // delivery, the package rule for the commit this node already holds (it passed the receipt check).
            var mode = await BeeMemoryBank.Sync.NodeEventVerifier.ReceiptModeAsync(
                verifyScope.ServiceProvider.GetRequiredService<IEventLogRepository>(), commitEvent);
            if (!await BeeMemoryBank.Sync.NodeEventVerifier.VerifyAsync(_connFactory, commitEvent, originator.Ed25519PublicKey, mode))
                throw new InvalidOperationException("Commit event signature verification failed (originator key).");

            // The node-local key-operation lock (2.12): busy is "not yet" (the commit stays pending and is retried), not a failure.
            using var keyOperation = TryEnterKeyOperation(out var refusal);
            if (keyOperation is null)
            {
                deferred = true;
                throw new DekRotationPreconditionException(refusal!);
            }
            await HeavyOperationLock.Instance.WaitAsync();
            bool runPostCompaction = false;
            try
            {
                _maintenance.Enter("DEK rotation auto-accept in progress\u2026");
                try
                {
                    runPostCompaction = await AutoAcceptCommitCoreAsync(commitEvent, payload);
                }
                catch (DekRotationPreconditionException)
                {
                    deferred = true;
                    throw;
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
                PublishCompleted();
                _deferredRetry.Reset();
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
            // Not after a deferral: that row is
            // still Committing, so the sweep would pick it straight back up, fail the same
            // precondition and sweep again, forever. A deferral instead schedules one bounded,
            // backed-off retry (so an always-unlocked server does not wait for an unlock that never
            // comes); the next unlock retries too.
            if (!deferred)
            {
                _ = Task.Run(async () =>
                {
                    try { await RetryPendingAutoAcceptsAsync(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Post-auto-accept retry sweep failed"); }
                });
            }
            else
            {
                _deferredRetry.Schedule(RetryPendingAutoAcceptsAsync, _logger);
            }
        }
    }

    /// <returns>True when the rotation was applied; false when it was skipped as already settled.</returns>
    private async Task<bool> AutoAcceptCommitCoreAsync(SyncEvent commitEvent, DekRotationCommitPayload payload)
    {
        using var scope = _scopeFactory.CreateScope();
        var stateRepo = scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>();

        // A commit this node has already applied (or cancelled, or rejected) is a no-op. Sync
        // re-delivers events, and re-running an applied rotation would treat the CURRENT DEK as the
        // old one. Checked here — under _executeLock and the heavy-operation lock, which the rewrap
        // also runs under — so the check and the rewrap cannot interleave with another apply.
        var existing = await stateRepo.GetAsync(commitEvent.EventId.ToString());
        if (existing?.State is DekRotationState.Applied or DekRotationState.Cancelled or DekRotationState.Rejected)
        {
            _logger.LogInformation(
                "DEK rotation commit {CommitEventId} is already {State}; auto-accept skipped (no-op)",
                commitEvent.EventId, existing.State);
            return false;
        }

        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");

        _progress.Update(DekRotationFlowStep.Committing, 15, "Auto-accept: decrypting new DEK...", eventId: commitEvent.EventId.ToString());
        _progress.ClearError();

        // Decrypt new DEK INSIDE the state-setting try-catch, parallel to AcceptCommitCoreAsync:
        // a CryptographicException from a corrupted payload would otherwise stick _progress at
        // Committing and leak oldDek.
        byte[]? oldDek = null;
        byte[]? newDek = null;
        string? chainEncB64 = null;
        string? chainIvB64 = null;
        var rewrapped = false;

        try
        {
            oldDek = _sessionService.GetMasterDek();
            // Confidential rotation: open this node's own envelope; legacy events fall back to
            // unwrap-under-old-DEK. (ADR 0006.)
            newDek = DekRotationMaterial.ResolveNewDek(
                payload, commitEvent.EventId, identity, oldDek, out chainEncB64, out chainIvB64,
                await BeeMemoryBank.Sync.DekRotation.DekEnvelopeRefresh.FetchedAsync(_connFactory, commitEvent.EventId));

            // chainEncB64/chainIvB64 are what LazySlotRewrapService needs to walk this rotation after
            // compaction removes the event they arrived in — stored node-local, never synced.
            var (agentsDeleted, recoveryDeleted, _) = await RewrapDestructiveCoreAsync(
                oldDek, newDek, payload.NewDekEpoch, commitEvent.EventId.ToString(),
                isInitiator: false,
                chainEncryptedNewDekB64: chainEncB64,
                chainIvB64: chainIvB64);
            rewrapped = true;

            _logger.LogInformation(
                "DEK rotation auto-accept completed. Epoch {OldEpoch}\u2192{NewEpoch}. AutoUnlockAgentsRemoved={Agents}. OldFormatAutoUnlockDoorsRemoved={Doors}.",
                payload.NewDekEpoch - 1, payload.NewDekEpoch, agentsDeleted, recoveryDeleted);
            if (DekRotationReasons.IsRemoval(payload.Reason))
                await RemovalRotationRecord.WriteAsync(scope.ServiceProvider, commitEvent, payload, _logger);
            // The rotation's own retired link and the device box of the doors (2.12).
            PostRotationRecovery.Start(_scopeFactory, _sessionService, _logger);
            return true;
        }
        catch (DekRotationPreconditionException ex)
        {
            // The envelope was made for this node's previous identity key: ask the node that rotated for a fresh one (plan WS-D D4).
            if (ex is BeeMemoryBank.Sync.DekRotation.DekEnvelopeStaleException)
                await BeeMemoryBank.Sync.DekRotation.DekEnvelopeRefresh.RequestAsync(_connFactory, commitEvent.EventId, commitEvent.NodeId);
            // Nothing was changed (the rewrap never started), so this is "not yet", not "failed":
            // the row stays Committing and is retried (bounded automatic retry, next unlock, or an
            // admin applying it from the pending-rotation banner). Failed is terminal — nothing retries it — which would
            // strand this node on the retired DEK for good.
            _progress.Update(DekRotationFlowStep.Failed, err: ex.Message,
                msg: "DEK rotation auto-accept deferred; it stays pending and is retried automatically.");
            _logger.LogError(ex, "DEK rotation auto-accept deferred for commit event {CommitEventId}", commitEvent.EventId);
            throw;
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
            // Clear key material on the error path.
            // Success path already cleared oldDek + transferred newDek to SessionService. Keyed on
            // the rewrap having returned: Completed is published later, after maintenance ends.
            if (!rewrapped)
            {
                if (oldDek != null) Array.Clear(oldDek);
                if (newDek != null) Array.Clear(newDek);
            }
        }
    }
}
