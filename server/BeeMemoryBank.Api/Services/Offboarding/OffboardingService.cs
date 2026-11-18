using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Vault.Sealed;
using Dapper;

namespace BeeMemoryBank.Api.Services.Offboarding;

/// <summary>A refusal of the removal process, in plain words, with the HTTP status the API answers with.</summary>
public sealed class OffboardingRefusedException(string message, int status = 409) : InvalidOperationException(message)
{
    public int Status { get; } = status;
}

/// <summary>What the administrator asks for when a removal starts (plan E1/E3).</summary>
public sealed record OffboardingStartRequest(
    int UserId, string Target, string? Role, IReadOnlyList<DemotionFolderRule>? FolderRules, bool AllowOpenRole, string Password);

/// <summary>
/// The removal of a superadmin (release 2.12.0, plan WS-E). One service owns it for the Users page, the API and the CLI: demoting or deleting a
/// superadmin STARTS a process here, and the account changes only inside it (S0 locks the person out, S5 deletes or demotes). Every step is
/// idempotent and resumable after a crash or a restart; no step is skipped and nothing is cancelled after S0.
///
/// <para>The administrator's password is needed twice: at the start (S0 publishes a fresh strong recovery box and a device box from it) and for the
/// rotation of S4. It is kept in memory only, as characters this service clears, for at most <see cref="PasswordHoldTime"/>; after that (or after a
/// restart) the page asks for it again when S4 is reached.</para>
/// </summary>
public sealed partial class OffboardingService(
    IServiceScopeFactory scopes,
    SessionService session,
    IDbConnectionFactory connFactory,
    ILogger<OffboardingService> logger,
    string dataPath)
{
    /// <summary>How long a password entered for a removal is kept in memory for its rotation.</summary>
    public static TimeSpan PasswordHoldTime { get; set; } = TimeSpan.FromMinutes(30);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, PasswordHold> _holds = new();

    private sealed class PasswordHold(char[] password, int adminUserId, DateTime expires)
    {
        public char[] Password { get; } = password;
        public int AdminUserId { get; } = adminUserId;
        public DateTime Expires { get; } = expires;
        public void Clear() => Array.Clear(Password);
    }

    private SemaphoreSlim Gate => Gates.GetOrAdd(connFactory.DatabaseId, _ => new SemaphoreSlim(1, 1));

    /// <summary>Test seam: called after the S0 transaction committed and before anything after it (a test stops the process there, as a crash would).</summary>
    internal Func<Task>? AfterLockOutCommitForTests { get; set; }

    /// <summary>Test seam: runs at the end of a pass, after the steps did their work and before the pass is saved (a crash there).</summary>
    internal Func<OffboardingProgress, Task>? BeforeSavingPassForTests { get; set; }

    /// <summary>Test seam: runs right after S4's rotation is proposed, before its id is saved with the process (a crash there).</summary>
    internal Func<Task>? AfterRotationProposedForTests { get; set; }

    // ── start ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks every precondition (plan E3; nothing changes when one fails, the refusal names it) and runs S0 in one transaction; then the work after
    /// S0 and every step that can run at once. Returns the process id.
    /// </summary>
    public async Task<Guid> StartAsync(OffboardingStartRequest req, int actorUserId, CancellationToken ct = default)
    {
        if (req.Target is not ("delete" or "demote"))
            throw new OffboardingRefusedException("Say what happens to the account at the end: deleted, or an ordinary user.", 400);
        if (!session.IsUnlocked)
            throw new OffboardingRefusedException("Unlock this node first: a removal needs the master key in memory.");

        await Gate.WaitAsync(ct);
        Guid processId;
        AdminDoor? admin = null;
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var users = sp.GetRequiredService<IUserRepository>();
            var userService = sp.GetRequiredService<UserService>();

            var person = await users.GetByIdAsync(req.UserId)
                ?? throw new OffboardingRefusedException("There is no such person on this node.", 404);
            if (person.Role != UserRoles.Superadmin)
                throw new OffboardingRefusedException("This person is not a superadmin: their account changes at once, without a removal process.");
            if (person.IsBeingRemoved)
                throw new OffboardingRefusedException("This person is already being removed.");
            if (person.Id == actorUserId)
                throw new OffboardingRefusedException("You cannot remove yourself. Another superadmin has to do it.");

            string? role = null;
            if (req.Target == "demote")
            {
                try { role = await userService.ValidateDemotionTargetAsync(req.Role ?? "", req.FolderRules, req.AllowOpenRole); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    throw new OffboardingRefusedException(ex.Message, ex is ArgumentException ? 400 : 409);
                }
            }

            await CheckPreconditionsAsync(sp, person);
            admin = await VerifyAdministratorAsync(sp, actorUserId, req.Password, person.Id);

            var self = await sp.GetRequiredService<INodeIdentityRepository>().GetAsync()
                ?? throw new OffboardingRefusedException("This node is not set up.");
            processId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var record = new OffboardingRecord
            {
                ProcessId = processId, PersonId = person.PersonId, UserId = person.Id, AccountsNodeId = self.NodeId, DriverNodeId = self.NodeId,
                Target = req.Target, TargetRole = role, AllowOpenRole = req.AllowOpenRole,
                TargetRulesJson = req.FolderRules is null ? null : JsonSerializer.Serialize(req.FolderRules),
                Progress = new OffboardingProgress { TombstoneEventId = Guid.NewGuid(), S0At = now },
                StartedBy = $"{admin.User.DisplayName} ({admin.User.Username})", StartedAt = now, UpdatedAt = now,
            };

            // S0, one transaction (plan E4): the account is locked out, its doors, recovery keys, auto-unlock agents, agents and tokens go (the frame),
            // the process is recorded, the tombstone is kept, and the person's open invitations to blind copies end.
            try
            {
                await userService.LockOutForRemovalAsync(person.Id, async (conn, tx) =>
                {
                    await OffboardingStore.InsertAsync(conn, tx, record);
                    await conn.ExecuteAsync(
                        @"INSERT INTO tbl_removed_person (person_id, outcome, removed_at, event_id) VALUES (@P, @O, @T, @E)
                          ON CONFLICT(person_id) DO UPDATE SET outcome = excluded.outcome, removed_at = excluded.removed_at, event_id = excluded.event_id",
                        new
                        {
                            P = OffboardingStore.Id(person.PersonId), O = record.OutcomeWord, T = OffboardingStore.Time(now),
                            E = OffboardingStore.Id(record.Progress.TombstoneEventId),
                        }, tx);
                    var uid = person.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    await conn.ExecuteAsync(
                        @"UPDATE tbl_blind_invite SET state = CASE state WHEN 'issued' THEN 'expired' ELSE 'refused' END, decided_at = @T, decided_by = 'removal'
                           WHERE created_by = @uid AND state IN ('issued', 'redeemed')",
                        new { uid, T = OffboardingStore.Time(now) }, tx);
                }, ct, admin.User.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (InvalidOperationException ex)
            {
                // The frame's guards: the last superadmin, the last door a person can open. Nothing was written.
                throw new OffboardingRefusedException(ex.Message);
            }

            _holds[processId] = new PasswordHold(req.Password.ToCharArray(), admin.User.Id, DateTime.UtcNow + PasswordHoldTime);
            await AuditAsync(sp, "offboarding_started", processId,
                $"Removal of user #{person.Id} started by user #{admin.User.Id} (at the end: {(record.IsDelete ? "deleted" : "an ordinary user")}); " +
                "account locked out, doors, recovery keys, agents and tokens revoked.");
            logger.LogWarning("A removal started: user #{User} is locked out (process {Process}).", person.Id, processId);
        }
        catch
        {
            admin?.Clear();
            throw;
        }
        finally
        {
            Gate.Release();
        }

        try
        {
            if (AfterLockOutCommitForTests is { } hook) await hook();
            await FinishLockOutAsync(processId, admin, ct);
        }
        finally
        {
            admin?.Clear();
        }
        await AdvanceAsync(processId, ct);
        return processId;
    }

    /// <summary>
    /// The preconditions of plan E3 that are about the network and the node (the person and the administrator are checked by the caller): no device
    /// waiting for an ownership review; no rotation, re-key or offline re-key in flight; never the last superadmin; another active superadmin holds a
    /// v2 door.
    /// </summary>
    private async Task CheckPreconditionsAsync(IServiceProvider sp, User person)
    {
        using var conn = connFactory.CreateConnection();
        var stillThere = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM tbl_user
               WHERE role = 'superadmin' AND is_active = 1 AND account_state = 'active' AND id <> @id", new { id = person.Id });
        if (stillThere == 0)
            throw new OffboardingRefusedException("This is the last superadmin. Make another person a superadmin first.");

        var withV2Door = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM tbl_user u JOIN tbl_key_slot k ON k.slot_id = u.key_slot_id
               WHERE u.role = 'superadmin' AND u.is_active = 1 AND u.account_state = 'active' AND u.id <> @id AND k.door_version = 2",
            new { id = person.Id });
        if (withV2Door == 0)
            throw new OffboardingRefusedException(
                "No other superadmin has signed in since this version was installed. One of them signs in once first, then the removal can start.");

        var review = (await conn.QueryAsync<string>(
            "SELECT display_name FROM tbl_whitelist WHERE status = 'A' AND needs_review = 1 ORDER BY display_name")).ToList();
        if (review.Count > 0)
            throw new OffboardingRefusedException(
                $"First say whose these devices are (Devices page): {string.Join(", ", review)}. A removal needs to know which devices are personal.");

        if (BeeMemoryBank.AppPaths.RekeyLock.IsHeld(dataPath))
            throw new OffboardingRefusedException("An offline re-key of this vault is running. Start the removal when it has finished.");
        if (KeyOperationLock.Holder(connFactory.DatabaseId) is { } holder)
            throw new OffboardingRefusedException($"{char.ToUpperInvariant(holder[0])}{holder[1..]} is running on this node. Start the removal when it has finished.");
        var states = sp.GetRequiredService<IDekRotationStateRepository>();
        if ((await states.GetByStateAsync(DekRotationState.Proposed)).Count > 0 || (await states.GetByStateAsync(DekRotationState.Committing)).Count > 0)
            throw new OffboardingRefusedException("A change of the master key is in flight. Start the removal when it has finished.");
    }

    /// <summary>The administrator's own door, opened with the password they typed again (plan E3). Kept for S0's boxes, cleared after.</summary>
    private sealed class AdminDoor(User user, MasterKeyStore slot, byte[] kek)
    {
        public User User { get; } = user;
        public MasterKeyStore Slot { get; } = slot;
        public byte[] Kek { get; } = kek;
        public void Clear() => Array.Clear(Kek);
    }

    private async Task<AdminDoor> VerifyAdministratorAsync(IServiceProvider sp, int actorUserId, string password, int personUserId)
    {
        if (actorUserId == personUserId)
            throw new OffboardingRefusedException("You cannot remove yourself. Another superadmin has to do it.");
        var users = sp.GetRequiredService<IUserRepository>();
        var actor = await users.GetByIdAsync(actorUserId);
        if (actor is not { Role: UserRoles.Superadmin, IsBeingRemoved: false })
            throw new OffboardingRefusedException("Only an active superadmin can remove a superadmin.", 403);
        if (actor.KeySlotId is not { } slotId)
            throw new OffboardingRefusedException("Sign in once with your password first: your own door is needed to confirm it.");
        var slot = (await sp.GetRequiredService<IKeySlotRepository>().GetAllAsync()).FirstOrDefault(s => s.SlotId == slotId);
        if (slot?.Salt is null || slot.ArgonMemory is null || slot.ArgonIterations is null || slot.ArgonParallelism is null)
            throw new OffboardingRefusedException("Sign in once with your password first: your own door is needed to confirm it.");

        var kek = KeyDerivation.DeriveKek(password ?? "", slot.Salt, slot.ArgonMemory.Value, slot.ArgonIterations.Value, slot.ArgonParallelism.Value);
        byte[]? opened = null;
        byte[]? current = null;
        try
        {
            try { opened = Doors.OpenSlot(slot, kek); }
            catch (CryptographicException) { opened = null; }
            current = session.GetMasterDek();
            if (opened is null || !CryptographicOperations.FixedTimeEquals(opened, current))
            {
                Array.Clear(kek);
                throw new OffboardingRefusedException("Your password is not correct.", 403);
            }
            return new AdminDoor(actor, slot, kek);
        }
        finally
        {
            if (opened is not null) Array.Clear(opened);
            if (current is not null) Array.Clear(current);
        }
    }

    /// <summary>
    /// The work of S0 after its transaction (idempotent; resumed after a crash): the person's personal devices revoked, the tombstone and the process
    /// state published, and - with the administrator's password in hand - a fresh strong box and a device box from their door (plan A4: a strong box
    /// made under the removed person's password is superseded; a peer without door-box-v1 gets a box it can read).
    /// </summary>
    private async Task FinishLockOutAsync(Guid processId, AdminDoor? admin, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var store = new OffboardingStore(connFactory);
            var record = await store.GetAsync(processId);
            if (record is null || record.Progress.S0Done || record.Origin != "local") return;
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;

            var whitelist = sp.GetRequiredService<IWhitelistRepository>();
            var events = sp.GetRequiredService<IEventLogger>();
            foreach (var device in (await whitelist.GetAllActiveAsync())
                         .Where(w => w.Ownership == OwnershipKinds.Personal && w.OwnerPersonId == record.PersonId))
            {
                await RevokeDeviceAsync(sp, device.NodeId);
                await AuditAsync(sp, "offboarding_device_revoked", processId, $"Personal device '{Printable(device.DisplayName)}' ({device.NodeId}) of the person revoked.");
            }

            await events.LogPersonRemovedAsync(record.PersonId, record.OutcomeWord, record.Progress.S0At, record.Progress.TombstoneEventId);

            if (admin is not null && session.IsUnlocked)
            {
                var dek = session.GetMasterDek();
                try
                {
                    if (sp.GetService<IRecoveryBoxPublisher>() is { } boxes)
                        await boxes.PublishDeviceBoxAsync(admin.Slot, dek, admin.Kek);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("The device box of the administrator's door could not be published at the lock-out ({Failure}); the next sign-in does it.", ex.GetType().Name);
                }
                finally
                {
                    Array.Clear(dek);
                }
                if (sp.GetService<Recovery.StrongBoxService>() is { } strong && _holds.TryGetValue(processId, out var hold))
                    _ = strong.RequestBuild(new string(hold.Password));
            }

            record.Progress.S0Done = true;
            await PublishStateAsync(sp, record);
        }
        finally
        {
            Gate.Release();
        }
    }

    // ── helpers shared by the steps ────────────────────────────────────────────────────────────────────────────

    private static async Task RevokeDeviceAsync(IServiceProvider sp, Guid nodeId)
    {
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var entry = await whitelist.GetByNodeIdAsync(nodeId, includeDeleted: true);
        if (entry is null || entry.Status != "A") return;
        var version = await sp.GetRequiredService<IEventLogger>().LogWhitelistRevokeAsync(nodeId);
        await whitelist.RevokeAsync(nodeId, version);
        await BeeMemoryBank.Api.Endpoints.WhitelistEndpoints.CleanupRevokedPeerAsync(sp, nodeId);
    }

    /// <summary>Publishes the process state (newest wins on every node) and stores the version it got.</summary>
    private async Task PublishStateAsync(IServiceProvider sp, OffboardingRecord record)
    {
        var step = record.ClosedAt is null ? record.Progress.CurrentStep : OffboardingSteps.Closed;
        try
        {
            record.Version = await sp.GetRequiredService<IEventLogger>().LogOffboardingStateAsync(
                record.ProcessId, record.PersonId, record.AccountsNodeId, record.DriverNodeId, record.Target, step,
                record.StartedAt, record.ClosedAt, record.ClosedAt is null ? null : record.Outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The local record is what drives the process; the event is how other nodes see it. The next change publishes again.
            logger.LogWarning("The state of removal {Process} could not be published ({Failure}).", record.ProcessId, ex.GetType().Name);
        }
        await new OffboardingStore(connFactory).SaveAsync(record);
    }

    private static Task AuditAsync(IServiceProvider sp, string action, Guid processId, string details) =>
        sp.GetRequiredService<IAuditLogRepository>().LogAsync("offboarding", processId.ToString("D"), action, "user", details);

    private static string Printable(string? text)
    {
        var clean = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 100 ? clean[..100] : clean;
    }
}
