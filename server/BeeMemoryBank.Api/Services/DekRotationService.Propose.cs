using System;
using System.Collections.Generic;
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
    public async Task<Guid> ProposeRotationAsync(string masterPassword, int? initiatorUserId = null)
    {
        // ConflictException, not a bare InvalidOperationException: /api/dek-rotation/propose has to
        // answer 409 for "already running" and 400 for every other refusal, and it used to decide
        // that with Message.Contains("in progress") — one reworded sentence away from a 400.
        if (!await _executeLock.WaitAsync(TimeSpan.Zero))
            throw new ConflictException("Another rotation is in progress.");
        try
        {
            if (!_sessionService.IsUnlocked)
                throw new SessionLockedException("Session is locked. Unlock first.");

            using var scope = _scopeFactory.CreateScope();
            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var keySlotRepo = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
            var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
            var eventLogRepo = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
            var clock = scope.ServiceProvider.GetRequiredService<ILamportClock>();
            var stateRepo = scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>();
            var whitelistRepo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

            // Guard against starting a new rotation while a previous one is still pending.
            // _executeLock is released between Propose and Accept, so without this DB check a
            // user could fire two Propose calls in a row and create two pending COMMIT events,
            // confusing the UI and the state machine. Surfaced by p9 integration test.
            var pendingCommitting = await stateRepo.GetByStateAsync(DekRotationState.Committing);
            if (pendingCommitting.Count > 0)
                throw new ConflictException("Another rotation is in progress (pending Accept).");

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

                // ADR 0006: the COMMIT event id is the rotation id that salts every envelope. Assign
                // it up front so both the PROPOSED and COMMIT payloads carry the SAME envelope set —
                // a receiver applies the COMMIT and opens peers[myNodeId] using commitEvent.EventId as
                // the salt, so the envelopes must be keyed to that id regardless of which event they
                // ride in.
                var commitEventId = Guid.NewGuid();

                // Recipients = the currently-active whitelist (status='A') PLUS this initiator node.
                // That snapshot IS the definition of "who this rotation includes": a revoked peer is
                // not enumerated and gets no openable envelope, which is exactly what makes the
                // rotation confidential against a node revoked before it.
                var activePeers = await whitelistRepo.GetAllActiveAsync();
                var recipients = new List<DekEnvelope.Recipient>(activePeers.Count + 1)
                {
                    new(identity.NodeId, identity.Ed25519PublicKey)
                };
                foreach (var peer in activePeers)
                {
                    // One malformed active whitelist key must not deny rotation to the whole mesh.
                    // Validate the birational map up front; a peer whose Ed25519 key will not convert
                    // is EXCLUDED (it gets no openable envelope and must re-join to catch up) rather
                    // than allowed to throw out of DekEnvelope.Build and abort every rotation forever.
                    // The initiator (recipients[0]) is deliberately NOT filtered: if its own key is
                    // unusable the rotation genuinely cannot proceed, and Build throwing is correct.
                    try
                    {
                        _ = DekEnvelope.Ed25519PublicKeyToX25519PublicKey(peer.Ed25519PublicKey);
                    }
                    catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException)
                    {
                        _logger.LogError(ex,
                            "DEK rotation: active whitelist peer {NodeId} has an unusable Ed25519 public key and is "
                            + "EXCLUDED from this rotation — it will receive no openable envelope and must re-join to "
                            + "catch up. The rotation proceeds for the remaining peers.", peer.NodeId);
                        continue;
                    }
                    recipients.Add(new DekEnvelope.Recipient(peer.NodeId, peer.Ed25519PublicKey));
                }

                var envelopeSet = DekEnvelope.Build(newDek, commitEventId, recipients);
                var dekEnvelopes = new DekEnvelopesPayload(
                    envelopeSet.EphemeralPublicKeyB64,
                    envelopeSet.Peers.ToDictionary(
                        kv => kv.Key,
                        kv => new DekEnvelopeBox(kv.Value.WrappedB64, kv.Value.NonceB64)));

                // Confidential rotation: ship the per-peer envelopes and OMIT encrypted_new_dek/iv
                // (ADR 0006, rollout Option B — new rotations are always confidential).
                var proposedPayload = new DekRotationProposedPayload(
                    NewDekEpoch: newEpoch,
                    RotationTs: DateTime.UtcNow.ToString("O"),
                    ExpiresAt: DateTime.UtcNow.AddHours(24).ToString("O"),
                    OriginatorNodeId: identity.NodeId.ToString(),
                    DekEnvelopes: dekEnvelopes
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

                // MVP: skip quorum — immediately build COMMIT event. Same envelope set as the
                // proposal; encrypted_new_dek/iv omitted (ADR 0006).
                var commitPayload = new DekRotationCommitPayload(
                    ProposedEventId: proposedEventId.ToString(),
                    NewDekEpoch: newEpoch,
                    RotationTs: proposedPayload.RotationTs,
                    OriginatorNodeId: identity.NodeId.ToString(),
                    DekEnvelopes: dekEnvelopes
                );

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

                // The new DEK is not kept in the clear anywhere in the synced payload; AcceptCommitAsync
                // re-derives it by opening this node's own envelope from the commit event.
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
