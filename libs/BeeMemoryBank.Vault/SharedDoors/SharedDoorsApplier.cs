using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.DekRotation;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.SharedDoors;

/// <summary>
/// Applies a received <c>superadmin_doors_set</c> list on an unlocked shared node in strict
/// door-replacement order: prepare & seal all v2 doors first, write pending boot doors, commit
/// all DB changes (mirror rows, doors, removed-account & local-superadmin cleanup, colliding name
/// disambiguation) in one transaction, rewrite the boot store after commit, and invalidate caches.
/// </summary>
public sealed class SharedDoorsApplier(
    IDbConnectionFactory db,
    INodeIdentityRepository nodeRepo,
    IAccountsHomeRepository accountsHomeRepo,
    ISharedDoorsStateRepository sharedDoorsStateRepo,
    IKeySlotRepository keySlotRepo,
    SessionService session,
    FolderAccessService folderAccess,
    IUnlockMaterialMirror? mirror = null,
    IAuditLogRepository? auditLogRepo = null,
    NodeStateAnnouncer? nodeStateAnnouncer = null,
    ILogger<SharedDoorsApplier>? logger = null,
    IServiceProvider? serviceProvider = null) : ISharedDoorsApplier
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> VaultGates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DeferredRotationRetry> VaultRetries = new(StringComparer.Ordinal);

    public static TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The last error a list gets when its automatic retries are spent while a key operation kept the key busy. The node says so in
    /// node_state (<see cref="SharedDoorsNodeStateContributor.SharedDoorsApplyStuckFlag"/>), so the wait is not silent; the next unlock, list or key
    /// change applies it.
    /// </summary>
    public const string ApplyStuckError =
        "The new list of superadmins is waiting: a key operation kept this node's key busy too long. It is applied at the next unlock or key change.";

    /// <summary>The retry budget of the busy-key retries, per vault (tests make it small).</summary>
    public static int RetryMaxAttempts { get; set; } = 10;

    private sealed record PreparedDoor(
        SuperadminDoorDescriptor Descriptor,
        Doors.Sealed SealedDoor,
        byte[] SaltBytes);

    /// <summary>
    /// Applies the pending list of superadmins, if any; true when one was committed. Then the tombstones this node keeps are
    /// enforced again, outside the doors gate: whoever published the list, no account of a removed person outlives it here.
    /// </summary>
    /// <summary>Test seam only: runs right after the doors transaction committed, before the device box is published again (a crash there).</summary>
    internal Func<Task>? AfterDoorsCommitForTests { get; set; }

    /// <summary>
    /// The node-local marker (<c>tbl_migration_marker</c>) the doors transaction writes: the device box has not followed the doors yet. Removed once
    /// it has; while it stands, the next apply (every unlock's catch-up asks) publishes the box again first (P11 O7).
    /// </summary>
    public const string BoxRepublishMarkerKey = "shared-doors-box-republish";

    public async Task<bool> ApplyPendingAsync(CancellationToken ct = default)
    {
        var applied = await ApplyPendingUnderGateAsync(ct).ConfigureAwait(false);
        if (applied && serviceProvider?.GetService<IRemovedPersonEnforcer>() is { } enforcer)
        {
            var closed = await enforcer.EnforceAsync(null, ct).ConfigureAwait(false);
            if (closed > 0)
                logger?.LogWarning("The list of superadmins named {Count} account(s) of a removed person; closed again, as the tombstone says.", closed);
        }
        return applied;
    }

    /// <summary>The device box follows the doors (a door of somebody who is gone here is no longer copied); then the marker of the commit goes.</summary>
    private async Task RepublishBoxAsync(byte[] masterDek)
    {
        if (serviceProvider?.GetService<IRecoveryBoxPublisher>() is { } recoveryBoxes)
            await recoveryBoxes.RepublishForDoorsAsync(masterDek).ConfigureAwait(false);
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_migration_marker WHERE key = @key", new { key = BoxRepublishMarkerKey }).ConfigureAwait(false);
    }

    /// <summary>A doors commit whose device box did not follow (a crash, a failure): the box is published again now, once. Under the gate, unlocked.</summary>
    private async Task RepublishBoxIfInterruptedAsync()
    {
        try
        {
            using (var conn = db.CreateConnection())
                if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key = @key", new { key = BoxRepublishMarkerKey })
                        .ConfigureAwait(false) == 0)
                    return;
            var masterDek = session.GetMasterDek();
            try
            {
                logger?.LogWarning("The device box did not follow the last change of the superadmin doors; it is published again now.");
                await RepublishBoxAsync(masterDek).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterDek);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("The device box could not be published again after the change of the superadmin doors ({Type}); it is tried at the next unlock.",
                ex.GetType().Name);
        }
    }

    /// <summary>The ids of every person this node keeps a tombstone of (none on a schema without the table).</summary>
    private async Task<HashSet<Guid>> RemovedPersonsAsync()
    {
        using var conn = db.CreateConnection();
        if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'tbl_removed_person'").ConfigureAwait(false) == 0)
            return [];
        var ids = await conn.QueryAsync<string>("SELECT person_id FROM tbl_removed_person").ConfigureAwait(false);
        return ids.Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();
    }

    private async Task<bool> ApplyPendingUnderGateAsync(CancellationToken ct)
    {
        var gate = VaultGates.GetOrAdd(db.DatabaseId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!session.IsUnlocked)
                return false;

            await RepublishBoxIfInterruptedAsync().ConfigureAwait(false);

            var identity = await nodeRepo.GetAsync().ConfigureAwait(false);
            if (identity is null || BlindNodeId.IsBlind(identity.NodeId))
                return false;

            var accountsHome = await accountsHomeRepo.GetAsync().ConfigureAwait(false);
            var isShared = await AccountsHomeService.IsPeerOwnedSharedAsync(sharedDoorsStateRepo, db, identity.NodeId).ConfigureAwait(false);
            if (!isShared)
            {
                var curState = await sharedDoorsStateRepo.GetAsync().ConfigureAwait(false);
                var resolvedOwn = curState.SelfOwnership;
                if (resolvedOwn != OwnershipKinds.Personal)
                    resolvedOwn = (await WhitelistHistory.ResolveSelfOwnershipAsync(db, identity.NodeId).ConfigureAwait(false))?.Ownership ?? resolvedOwn;
                if (resolvedOwn == OwnershipKinds.Personal)
                    await PurgeReplicatedUnderGateAsync(identity, ct).ConfigureAwait(false);
                return false;
            }

            if (accountsHome is null || accountsHome.NodeId == identity.NodeId)
                return false;

            var state = await sharedDoorsStateRepo.GetAsync().ConfigureAwait(false);

            if (!state.PendingApply && state.AppliedVersion >= state.LatestReceivedVersion)
                return false;

            if (state.LatestAuthorNodeId != accountsHome.NodeId
                || state.LatestEventId is not { } eventId
                || string.IsNullOrWhiteSpace(state.LatestPayloadJson))
                return false;

            SuperadminDoorsSetPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<SuperadminDoorsSetPayload>(state.LatestPayloadJson);
            }
            catch (JsonException)
            {
                ResetRetryBudget();
                await RecordFailureAsync(state.LatestReceivedVersion, "Invalid superadmin_doors_set JSON payload.").ConfigureAwait(false);
                return false;
            }
            if (payload is null)
            {
                ResetRetryBudget();
                await RecordFailureAsync(state.LatestReceivedVersion, "Empty superadmin_doors_set payload.").ConfigureAwait(false);
                return false;
            }

            // Acquire per-vault KeyOperationLock before reading the master DEK so seal->commit
            // can never interleave with or use a retired key from a concurrent DEK rotation.
            using var keyOperation = KeyOperationLock.TryEnter(db.DatabaseId, "applying shared doors");
            if (keyOperation is null)
            {
                logger?.LogInformation("{Reason}; leaving pending_apply set and scheduling retry.",
                    KeyOperationLock.Busy(db.DatabaseId, "Applying shared doors"));
                if (ScheduleRetryAfterBusy())
                {
                    // the budget is spent and the list still waits - said, not silent.
                    logger?.LogWarning("Applying the shared doors list gave up its automatic retries; it stays pending for the next unlock or key change.");
                    await sharedDoorsStateRepo.RecordApplyErrorAsync(ApplyStuckError).ConfigureAwait(false);
                }
                return false;
            }

            if (!session.IsUnlocked)
                return false;

            var masterDek = session.GetMasterDek();
            SuperadminDoorsDocument? doc;
            try
            {
                var mySeed = NodeIdentityVault.GetDecryptedPrivateKey(
                    identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV,
                    identity.Ed25519PrivateKeyV, identity.NodeId, masterDek);
                try
                {
                    doc = SuperadminDoorsCrypto.TryDecryptForNode(
                        payload, eventId, accountsHome.NodeId, identity.NodeId, mySeed);
                }
                catch (CryptographicException)
                {
                    ResetRetryBudget();
                    await RecordFailureAsync(payload.Version, "Could not decrypt superadmin_doors_set envelope for this shared node.").ConfigureAwait(false);
                    return false;
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    ResetRetryBudget();
                    await RecordFailureAsync(payload.Version, "Malformed ciphertext or envelope encoding in superadmin_doors_set.").ConfigureAwait(false);
                    return false;
                }
                catch (JsonException)
                {
                    ResetRetryBudget();
                    await RecordFailureAsync(payload.Version, "Malformed decrypted document in superadmin_doors_set.").ConfigureAwait(false);
                    return false;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(mySeed);
                }

                if (doc is null)
                {
                    ResetRetryBudget();
                    await RecordFailureAsync(payload.Version, "No envelope for this shared node in superadmin_doors_set.").ConfigureAwait(false);
                    return false;
                }

                if (doc.People.Count == 0)
                {
                    ResetRetryBudget();
                    await sharedDoorsStateRepo.ClearPendingAsync("Empty superadmin doors list ignored; existing doors unchanged.").ConfigureAwait(false);
                    logger?.LogWarning("Received empty superadmin_doors_set v{Version}; keeping existing doors.", payload.Version);
                    return false;
                }

                // never a mirror of a removed person, whoever published the list (the genuine publisher leaves them out; a rogue
                // or removed accounts node may not). Left out like a person the list does not name: an existing mirror of theirs is deleted.
                var removedPersons = await RemovedPersonsAsync().ConfigureAwait(false);
                var people = doc.People.Where(d => !removedPersons.Contains(d.PersonId)).ToList();
                if (people.Count < doc.People.Count)
                    logger?.LogWarning("The superadmin_doors_set v{Version} names {Count} removed person(s); left out.", payload.Version, doc.People.Count - people.Count);
                if (people.Count == 0)
                {
                    ResetRetryBudget();
                    await sharedDoorsStateRepo.ClearPendingAsync("A superadmin doors list naming only removed people was ignored; existing doors unchanged.").ConfigureAwait(false);
                    return false;
                }

                var (prepared, doorErrors) = PrepareDoors(people, masterDek);
                if (prepared.Count == 0)
                {
                    ResetRetryBudget();
                    var err = doorErrors.Count > 0
                        ? string.Join("; ", doorErrors)
                        : "No usable superadmin doors could be sealed from the list.";
                    await RecordFailureAsync(payload.Version, err).ConfigureAwait(false);
                    logger?.LogWarning("Refused superadmin_doors_set v{Version}: {Error}. Existing accounts and doors kept.", payload.Version, err);
                    return false;
                }

                var result = await CommitDoorsAsync(payload.Version, prepared, masterDek, ct).ConfigureAwait(false);
                if (AfterDoorsCommitForTests is { } crash) await crash();

                await RepublishBoxAsync(masterDek).ConfigureAwait(false);

                ResetRetryBudget();
                var summary =
                    $"Applied superadmin_doors_set v{payload.Version}: {prepared.Count} replicated door(s)" +
                    $", {result.RemovedMirrorCount} removed mirror(s)" +
                    $", {result.RemovedLocalSuperadminCount} local superadmin(s) deleted (no local superadmin doors)" +
                    (result.RenamedNonSuperadmins.Count > 0
                        ? $", renamed local user(s): {string.Join(", ", result.RenamedNonSuperadmins)}"
                        : "") +
                    (doorErrors.Count > 0 ? $", skipped unusable entry(ies): {string.Join("; ", doorErrors)}" : "") +
                    ".";
                logger?.LogInformation("{Summary}", summary);
                if (auditLogRepo is not null)
                    await auditLogRepo.LogAsync("shared_doors", identity.NodeId.ToString("D"), "apply_doors", "system", summary).ConfigureAwait(false);

                nodeStateAnnouncer?.RequestImmediateCheck();
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterDek);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void ResetRetryBudget()
    {
        if (VaultRetries.TryGetValue(db.DatabaseId, out var retry))
            retry.Reset();
    }

    public async Task<int> PurgeReplicatedForPersonalAsync(CancellationToken ct = default)
    {
        var gate = VaultGates.GetOrAdd(db.DatabaseId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var identity = await nodeRepo.GetAsync().ConfigureAwait(false);
            if (identity is null || BlindNodeId.IsBlind(identity.NodeId))
                return 0;
            return await PurgeReplicatedUnderGateAsync(identity, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> PurgeReplicatedUnderGateAsync(NodeIdentity identity, CancellationToken ct)
    {
        using var keyOperation = KeyOperationLock.TryEnter(db.DatabaseId, "purging replicated shared doors");
        if (keyOperation is null)
        {
            logger?.LogInformation("{Reason}; scheduling retry to purge replicated doors after shared->personal change.",
                KeyOperationLock.Busy(db.DatabaseId, "Purging replicated shared doors"));
            ScheduleRetryAfterBusy();
            return 0;
        }

        List<User> replicatedUsers;
        List<int> slotIds;
        using (var conn = db.CreateConnection())
        {
            replicatedUsers = (await conn.QueryAsync<User>(
                @"SELECT id AS Id, person_id AS PersonId, username AS Username, display_name AS DisplayName,
                         password_hash AS PasswordHash, role AS Role, key_slot_id AS KeySlotId,
                         password_hint AS PasswordHint, is_active AS IsActive, chat_access AS ChatAccess,
                         created_at AS CreatedAt, last_login_at AS LastLoginAt, security_stamp AS SecurityStamp,
                         replicated AS Replicated
                  FROM tbl_user
                  WHERE replicated = 1")).ToList();

            var userIds = replicatedUsers.Select(u => u.Id).ToHashSet();
            var rawSlotIds = new HashSet<int>();
            foreach (var u in replicatedUsers)
            {
                if (u.KeySlotId is { } ks) rawSlotIds.Add(ks);
            }
            if (userIds.Count > 0)
            {
                var ownedSlots = await conn.QueryAsync<int>(
                    "SELECT slot_id FROM tbl_key_slot WHERE owner_user_id IN @Ids",
                    new { Ids = userIds.ToArray() }).ConfigureAwait(false);
                foreach (var sid in ownedSlots) rawSlotIds.Add(sid);
            }
            slotIds = rawSlotIds.ToList();
        }

        if (replicatedUsers.Count == 0 && slotIds.Count == 0)
        {
            var cur = await sharedDoorsStateRepo.GetAsync().ConfigureAwait(false);
            if (cur.PendingApply || cur.AppliedVersion > 0)
            {
                using var conn = db.CreateConnection();
                using var tx = conn.BeginTransaction();
                await conn.ExecuteAsync(
                    @"INSERT INTO tbl_shared_doors_state (id, pending_apply, applied_version, applied_at, primary_person_id, last_error)
                      VALUES (1, 0, 0, NULL, NULL, NULL)
                      ON CONFLICT(id) DO UPDATE SET
                          pending_apply = 0,
                          applied_version = 0,
                          applied_at = NULL,
                          primary_person_id = NULL,
                          last_error = NULL",
                    transaction: tx).ConfigureAwait(false);
                tx.Commit();
            }
            ResetRetryBudget();
            return 0;
        }

        // Boot-store rule 2: retire every replicated door in the boot store BEFORE the DB delete commits.
        if (mirror is not null)
        {
            foreach (var slotId in slotIds)
                await mirror.RetireDoorAsync(BootDoorKinds.Slot, slotId, ct).ConfigureAwait(false);
        }

        var invalidatedUserIds = new HashSet<int>();
        using (var conn = db.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                var allUsernames = (await conn.QueryAsync<string>(
                    "SELECT username FROM tbl_user", transaction: tx)).ToList();
                var takenNames = new HashSet<string>(allUsernames, StringComparer.OrdinalIgnoreCase);

                foreach (var mirrorUser in replicatedUsers)
                {
                    await RemoveAccountInTxAsync(conn, tx, mirrorUser, takenNames).ConfigureAwait(false);
                    // Ensure no inactive replicated superadmin row remains if an agent history kept the row.
                    await conn.ExecuteAsync(
                        "UPDATE tbl_user SET replicated = 0, role = @Role WHERE id = @Id",
                        new { Role = UserRoles.User, mirrorUser.Id }, tx).ConfigureAwait(false);
                    invalidatedUserIds.Add(mirrorUser.Id);
                }

                foreach (var slotId in slotIds)
                    await keySlotRepo.DeleteAsync(slotId, tx).ConfigureAwait(false);

                await conn.ExecuteAsync(
                    @"INSERT INTO tbl_shared_doors_state (id, pending_apply, applied_version, applied_at, primary_person_id, last_error)
                      VALUES (1, 0, 0, NULL, NULL, NULL)
                      ON CONFLICT(id) DO UPDATE SET
                          pending_apply = 0,
                          applied_version = 0,
                          applied_at = NULL,
                          primary_person_id = NULL,
                          last_error = NULL",
                    transaction: tx).ConfigureAwait(false);

                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                await SlotDoors.RewriteAsync(mirror, logger).ConfigureAwait(false);
                throw;
            }
        }

        foreach (var uid in invalidatedUserIds)
        {
            CallerStateCache.Invalidate(uid);
            folderAccess.InvalidateCache(uid);
        }

        // Boot-store rule 3: rewrite the boot store AFTER the DB transaction has committed.
        await SlotDoors.RewriteAsync(mirror, logger).ConfigureAwait(false);
        ResetRetryBudget();

        var summary = $"Purged {replicatedUsers.Count} replicated mirror account(s) and {slotIds.Count} door(s) after ownership changed to personal.";
        logger?.LogInformation("{Summary}", summary);
        if (auditLogRepo is not null)
            await auditLogRepo.LogAsync("shared_doors", identity.NodeId.ToString("D"), "purge_replicated_personal", "system", summary).ConfigureAwait(false);
        nodeStateAnnouncer?.RequestImmediateCheck();
        return replicatedUsers.Count;
    }

    /// <summary>Schedules a retry after the key was busy; true when the retry budget is spent (nothing more is scheduled).</summary>
    private bool ScheduleRetryAfterBusy()
    {
        var retry = VaultRetries.GetOrAdd(db.DatabaseId, _ => new DeferredRotationRetry());
        retry.BaseDelay = RetryBaseDelay;
        retry.MaxDelay = TimeSpan.FromMinutes(5);
        retry.MaxAttempts = RetryMaxAttempts;
        var scopeFactory = serviceProvider?.GetService<IServiceScopeFactory>();
        var scheduled = retry.Schedule(async () =>
        {
            try
            {
                if (scopeFactory is not null)
                {
                    using var scope = scopeFactory.CreateScope();
                    var applier = scope.ServiceProvider.GetService<ISharedDoorsApplier>();
                    if (applier is not null)
                        await applier.ApplyPendingAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await ApplyPendingAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException)
            {
                // Test host disposed before scheduled retry fired.
            }
        }, logger);
        return !scheduled && retry.Attempts >= retry.MaxAttempts;
    }

    private sealed record CommitOutcome(
        Guid PrimaryPersonId,
        int RemovedMirrorCount,
        int RemovedLocalSuperadminCount,
        List<string> RenamedNonSuperadmins);

    private async Task<CommitOutcome> CommitDoorsAsync(
        long version,
        List<PreparedDoor> prepared,
        byte[] masterDek,
        CancellationToken ct)
    {
        var invalidatedUserIds = new HashSet<int>();
        var renamedNonSuperadmins = new List<string>();
        var primaryPersonId = prepared[0].Descriptor.PersonId;
        var listedPersonIds = prepared.Select(p => p.Descriptor.PersonId).ToHashSet();
        var listedUsernames = prepared.Select(p => p.Descriptor.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Pre-load slots for existing mirror users that will be updated in place so we can write
        // their pending boot doors BEFORE any transaction mutation, then handle brand-new slots before Commit().
        var prePendingSlots = new List<MasterKeyStore>();
        int removedMirrors = 0;
        int removedLocalSuperadmins = 0;

        using (var conn = db.CreateConnection())
        {
            var existingUsers = (await conn.QueryAsync<User>(
                @"SELECT id AS Id, person_id AS PersonId, username AS Username, display_name AS DisplayName,
                         password_hash AS PasswordHash, role AS Role, key_slot_id AS KeySlotId,
                         password_hint AS PasswordHint, is_active AS IsActive, chat_access AS ChatAccess,
                         created_at AS CreatedAt, last_login_at AS LastLoginAt, security_stamp AS SecurityStamp,
                         replicated AS Replicated
                  FROM tbl_user")).ToList();

            var existingSlotsById = (await conn.QueryAsync<MasterKeyStore>(
                @"SELECT slot_id AS SlotId, slot_type AS SlotType, encrypted_master_dek AS EncryptedMasterDek,
                         iv AS IV, salt AS Salt, argon_memory AS ArgonMemory, argon_iterations AS ArgonIterations,
                         argon_parallelism AS ArgonParallelism, created_at AS CreatedAt,
                         door_version AS DoorVersion, door_public_key AS DoorPublicKey, dek_fp AS DekFingerprint,
                         owner_user_id AS OwnerUserId, created_by_user_id AS CreatedByUserId, label AS Label
                  FROM tbl_key_slot")).ToDictionary(s => s.SlotId);

            foreach (var item in prepared)
            {
                var mirrorRow = existingUsers.FirstOrDefault(u => u.Replicated && u.PersonId == item.Descriptor.PersonId);
                if (mirrorRow?.KeySlotId is { } existingSlotId && existingSlotsById.TryGetValue(existingSlotId, out var existingSlot))
                {
                    var updated = Doors.WithDoor(existingSlot, item.SealedDoor);
                    updated.Salt = item.SaltBytes;
                    updated.ArgonMemory = item.Descriptor.ArgonMemory;
                    updated.ArgonIterations = item.Descriptor.ArgonIterations;
                    updated.ArgonParallelism = item.Descriptor.ArgonParallelism;
                    updated.OwnerUserId = mirrorRow.Id;
                    prePendingSlots.Add(updated);
                }
            }
        }

        // Write pending boot doors for in-place door replacements BEFORE starting the database transaction.
        if (mirror is not null)
        {
            foreach (var slot in prePendingSlots)
                await mirror.AddPendingDoorAsync(SlotDoors.PendingFor(slot, masterDek), ct).ConfigureAwait(false);
        }

        var now = DateTime.UtcNow;
        using (var conn = db.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                var allUsers = (await conn.QueryAsync<User>(
                    @"SELECT id AS Id, person_id AS PersonId, username AS Username, display_name AS DisplayName,
                             password_hash AS PasswordHash, role AS Role, key_slot_id AS KeySlotId,
                             password_hint AS PasswordHint, is_active AS IsActive, chat_access AS ChatAccess,
                             created_at AS CreatedAt, last_login_at AS LastLoginAt, security_stamp AS SecurityStamp,
                             replicated AS Replicated
                      FROM tbl_user", transaction: tx)).ToList();

                var allSlotsById = (await conn.QueryAsync<MasterKeyStore>(
                    @"SELECT slot_id AS SlotId, slot_type AS SlotType, encrypted_master_dek AS EncryptedMasterDek,
                             iv AS IV, salt AS Salt, argon_memory AS ArgonMemory, argon_iterations AS ArgonIterations,
                             argon_parallelism AS ArgonParallelism, created_at AS CreatedAt,
                             door_version AS DoorVersion, door_public_key AS DoorPublicKey, dek_fp AS DekFingerprint,
                             owner_user_id AS OwnerUserId, created_by_user_id AS CreatedByUserId, label AS Label
                      FROM tbl_key_slot", transaction: tx)).ToDictionary(s => s.SlotId);

                var takenNames = new HashSet<string>(
                    allUsers.Select(u => u.Username),
                    StringComparer.OrdinalIgnoreCase);

                // 1. Delete removed mirror rows (anyone NOT in the new list). Never retire boot doors before commit.
                foreach (var removed in allUsers.Where(u => u.Replicated && u.IsActive && !listedPersonIds.Contains(u.PersonId)))
                {
                    await RemoveAccountInTxAsync(conn, tx, removed, takenNames).ConfigureAwait(false);
                    invalidatedUserIds.Add(removed.Id);
                    removedMirrors++;
                }

                // 2. Delete all non-replicated local superadmin accounts + any un-migrated legacy 'password' slot
                // (safe because we already verified prepared.Count >= 1).
                foreach (var localSa in allUsers.Where(u => !u.Replicated && u.IsActive && string.Equals(u.Role, UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase)))
                {
                    await RemoveAccountInTxAsync(conn, tx, localSa, takenNames).ConfigureAwait(false);
                    invalidatedUserIds.Add(localSa.Id);
                    removedLocalSuperadmins++;
                }

                foreach (var legacySlot in allSlotsById.Values.Where(s => string.Equals(s.SlotType, KeySlotTypes.Password, StringComparison.Ordinal)))
                {
                    await conn.ExecuteAsync("UPDATE tbl_user SET key_slot_id = NULL WHERE key_slot_id = @SlotId", new { legacySlot.SlotId }, tx).ConfigureAwait(false);
                    await keySlotRepo.DeleteAsync(legacySlot.SlotId, tx).ConfigureAwait(false);
                }

                // 3. Rename any colliding active local non-superadmin user with a numeric suffix (_2, _3, ...).
                var reservedForNewList = new HashSet<string>(listedUsernames, StringComparer.OrdinalIgnoreCase);
                foreach (var nonSa in allUsers.Where(u => !u.Replicated && u.IsActive && !string.Equals(u.Role, UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!listedUsernames.Contains(nonSa.Username))
                        continue;

                    var candidate = AllocateNumericSuffix(nonSa.Username, takenNames, reservedForNewList);
                    takenNames.Remove(nonSa.Username);
                    takenNames.Add(candidate);
                    await conn.ExecuteAsync(
                        "UPDATE tbl_user SET username = @Candidate, security_stamp = @Stamp WHERE id = @Id",
                        new { Candidate = candidate, Stamp = NewSecurityStamp(), nonSa.Id }, tx).ConfigureAwait(false);
                    invalidatedUserIds.Add(nonSa.Id);
                    renamedNonSuperadmins.Add($"{nonSa.Username}->{candidate}");
                }

                // 4. Park kept mirror rows under temporary unique names if their username is shifting to avoid
                // swap-rename UNIQUE constraint collisions on tbl_user.username.
                var keptMirrors = new Dictionary<Guid, User>();
                foreach (var u in allUsers.Where(u => u.Replicated && listedPersonIds.Contains(u.PersonId)))
                {
                    // Prefer active row if somehow multiple exist, otherwise latest match.
                    if (!keptMirrors.TryGetValue(u.PersonId, out var current) || (!current.IsActive && u.IsActive))
                        keptMirrors[u.PersonId] = u;
                }
                foreach (var kept in keptMirrors.Values)
                {
                    var targetDesc = prepared.First(p => p.Descriptor.PersonId == kept.PersonId).Descriptor;
                    if (!string.Equals(kept.Username, targetDesc.Username, StringComparison.Ordinal))
                    {
                        var tempName = $"__tmp_rep_{kept.Id}_{Guid.NewGuid():N}";
                        await conn.ExecuteAsync(
                            "UPDATE tbl_user SET username = @Temp WHERE id = @Id",
                            new { Temp = tempName, kept.Id }, tx).ConfigureAwait(false);
                        takenNames.Remove(kept.Username);
                        takenNames.Add(tempName);
                        kept.Username = tempName;
                    }
                }

                // 5. Create or update every listed mirror row and its v2 user door.
                foreach (var item in prepared)
                {
                    var d = item.Descriptor;
                    if (keptMirrors.TryGetValue(d.PersonId, out var existing))
                    {
                        var slotChanged = true;
                        int slotId;
                        if (existing.KeySlotId is { } eid && allSlotsById.TryGetValue(eid, out var existingSlot))
                        {
                            slotChanged = existingSlot.DoorVersion != Doors.V2
                                || existingSlot.DoorPublicKey is null
                                || !CryptographicOperations.FixedTimeEquals(existingSlot.DoorPublicKey, item.SealedDoor.PublicKey)
                                || existingSlot.Salt is null
                                || !existingSlot.Salt.SequenceEqual(item.SaltBytes)
                                || existingSlot.ArgonMemory != d.ArgonMemory
                                || existingSlot.ArgonIterations != d.ArgonIterations
                                || existingSlot.ArgonParallelism != d.ArgonParallelism;

                            var updatedSlot = Doors.WithDoor(existingSlot, item.SealedDoor);
                            updatedSlot.Salt = item.SaltBytes;
                            updatedSlot.ArgonMemory = d.ArgonMemory;
                            updatedSlot.ArgonIterations = d.ArgonIterations;
                            updatedSlot.ArgonParallelism = d.ArgonParallelism;
                            updatedSlot.OwnerUserId = existing.Id;
                            await keySlotRepo.ReplaceDoorAsync(updatedSlot, null, tx).ConfigureAwait(false);
                            slotId = eid;
                        }
                        else
                        {
                            var newSlot = BuildNewDoorSlot(existing.Id, item, now);
                            slotId = await keySlotRepo.CreateAsync(newSlot, tx).ConfigureAwait(false);
                            newSlot.SlotId = slotId;
                            if (mirror is not null)
                                await mirror.AddPendingDoorAsync(SlotDoors.PendingFor(newSlot, masterDek), ct).ConfigureAwait(false);
                        }

                        var stamp = slotChanged || !existing.IsActive ? NewSecurityStamp() : existing.SecurityStamp;
                        await conn.ExecuteAsync(
                            @"UPDATE tbl_user
                                 SET username = @Username,
                                     display_name = @DisplayName,
                                     password_hash = '',
                                     password_hint = @PasswordHint,
                                     role = @Role,
                                     is_active = 1,
                                     replicated = 1,
                                     key_slot_id = @SlotId,
                                     security_stamp = @Stamp
                               WHERE id = @Id",
                            new
                            {
                                d.Username,
                                d.DisplayName,
                                d.PasswordHint,
                                Role = UserRoles.Superadmin,
                                SlotId = slotId,
                                Stamp = stamp,
                                existing.Id,
                            }, tx).ConfigureAwait(false);
                        if (slotChanged || !existing.IsActive)
                            invalidatedUserIds.Add(existing.Id);
                    }
                    else
                    {
                        var stamp = NewSecurityStamp();
                        var userId = await conn.ExecuteScalarAsync<int>(
                            @"INSERT INTO tbl_user
                                  (person_id, username, display_name, password_hash, role, key_slot_id,
                                   password_hint, is_active, created_at, security_stamp, chat_access, replicated)
                              VALUES
                                  (@PersonId, @Username, @DisplayName, '', @Role, NULL,
                                   @PasswordHint, 1, @CreatedAt, @Stamp, 1, 1);
                              SELECT last_insert_rowid()",
                            new
                            {
                                d.PersonId,
                                d.Username,
                                d.DisplayName,
                                Role = UserRoles.Superadmin,
                                d.PasswordHint,
                                CreatedAt = now,
                                Stamp = stamp,
                            }, tx).ConfigureAwait(false);

                        var newSlot = BuildNewDoorSlot(userId, item, now);
                        var slotId = await keySlotRepo.CreateAsync(newSlot, tx).ConfigureAwait(false);
                        newSlot.SlotId = slotId;
                        if (mirror is not null)
                            await mirror.AddPendingDoorAsync(SlotDoors.PendingFor(newSlot, masterDek), ct).ConfigureAwait(false);

                        await conn.ExecuteAsync(
                            "UPDATE tbl_user SET key_slot_id = @SlotId WHERE id = @UserId",
                            new { SlotId = slotId, UserId = userId }, tx).ConfigureAwait(false);
                    }
                }

                await sharedDoorsStateRepo.RecordAppliedAsync(version, primaryPersonId, lastError: null, tx).ConfigureAwait(false);
                // The device box follows these doors after the commit; until it has, the marker says so (P11 O7).
                await conn.ExecuteAsync("INSERT OR REPLACE INTO tbl_migration_marker (key, value, set_at) VALUES (@key, @value, @now)", new
                {
                    key = BoxRepublishMarkerKey,
                    value = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    now = DateTime.UtcNow.ToString("O"),
                }, tx).ConfigureAwait(false);
                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                await SlotDoors.RewriteAsync(mirror, logger).ConfigureAwait(false);
                throw;
            }
        }

        foreach (var uid in invalidatedUserIds)
        {
            CallerStateCache.Invalidate(uid);
            folderAccess.InvalidateCache(uid);
        }

        // Rule 3: rewrite the boot store AFTER the DB transaction has committed.
        await SlotDoors.RewriteAsync(mirror, logger).ConfigureAwait(false);

        return new CommitOutcome(primaryPersonId, removedMirrors, removedLocalSuperadmins, renamedNonSuperadmins);
    }

    private async Task RemoveAccountInTxAsync(IDbConnection conn, IDbTransaction tx, User user, HashSet<string> takenNames)
    {
        var userId = user.Id;
        var slotIds = (await conn.QueryAsync<int>(
            "SELECT slot_id FROM tbl_key_slot WHERE owner_user_id = @UserId",
            new { UserId = userId }, tx)).ToList();
        if (user.KeySlotId is { } ks && !slotIds.Contains(ks))
            slotIds.Add(ks);

        await conn.ExecuteAsync("DELETE FROM tbl_remote_api_token WHERE user_id = @UserId", new { UserId = userId }, tx).ConfigureAwait(false);

        var agentCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tbl_agent WHERE owner_user_id = @UserId",
            new { UserId = userId }, tx).ConfigureAwait(false);
        if (agentCount > 0)
        {
            await conn.ExecuteAsync(
                @"UPDATE tbl_agent
                     SET status = 'D', encrypted_dek = NULL, dek_iv = NULL, salt = NULL,
                         kdf_version = 0, door_public_key = NULL, dek_fp = NULL
                   WHERE owner_user_id = @UserId",
                new { UserId = userId }, tx).ConfigureAwait(false);
        }

        await conn.ExecuteAsync("UPDATE tbl_user SET key_slot_id = NULL WHERE id = @UserId", new { UserId = userId }, tx).ConfigureAwait(false);
        foreach (var slotId in slotIds)
            await keySlotRepo.DeleteAsync(slotId, tx).ConfigureAwait(false);

        takenNames.Remove(user.Username);
        if (agentCount == 0)
        {
            await conn.ExecuteAsync("DELETE FROM tbl_user WHERE id = @UserId", new { UserId = userId }, tx).ConfigureAwait(false);
        }
        else
        {
            var released = AllocateReleasedName(user.Username, takenNames);
            takenNames.Add(released);
            await conn.ExecuteAsync(
                @"UPDATE tbl_user
                     SET is_active = 0,
                         username = @Released,
                         password_hash = '',
                         password_hint = NULL,
                         key_slot_id = NULL,
                         security_stamp = @Stamp
                   WHERE id = @UserId",
                new { Released = released, Stamp = NewSecurityStamp(), UserId = userId }, tx).ConfigureAwait(false);
        }
    }

    private static (List<PreparedDoor> Prepared, List<string> Errors) PrepareDoors(
        List<SuperadminDoorDescriptor> descriptors, byte[] masterDek)
    {
        var prepared = new List<PreparedDoor>();
        var errors = new List<string>();
        var seenPersonIds = new HashSet<Guid>();
        var seenUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in descriptors)
        {
            if (d.PersonId == Guid.Empty || string.IsNullOrWhiteSpace(d.Username))
            {
                errors.Add("Entry missing person_id or username");
                continue;
            }
            if (!seenPersonIds.Add(d.PersonId) || !seenUsernames.Add(d.Username.Trim()))
            {
                errors.Add($"Duplicate person or username '{d.Username}'");
                continue;
            }
            try
            {
                KeyDerivation.ValidateUntrustedParameters(d.ArgonMemory, d.ArgonIterations, d.ArgonParallelism);
            }
            catch (CryptographicException)
            {
                errors.Add($"Invalid KDF parameters for '{d.Username}'");
                continue;
            }

            byte[] salt;
            byte[] doorPk;
            try
            {
                salt = Convert.FromBase64String(d.SaltB64);
                doorPk = Convert.FromBase64String(d.DoorPublicKeyB64);
            }
            catch (FormatException)
            {
                errors.Add($"Malformed base64 salt or door_pk for '{d.Username}'");
                continue;
            }

            if (salt.Length == 0 || doorPk.Length != DoorBox.PublicKeySize || doorPk.All(b => b == 0))
            {
                errors.Add($"Unusable door_pk or salt for '{d.Username}'");
                continue;
            }

            try
            {
                var sealedDoor = Doors.SealTo(KeySlotTypes.User, doorPk, masterDek);
                prepared.Add(new PreparedDoor(d with { Username = d.Username.Trim() }, sealedDoor, salt));
            }
            catch (CryptographicException)
            {
                errors.Add($"Unusable door_pk for '{d.Username}'");
            }
            catch (ArgumentException)
            {
                errors.Add($"Unusable door_pk for '{d.Username}'");
            }
        }

        return (prepared, errors);
    }

    private static MasterKeyStore BuildNewDoorSlot(int userId, PreparedDoor item, DateTime now) => new()
    {
        SlotType = KeySlotTypes.User,
        EncryptedMasterDek = item.SealedDoor.Box,
        IV = item.SealedDoor.Iv,
        Salt = item.SaltBytes,
        ArgonMemory = item.Descriptor.ArgonMemory,
        ArgonIterations = item.Descriptor.ArgonIterations,
        ArgonParallelism = item.Descriptor.ArgonParallelism,
        CreatedAt = now,
        DoorVersion = Doors.V2,
        DoorPublicKey = item.SealedDoor.PublicKey,
        DekFingerprint = item.SealedDoor.DekFingerprint,
        OwnerUserId = userId,
        CreatedByUserId = userId,
    };

    private async Task RecordFailureAsync(long version, string error)
    {
        try
        {
            await sharedDoorsStateRepo.RecordApplyErrorAsync(error).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not record shared-doors apply error for v{Version}.", version);
        }
    }

    private static string AllocateNumericSuffix(string baseName, HashSet<string> takenNames, HashSet<string> reservedNames)
    {
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName}_{i}";
            if (!takenNames.Contains(candidate) && !reservedNames.Contains(candidate))
                return candidate;
        }
    }

    private static string AllocateReleasedName(string baseName, HashSet<string> takenNames)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var suffix = new string(Enumerable.Range(0, 6)
                .Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
            var candidate = $"{baseName}_del_{suffix}";
            if (!takenNames.Contains(candidate))
                return candidate;
        }
        return $"del_{Guid.NewGuid():N}";
    }

    private static string NewSecurityStamp() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
