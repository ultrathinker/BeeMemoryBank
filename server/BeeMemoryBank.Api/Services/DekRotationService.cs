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

public class DekRotationService : IDekRotationApplier
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SessionService _sessionService;
    private readonly DbConnectionFactory _connFactory;
    private readonly MaintenanceModeService _maintenance;
    private readonly ILogger<DekRotationService> _logger;
    private readonly string _dataPath;

    private readonly ProgressState _progress = new();
    private readonly SemaphoreSlim _executeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed class ProgressState
    {
        private volatile DekRotationFlowStep _step = DekRotationFlowStep.Idle;
        private volatile int _pct;
        private volatile string? _msg;
        private volatile string? _err;
        private volatile string? _eventId;

        public DekRotationFlowStep Step => _step;
        public int Pct => _pct;
        public string? Msg => _msg;
        public string? Err => _err;
        public string? EventId => _eventId;

        public void Update(DekRotationFlowStep step, int? pct = null, string? msg = null,
            string? err = null, string? eventId = null)
        {
            _step = step;
            if (pct.HasValue) _pct = pct.Value;
            if (msg != null) _msg = msg;
            if (err != null) _err = err;
            if (eventId != null) _eventId = eventId;
        }

        public void ClearError() => _err = null;
        public void ClearEventId() => _eventId = null;
    }

    public DekRotationService(
        IServiceScopeFactory scopeFactory,
        SessionService sessionService,
        DbConnectionFactory connFactory,
        MaintenanceModeService maintenance,
        ILogger<DekRotationService> logger,
        string dataPath)
    {
        _scopeFactory = scopeFactory;
        _sessionService = sessionService;
        _connFactory = connFactory;
        _maintenance = maintenance;
        _logger = logger;
        _dataPath = dataPath;
    }

    public DekRotationProgressResponse GetProgress()
    {
        return new DekRotationProgressResponse(
            _progress.EventId != null ? Guid.Parse(_progress.EventId) : null,
            _progress.Step,
            _progress.Pct,
            _progress.Msg,
            _progress.Err);
    }

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
                initiator = users.FirstOrDefault(u => u.Role == UserRoles.Superadmin)
                    ?? throw new InvalidOperationException("No active superadmin found on this node.");
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
                    EntityId = proposedEventId.ToString(),
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

    /// <summary>
    /// Shared destructive core for both initiator Accept and peer AutoAccept paths.
    /// Opens a single atomic transaction, re-wraps all encrypted DEKs in 4 tables,
    /// deletes agents, handles slot cleanup (initiator: re-wrap own slot + delete others;
    /// auto-accept: delete recovery slots only), updates sentinel + epoch + state, commits,
    /// then swaps the in-memory master DEK and marks progress Completed.
    /// Returns (agentsDeleted, slotsDeleted) for caller-side logging.
    /// </summary>
    private async Task<(int agentsDeleted, int slotsDeleted)> RewrapDestructiveCoreAsync(
        byte[] oldDek, byte[] newDek, int newEpoch, string commitEventId,
        bool isInitiator,
        int? initiatorSlotId = null,
        byte[]? newWrappedSlotDek = null,
        byte[]? newWrappedSlotIv = null)
    {
        int agentsDeleted = 0;
        int slotsDeleted = 0;

        _progress.Update(DekRotationFlowStep.ReWrappingPerItem, 20,
            isInitiator ? "Re-wrapping article bodies..." : "Auto-accept: re-wrapping article bodies...");

        using var conn = _connFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            ReWrapTableAsync(conn, tx, "tbl_article_body", "article_id", "encrypted_dek", "dek_iv", oldDek, newDek);
            _progress.Update(DekRotationFlowStep.ReWrappingPerItem, 35,
                isInitiator ? "Re-wrapping article versions..." : "Auto-accept: re-wrapping article versions...");

            ReWrapTableAsync(conn, tx, "tbl_article_version", "id", "encrypted_dek", "dek_iv", oldDek, newDek);
            _progress.Update(DekRotationFlowStep.ReWrappingPerItem, 50,
                isInitiator ? "Re-wrapping conflict versions..." : "Auto-accept: re-wrapping conflict versions...");

            ReWrapTableAsync(conn, tx, "tbl_conflict_version", "id", "encrypted_dek", "dek_iv", oldDek, newDek);
            _progress.Update(DekRotationFlowStep.ReWrappingPerItem, 65,
                isInitiator ? "Re-wrapping media..." : "Auto-accept: re-wrapping media...");

            ReWrapTableAsync(conn, tx, "tbl_media", "id", "encrypted_dek", "dek_iv", oldDek, newDek);

            _progress.Update(DekRotationFlowStep.InvalidatingAgents, 75,
                isInitiator ? "Invalidating agents..." : "Auto-accept: invalidating agents...");

            // --- tbl_agent: agents hold API keys encrypted with the old DEK; server
            // cannot re-wrap them (no access to plaintext keys). Delete all agents.
            agentsDeleted = await conn.ExecuteAsync("DELETE FROM tbl_agent", transaction: tx);

            if (isInitiator)
            {
                _progress.Update(DekRotationFlowStep.InvalidatingSlots, 80,
                    "Re-wrapping initiator key slot, removing others...");

                await conn.ExecuteAsync(
                    "UPDATE tbl_key_slot SET encrypted_master_dek = @encDek, iv = @iv WHERE slot_id = @slotId",
                    new { encDek = newWrappedSlotDek, iv = newWrappedSlotIv, slotId = initiatorSlotId!.Value }, tx);

                slotsDeleted = await conn.ExecuteAsync(
                    "DELETE FROM tbl_key_slot WHERE slot_id <> @slotId",
                    new { slotId = initiatorSlotId!.Value }, tx);

                await conn.ExecuteAsync(
                    "UPDATE tbl_user SET key_slot_id = NULL WHERE key_slot_id IS NOT NULL AND key_slot_id <> @slotId",
                    new { slotId = initiatorSlotId!.Value }, tx);
            }
            else
            {
                _progress.Update(DekRotationFlowStep.InvalidatingAgents, 80,
                    "Auto-accept: removing recovery key slots...");

                slotsDeleted = await conn.ExecuteAsync(
                    "DELETE FROM tbl_key_slot WHERE slot_type = 'recovery'", transaction: tx);
            }

            _progress.Update(DekRotationFlowStep.Finalizing, 85,
                isInitiator ? "Updating sentinel and epoch..." : "Auto-accept: updating sentinel and epoch...");

            var newSentinel = MasterKeyManager.ComputeSentinel(newDek);
            await conn.ExecuteAsync(
                "UPDATE tbl_node_identity SET sentinel_value = @sentinel, dek_epoch = @epoch",
                new { sentinel = newSentinel, epoch = newEpoch }, tx);

            // Mark Applied INSIDE the rotation tx. If the process crashes between
            // tx.Commit() and the swap, the DB+state agree (Applied + new sentinel);
            // the startup sweep won't mark this as Failed.
            await conn.ExecuteAsync(
                @"UPDATE tbl_dek_rotation_state SET state = @state, applied_at = @now, updated_at = @now
                  WHERE event_id = @eventId",
                new { state = DekRotationState.Applied.ToString().ToUpperInvariant(), now = DateTime.UtcNow.ToString("O"), eventId = commitEventId },
                tx);

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        _sessionService.SwapMasterDek(newDek);
        // Do NOT clear newDek — ownership transferred to SessionService.
        Array.Clear(oldDek, 0, oldDek.Length);

        var completedMsg = isInitiator
            ? $"DEK rotation completed. Epoch {newEpoch - 1}\u2192{newEpoch}. Agents invalidated: {agentsDeleted}."
            : $"DEK rotation auto-accept completed. Epoch {newEpoch - 1}\u2192{newEpoch}. Agents invalidated: {agentsDeleted}. Recovery slots removed: {slotsDeleted}.";
        _progress.Update(DekRotationFlowStep.Completed, 100, completedMsg);
        _progress.ClearError();

        return (agentsDeleted, slotsDeleted);
    }

    /// <summary>
    /// Builds AAD for a per-row DEK wrap. Format must match the encrypt-side AAD used
    /// when the row was created. For Wave 1 v=1 rows, AAD includes a table-specific
    /// prefix and the row's primary-key bytes (article_id / media_id). For v=0 rows
    /// (legacy plaintext wrap) returns null — DekManager.UnwrapDek handles that path.
    /// </summary>
    private static byte[]? BuildPerRowAadForTable(string tableName, string pk, byte[] wrapped)
    {
        // v=0 legacy: exactly 48 bytes, no version prefix → no AAD
        if (wrapped.Length == 48) return null;
        // Anything else: assume v=1 (49 bytes with 0x01 prefix). DekManager validates strictly.
        var prefix = tableName switch
        {
            "tbl_article_body" => "bmb-art-dek"u8.ToArray(),
            "tbl_article_version" => "bmb-art-dek"u8.ToArray(),
            "tbl_conflict_version" => "bmb-art-dek"u8.ToArray(),
            "tbl_media" => "bmb-media-dek"u8.ToArray(),
            _ => null
        };
        if (prefix == null) return null;
        // PK is the article_id / media_id GUID (string form). Convert back to bytes via Guid.
        if (!Guid.TryParse(pk, out var pkGuid))
        {
            // Some tables (tbl_article_version, tbl_conflict_version) use a separate row id
            // as PK, not articleId. Their AAD scheme would need the parent article_id.
            // For now skip AAD on those — UnwrapDek will fall through to legacy path on
            // length 48; for length-49 v=1 rows the unwrap will throw and the caller can decide.
            return null;
        }
        var pkBytes = pkGuid.ToByteArray();
        var aad = new byte[prefix.Length + pkBytes.Length];
        prefix.CopyTo(aad, 0);
        pkBytes.CopyTo(aad, prefix.Length);
        return aad;
    }

    private static void ReWrapTableAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        string tableName,
        string pkColumn,
        string dekColumn,
        string dekIvColumn,
        byte[] oldDek,
        byte[] newDek)
    {
        // Roadmap p7: keyset pagination instead of OFFSET. SQLite scans+discards rows on
        // OFFSET, making each batch progressively slower (O(n²) for the whole rewrap). Keyset
        // (WHERE pk > @lastPk ORDER BY pk LIMIT N) is O(n) total. PK columns here are TEXT
        // (article_id, id) — no special collation needed since rows we just UPDATE'd retain
        // their PK values, ORDER BY pk is stable across the batch.
        const int batchSize = 500;
        string? lastPk = null;
        while (true)
        {
            var sql = lastPk == null
                ? $"SELECT [{pkColumn}] AS pk, [{dekColumn}] AS enc_dek, [{dekIvColumn}] AS dek_iv FROM [{tableName}] ORDER BY [{pkColumn}] LIMIT @limit"
                : $"SELECT [{pkColumn}] AS pk, [{dekColumn}] AS enc_dek, [{dekIvColumn}] AS dek_iv FROM [{tableName}] WHERE [{pkColumn}] > @lastPk ORDER BY [{pkColumn}] LIMIT @limit";

            var rows = conn.Query<dynamic>(sql, new { limit = batchSize, lastPk }, tx).ToList();
            if (rows.Count == 0) break;

            foreach (var row in rows)
            {
                var encDek = (byte[])row.enc_dek;
                var dekIv = (byte[])row.dek_iv;
                var pk = (string)row.pk;

                // Hold plainDek in try/finally so an exception from Wrap or Execute can't leak
                // the per-item DEK on the heap. Use DekManager (per-row AAD) — these are
                // article/media DEKs, not master DEKs. AAD format depends on the table.
                var aad = BuildPerRowAadForTable(tableName, pk, encDek);
                var plainDek = DekManager.UnwrapDek(encDek, dekIv, oldDek, aad);
                try
                {
                    var (newEnc, newIv) = DekManager.WrapDek(plainDek, newDek, aad);
                    conn.Execute(
                        $"UPDATE [{tableName}] SET [{dekColumn}] = @enc, [{dekIvColumn}] = @iv WHERE [{pkColumn}] = @pk",
                        new { enc = newEnc, iv = newIv, pk },
                        tx);
                }
                finally
                {
                    Array.Clear(plainDek, 0, plainDek.Length);
                }

                lastPk = pk;
            }
        }
    }

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
            throw new InvalidOperationException("Another rotation is in progress.");

        try
        {
            if (!_sessionService.IsUnlocked)
                throw new InvalidOperationException("Session is locked; auto-accept requires unlocked session.");

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

    public async Task CancelAsync(string eventId)
    {
        if (!await _executeLock.WaitAsync(TimeSpan.Zero))
            throw new InvalidOperationException("Cannot cancel \u2014 rotation flow is actively executing.");
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var stateRepo = scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>();
            await stateRepo.UpdateStateAsync(eventId, DekRotationState.Cancelled);

            if (_progress.EventId == eventId)
            {
                _progress.Update(DekRotationFlowStep.Idle, 0, "Cancelled");
                _progress.ClearEventId();
            }
        }
        finally
        {
            _executeLock.Release();
        }
    }

}
