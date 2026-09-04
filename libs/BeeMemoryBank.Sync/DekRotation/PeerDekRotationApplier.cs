using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BeeMemoryBank.Sync.DekRotation;

/// <summary>
/// Applies a DEK rotation committed by another node, for hosts that have no API layer of their own
/// — mobile and CLI.
///
/// <para>
/// These hosts previously registered a no-op applier. A rotation reached them, logged
/// "node will fall behind cluster on next rotation", and did nothing: the node kept its retired
/// master DEK while the rest of the cluster moved on. Every article arriving afterwards carried a
/// DEK wrapped under a key it did not have, and <c>EventApplier</c> stored those rows verbatim —
/// so the content synced fine and was simply unreadable, with no error at the point of failure.
/// The gap was unreachable only for as long as rotation itself was broken; fixing rotation made it
/// live.
/// </para>
///
/// <para>
/// The server keeps its own <c>DekRotationService</c> because it also proposes and accepts
/// rotations, tracks progress for the admin UI and runs post-rotation compaction. Both paths run
/// the identical rewrap (<see cref="DekRewrapper"/>) — the difference here is only what surrounds
/// it.
/// </para>
/// </summary>
public sealed class PeerDekRotationApplier(
    IServiceScopeFactory scopeFactory,
    SessionService sessionService,
    DbConnectionFactory connFactory,
    MaintenanceModeService maintenance,
    ILogger<PeerDekRotationApplier> logger) : IDekRotationApplier
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _executeLock = new(1, 1);

    public async Task AutoAcceptCommitAsync(SyncEvent commitEvent)
    {
        if (!await _executeLock.WaitAsync(TimeSpan.Zero))
            throw new InvalidOperationException("Another rotation is in progress.");

        try
        {
            // A locked node cannot rewrap: it has no master DEK to unwrap the new one with. This
            // is the ordinary case for a phone syncing in the background, not an error — the row
            // stays Committing and SessionService replays it through RetryPendingAutoAcceptsAsync
            // on the next unlock.
            if (!sessionService.IsUnlocked)
                throw new SessionLockedException("Session is locked; auto-accept requires unlocked session.");

            var payload = JsonSerializer.Deserialize<DekRotationCommitPayload>(commitEvent.Payload, JsonOpts)
                ?? throw new InvalidOperationException("Failed to deserialize commit payload.");

            using var verifyScope = scopeFactory.CreateScope();
            var whitelistRepo = verifyScope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var originator = await whitelistRepo.GetByNodeIdAsync(commitEvent.NodeId)
                ?? throw new InvalidOperationException($"Originator node {commitEvent.NodeId} not in whitelist.");

            // Signature check before anything destructive: this event destroys every wrapped key in
            // the vault, so it must provably come from the pinned key of a whitelisted peer.
            var sigPayload = EventSignature.BuildPayload(commitEvent);
            if (!Ed25519Signer.Verify(originator.Ed25519PublicKey, sigPayload, commitEvent.Signature))
                throw new InvalidOperationException("Commit event signature verification failed (originator key).");

            await HeavyOperationLock.Instance.WaitAsync();
            try
            {
                maintenance.Enter("DEK rotation auto-accept in progress…");
                try
                {
                    await ApplyCoreAsync(commitEvent, payload);
                }
                finally
                {
                    maintenance.Exit();
                }
            }
            finally
            {
                HeavyOperationLock.Instance.Release();
            }
        }
        finally
        {
            _executeLock.Release();

            // Two COMMITs delivered in one sync batch: the second would hit "another rotation in
            // progress" and never retry, because its event is already in tbl_event and sync will
            // not redeliver it. Sweep once the lock is free. Bounded by the lock plus the number
            // of Committing rows.
            _ = Task.Run(async () =>
            {
                try { await RetryPendingAutoAcceptsAsync(); }
                catch (Exception ex) { logger.LogWarning(ex, "Post-auto-accept retry sweep failed"); }
            });
        }
    }

    private async Task ApplyCoreAsync(SyncEvent commitEvent, DekRotationCommitPayload payload)
    {
        using var scope = scopeFactory.CreateScope();
        var stateRepo = scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>();

        byte[]? oldDek = null;
        byte[]? newDek = null;
        bool completed = false;

        try
        {
            var encNewDekBytes = Convert.FromBase64String(payload.EncryptedNewDek);
            var ivBytes = Convert.FromBase64String(payload.Iv);

            oldDek = sessionService.GetMasterDek();
            try
            {
                newDek = MasterKeyManager.UnwrapMasterDek(encNewDekBytes, ivBytes, oldDek);
            }
            finally
            {
                Array.Clear(encNewDekBytes, 0, encNewDekBytes.Length);
            }

            // The base64 strings straight off the payload, not the decoded bytes: they are what
            // LazySlotRewrapService needs to walk this rotation later, and the decoded copy above
            // has already been cleared by this point. Storing them locally is what keeps a user's
            // key slot re-wrappable after compaction removes the event they came from.
            var (agentsDeleted, recoveryDeleted) = await DekRewrapper.RewrapAllAsync(
                connFactory, sessionService,
                oldDek, newDek, payload.NewDekEpoch, commitEvent.EventId.ToString(),
                isInitiator: false,
                chainEncryptedNewDekB64: payload.EncryptedNewDek,
                chainIvB64: payload.Iv);

            completed = true;
            logger.LogInformation(
                "DEK rotation auto-accept completed. Epoch {OldEpoch}→{NewEpoch}. Agents={Agents}. RecoverySlots={Recovery}.",
                payload.NewDekEpoch - 1, payload.NewDekEpoch, agentsDeleted, recoveryDeleted);
        }
        catch (Exception ex)
        {
            await stateRepo.UpdateStateAsync(commitEvent.EventId.ToString(), DekRotationState.Failed, ex.Message);
            logger.LogError(ex, "DEK rotation auto-accept failed for commit event {CommitEventId}", commitEvent.EventId);
            throw;
        }
        finally
        {
            // On success RewrapAllAsync already cleared oldDek and handed newDek to SessionService;
            // clearing them again here would zero the live master DEK.
            if (!completed)
            {
                if (oldDek != null) Array.Clear(oldDek);
                if (newDek != null) Array.Clear(newDek);
            }
        }
    }

    /// <summary>
    /// Re-dispatches any rotation left in Committing — the state a COMMIT reaches when it arrives
    /// while the node is locked, which on a phone is most of the time. Called by
    /// <c>SessionService</c> after every successful unlock.
    /// </summary>
    public async Task RetryPendingAutoAcceptsAsync()
    {
        if (!sessionService.IsUnlocked) return;

        using var scope = scopeFactory.CreateScope();
        var stateRepo = scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>();
        var eventRepo = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var whitelistRepo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();

        var pending = await stateRepo.GetByStateAsync(DekRotationState.Committing);
        if (pending.Count == 0) return;

        var localNodeId = (await nodeRepo.GetAsync())?.NodeId.ToString();

        foreach (var row in pending)
        {
            try
            {
                var commit = await eventRepo.GetByIdAsync(row.EventId);
                if (commit == null) continue;

                // Never auto-accept our own commit — the initiating path owns that.
                if (commit.NodeId.ToString().Equals(localNodeId, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Auto-accept is opt-in per peer. Without this check a node would silently apply a
                // vault-wide key rotation from any whitelisted peer the operator had not agreed to
                // follow.
                if (!await whitelistRepo.GetAutoAcceptDekRotationAsync(commit.NodeId.ToString()))
                    continue;

                logger.LogInformation(
                    "Retrying auto-accept for previously-deferred DEK rotation commit {EventId} from {NodeId}",
                    row.EventId, commit.NodeId);
                await AutoAcceptCommitAsync(commit);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RetryPendingAutoAcceptsAsync failed for event {EventId}", row.EventId);
            }
        }
    }
}
