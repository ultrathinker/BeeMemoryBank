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
    public async Task<Guid> ProposeRotationAsync(string masterPassword, int? initiatorUserId = null)
    {
        if (!await _executeLock.WaitAsync(TimeSpan.Zero))
            throw new InvalidOperationException("Another rotation is in progress.");
        try
        {
            if (!_sessionService.IsUnlocked)
                throw new InvalidOperationException("Session is locked. Unlock first.");

            using var scope = _scopeFactory.CreateScope();
            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var keySlotRepo = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
            var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
            var eventLogRepo = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
            var clock = scope.ServiceProvider.GetRequiredService<ILamportClock>();
            var stateRepo = scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>();

            // Guard against starting a new rotation while a previous one is still pending.
            // _executeLock is released between Propose and Accept, so without this DB check a
            // user could fire two Propose calls in a row and create two pending COMMIT events,
            // confusing the UI and the state machine. Surfaced by p9 integration test.
            var pendingCommitting = await stateRepo.GetByStateAsync(DekRotationState.Committing);
            if (pendingCommitting.Count > 0)
                throw new InvalidOperationException("Another rotation is in progress (pending Accept).");

            // (The big try-catch comment moved to AcceptCommitCoreAsync where it actually
            // applies — Propose doesn't have the same Failed-state-leak risk because it runs
            // synchronously and returns before any persistent state machine is engaged.)
            User initiator;
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
                        "No active superadmin with a key slot found on this node. A superadmin " +
                        "promoted but not yet logged in has no slot yet — have one log in first.");
            }

            if (initiator.KeySlotId == null)
                throw new InvalidOperationException("Initiator has no key slot.");

            var allSlots = await keySlotRepo.GetAllAsync();
            var slot = allSlots.FirstOrDefault(s => s.SlotId == initiator.KeySlotId)
                ?? throw new InvalidOperationException("Initiator key slot not found.");

            if (slot.Salt == null || !slot.ArgonMemory.HasValue || !slot.ArgonIterations.HasValue || !slot.ArgonParallelism.HasValue)
                throw new InvalidOperationException("Initiator key slot missing Argon2 parameters.");

            var kek = KeyDerivation.DeriveKek(
                masterPassword,
                slot.Salt,
                slot.ArgonMemory.Value,
                slot.ArgonIterations.Value,
                slot.ArgonParallelism.Value);

            byte[] unwrappedDek;
            try
            {
                unwrappedDek = MasterKeyManager.UnwrapMasterDek(slot.EncryptedMasterDek, slot.IV, kek);
            }
            catch (CryptographicException)
            {
                throw new UnauthorizedAccessException("Wrong master password.");
            }

            var currentDek = _sessionService.GetMasterDek();
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(unwrappedDek, currentDek))
                    throw new UnauthorizedAccessException("Wrong master password.");
            }
            finally
            {
                Array.Clear(unwrappedDek);
                Array.Clear(currentDek);
                Array.Clear(kek);
            }

            var newDek = MasterKeyManager.GenerateMasterDek();
            var oldDek = _sessionService.GetMasterDek();
            try
            {
                var (encNewDek, ivNewDek) = MasterKeyManager.WrapMasterDek(newDek, oldDek);

                var identity = await nodeRepo.GetAsync()
                    ?? throw new InvalidOperationException("Node is not initialized.");

                int currentEpoch;
                using (var epochConn = _connFactory.CreateConnection())
                {
                    // node_id is stored uppercase (Dapper Guid serialization), so compare
                    // case-insensitively. Without this, the SELECT misses and returns
                    // default(int)=0, making the very first rotation always go 0→1 and every
                    // subsequent rotation also go 0→1 (epoch UPDATE happens against a row that
                    // exists and works — but currentEpoch read by Propose is wrong).
                    currentEpoch = await epochConn.ExecuteScalarAsync<int>(
                        "SELECT dek_epoch FROM tbl_node_identity WHERE node_id = @nodeId COLLATE NOCASE",
                        new { nodeId = identity.NodeId.ToString() });
                }
                var newEpoch = currentEpoch + 1;

                var proposedPayload = new DekRotationProposedPayload(
                    EncryptedNewDek: Convert.ToBase64String(encNewDek),
                    Iv: Convert.ToBase64String(ivNewDek),
                    NewDekEpoch: newEpoch,
                    RotationTs: DateTime.UtcNow.ToString("O"),
                    ExpiresAt: DateTime.UtcNow.AddHours(24).ToString("O"),
                    OriginatorNodeId: identity.NodeId.ToString()
                );

                var proposedEventId = Guid.NewGuid();
                var lamportTs = clock.Tick();
                var proposedPayloadJson = JsonSerializer.Serialize(proposedPayload, JsonOpts);

                var proposedEvent = new SyncEvent
                {
                    EventId = proposedEventId,
                    NodeId = identity.NodeId,
                    LamportTs = lamportTs,
                    EventType = EventTypes.DekRotationProposed,
                    Payload = proposedPayloadJson,
                    Signature = [],
                    ProtocolVersion = 1,
                    CreatedAt = DateTime.UtcNow,
                    ActorType = "web",
                    ActorName = initiator.DisplayName
                };

                var sigPayload = EventSignature.BuildPayload(proposedEvent);
                proposedEvent.Signature = NodeIdentityCrypto.SignWithIdentityOrGetDek(
                    identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                    identity.NodeId, () => _sessionService.GetMasterDek(), sigPayload);
                await eventLogRepo.AppendAsync(proposedEvent);

                _progress.Update(DekRotationFlowStep.Proposing, 10, "Proposed; awaiting commit", eventId: proposedEventId.ToString());
                _progress.ClearError();

                await stateRepo.UpsertAsync(new DekRotationStateRow(
                    EventId: proposedEventId.ToString(),
                    State: DekRotationState.Proposed,
                    ProposedEventId: proposedEventId.ToString(),
                    RotationTs: proposedPayload.RotationTs,
                    AppliedAt: null,
                    ErrorMessage: null,
                    LastProcessedIdArticle: null,
                    LastProcessedIdArticleVersion: null,
                    LastProcessedIdMedia: null,
                    LastProcessedIdConflictVersion: null,
                    LastProcessedIdComment: null,
                    CreatedAt: DateTime.UtcNow.ToString("O"),
                    UpdatedAt: DateTime.UtcNow.ToString("O")
                ));

                // MVP: skip quorum — immediately build COMMIT event.
                var commitPayload = new DekRotationCommitPayload(
                    ProposedEventId: proposedEventId.ToString(),
                    EncryptedNewDek: Convert.ToBase64String(encNewDek),
                    Iv: Convert.ToBase64String(ivNewDek),
                    NewDekEpoch: newEpoch,
                    RotationTs: proposedPayload.RotationTs,
                    OriginatorNodeId: identity.NodeId.ToString()
                );

                var commitEventId = Guid.NewGuid();
                var commitLamportTs = clock.Tick();
                var commitPayloadJson = JsonSerializer.Serialize(commitPayload, JsonOpts);

                var commitEvent = new SyncEvent
                {
                    EventId = commitEventId,
                    NodeId = identity.NodeId,
                    LamportTs = commitLamportTs,
                    EventType = EventTypes.DekRotationCommit,
                    // Same value as proposedEventId.ToString(), but taken through the shared rule:
                    // a peer reconstructs EntityId from the signed payload (EntityId itself is not
                    // signed), so the two must never be written independently. See EventEntityId.
                    EntityId = EventEntityId.Derive(EventTypes.DekRotationCommit, null, commitPayloadJson),
                    Payload = commitPayloadJson,
                    Signature = [],
                    ProtocolVersion = 1,
                    CreatedAt = DateTime.UtcNow,
                    ActorType = "web",
                    ActorName = initiator.DisplayName
                };

                var commitSigPayload = EventSignature.BuildPayload(commitEvent);
                commitEvent.Signature = NodeIdentityCrypto.SignWithIdentityOrGetDek(
                    identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                    identity.NodeId, () => _sessionService.GetMasterDek(), commitSigPayload);
                await eventLogRepo.AppendAsync(commitEvent);

                _progress.Update(DekRotationFlowStep.Committing, 15, "Commit event created; awaiting AcceptCommit call.");

                await stateRepo.UpsertAsync(new DekRotationStateRow(
                    EventId: commitEventId.ToString(),
                    State: DekRotationState.Committing,
                    ProposedEventId: proposedEventId.ToString(),
                    RotationTs: proposedPayload.RotationTs,
                    AppliedAt: null,
                    ErrorMessage: null,
                    LastProcessedIdArticle: null,
                    LastProcessedIdArticleVersion: null,
                    LastProcessedIdMedia: null,
                    LastProcessedIdConflictVersion: null,
                    LastProcessedIdComment: null,
                    CreatedAt: DateTime.UtcNow.ToString("O"),
                    UpdatedAt: DateTime.UtcNow.ToString("O")
                ));

                // Clear local copies of the new DEK material. AcceptCommitAsync will
                // re-derive it from the commit event payload.
                Array.Clear(encNewDek, 0, encNewDek.Length);
                Array.Clear(ivNewDek, 0, ivNewDek.Length);

                return commitEventId;
            }
            finally
            {
                Array.Clear(newDek, 0, newDek.Length);
                Array.Clear(oldDek, 0, oldDek.Length);
            }
        }
        finally
        {
            _executeLock.Release();
        }
    }
}
