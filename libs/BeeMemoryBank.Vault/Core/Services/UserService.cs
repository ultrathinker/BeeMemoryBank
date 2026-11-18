using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Vault.Sealed;
using BeeMemoryBank.Vault.SharedDoors;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Core.Services;

public partial class UserService(
    IUserRepository userRepo,
    IKeySlotRepository keySlotRepo,
    SessionService session,
    IRoleRepository roleRepo,
    FolderAccessService folderAccess,
    IRemoteApiTokenRepository? remoteTokenRepo = null,
    IAgentRepository? agentRepo = null,
    IRecoveryBoxPublisher? recoveryBoxes = null,
    IDbConnectionFactory? factory = null,
    IUnlockMaterialMirror? mirror = null,
    IAccountsHomeRepository? accountsHomeRepo = null,
    IWhitelistRepository? whitelistRepo = null,
    INodeIdentityRepository? nodeIdentityRepo = null,
    ISharedDoorsStateRepository? sharedDoorsStateRepo = null,
    ISuperadminDoorsNotifier? doorsNotifier = null,
    Microsoft.Extensions.Logging.ILogger<UserService>? logger = null,
    ILazySlotRewrapService? lazyRewrap = null)
{
    // Step B (docs/design/step-b-spec.md 3.6, 4.3): every key slot this service creates or deletes is a door of a sealed node. The
    // pending door is written before the sealed insert commits, the copies of a deleted slot's door are retired before the delete, and
    // the boot store is rewritten when the operation is done. With no mirror (or on a plain node, where the mirror does nothing) this is
    // the plain repository call it replaces.

    // Roles are rows in tbl_role. The only role this service special-cases
    // is "superadmin", because that is the one that owns a key slot; every other role is just a
    // string that decides which folder rules apply.
    //
    // Returns the CANONICAL name from tbl_role, which is what must be stored. The lookup is
    // case-insensitive (COLLATE NOCASE), but almost every consumer of tbl_user.role compares it
    // with an ordinal == : CallerIdentity's X-User-Role check, the key-slot branches below,
    // FolderAccessService's superadmin bypass. Storing "Superadmin" verbatim would pass
    // validation and then fail every one of those — an account that is a superadmin in the
    // table, has no key slot, and is treated as an ordinary user by the middleware.
    private async Task<string> CanonicalRoleNameAsync(string role)
    {
        var existing = await roleRepo.GetByNameAsync(role)
            ?? throw new ArgumentException($"Invalid role '{role}'. No such role exists.");
        return existing.Name;
    }

    // Invalidate any remote API tokens the user owns whenever their password
    // changes. Without this, an attacker who captured
    // a remote token before the password rotation keeps full read access for
    // the remaining sliding-90-day window. Repo is optional so legacy DI
    // setups in tests still construct the service.
    private async Task RevokeRemoteTokensAsync(int userId)
    {
        if (remoteTokenRepo == null) return;
        try
        {
            var tokens = await remoteTokenRepo.ListByUserAsync(userId);
            foreach (var t in tokens)
                await remoteTokenRepo.DeleteAsync(t.Id);
        }
        catch { /* non-fatal — log via standard logger if it ever matters */ }
    }

    // Wrap the vault's master DEK with a KEK derived from `password` and store it as a new
    // "user" key slot, returning its id. Every superadmin needs one: UnlockAsync walks every
    // slot and tries the entered password against each, so a slot's password IS that user's
    // unlock password. Requires an unlocked session — the master DEK only exists in memory
    // while the vault is open, and there is no other way to obtain it — unless the caller already
    // holds the unwrapped DEK (the recovery-key reset, which proves it without opening the session).
    // A DEK passed in stays the caller's: it is neither cloned nor wiped here.
    private async Task<int> CreateUserKeySlotAsync(string password, byte[]? providedDek = null)
    {
        if (providedDek == null && !session.IsUnlocked)
            throw new InvalidOperationException("Session must be unlocked to create a key slot");

        var masterDek = providedDek ?? session.GetMasterDek();
        byte[]? kek = null;
        try
        {
            var salt = KeyDerivation.GenerateSalt();
            kek = KeyDerivation.DeriveKek(password, salt);
            // A new door is always a sealed (v2) door (ADR 0012). Its owner is set by the database when tbl_user points at it (migration 046).
            var slot = Doors.NewPasswordSlot(KeySlotTypes.User, kek, salt, CryptoConstants.DefaultArgonMemory, CryptoConstants.DefaultArgonIterations,
                CryptoConstants.DefaultArgonParallelism, masterDek);
            await SlotDoors.CreateSlotAsync(keySlotRepo, factory, mirror, slot, masterDek);

            // Every UserService path that gives a password a slot — self-service and admin password
            // change, provisioning at login, creating or promoting a superadmin — comes through here,
            // so this one call is what puts the new password's device box on the blind nodes.
            if (recoveryBoxes != null)
                await recoveryBoxes.PublishDeviceBoxAsync(slot, masterDek, kek);
            return slot.SlotId;
        }
        finally
        {
            if (providedDek == null) Array.Clear(masterDek);
            Array.Clear(kek);
        }
    }

    public Task RecordLoginAsync(int userId) => userRepo.UpdateLastLoginAsync(userId);

    /// <summary>
    /// A door was deleted (2.12, plan WS-A4): this node's device box may have copied it, so it is published again from the door it should copy
    /// now. Best effort, like every device-box publication.
    /// </summary>
    private async Task RepublishDeviceBoxAsync()
    {
        if (recoveryBoxes == null || !session.IsUnlocked) return;
        var dek = session.GetMasterDek();
        try
        {
            await recoveryBoxes.RepublishForDoorsAsync(dek);
        }
        finally
        {
            Array.Clear(dek);
        }
    }

    public async Task<User?> AuthenticateAsync(string username, string password)
    {
        var (user, dek) = await AuthenticateWithDekAsync(username, password);
        if (dek is not null) Array.Clear(dek);
        return user;
    }

    /// <summary>
    /// Authenticates a user and, when the account is replicated (<c>replicated = 1</c>), also returns
    /// the sentinel-verified master DEK unwrapped from THAT person's door so <c>/api/session/login</c>
    /// can unlock a plain node with the same single Argon2 derivation. Caller must clear any non-null DEK.
    /// </summary>
    public async Task<(User? User, byte[]? VerifiedMasterDek)> AuthenticateWithDekAsync(string username, string password)
    {
        var user = await userRepo.GetByUsernameAsync(username);
        if (user == null)
        {
            // Burn the same Argon2id work an existing account would have cost before answering.
            // Without it, "no such user" returns in microseconds while a wrong password for a real
            // account takes hundreds of milliseconds — a timing oracle that enumerates valid
            // usernames from outside with no failed-login trail, which is exactly the input a
            // targeted password-guessing campaign wants.
            BurnPasswordVerification(password);
            return (null, null);
        }

        if (user.Replicated)
        {
            if (user.KeySlotId is not { } slotId)
            {
                BurnPasswordVerification(password);
                return (null, null);
            }

            var opened = await session.TryOpenUserDoorAsync(slotId, password);
            if (!opened.DerivationAttempted)
                BurnPasswordVerification(password);
            if (opened.Dek is null)
                return (null, null);
            // A mirror row of a person a removal holds (a shared node, release 2.12.0): the door was opened as for anybody, then refused.
            if (user.IsBeingRemoved)
            {
                Array.Clear(opened.Dek);
                return (null, null);
            }

            await userRepo.UpdateLastLoginAsync(user.Id);
            return (user, opened.Dek);
        }

        if (!VerifyPassword(password, user.PasswordHash)) return (null, null);

        // An account a removal process holds cannot sign in (release 2.12.0, plan E4 S0). The password was checked first, so the refusal costs what a wrong
        // password costs and says nothing more than one.
        if (user.IsBeingRemoved) return (null, null);

        await userRepo.UpdateLastLoginAsync(user.Id);
        return (user, null);
    }

    /// <summary>
    /// Runs one password verification against a fixed dummy hash and discards the result, so a
    /// caller cannot tell "this account does not exist" from "wrong password" by how long the
    /// answer took. The hash is a real Argon2id record over a random password nobody holds,
    /// generated once per process — using a constant literal would make the work identical but is
    /// no cheaper, and generating it lazily keeps this file free of a checked-in credential-shaped
    /// blob that future readers would have to reason about.
    /// </summary>
    public static void BurnPasswordVerification(string password)
    {
        _ = VerifyPassword(password, GetDummyPasswordHash());
    }

    // Not Lazy<T>: Lazy caches an exception, so a first call that met a saturated KDF gate
    // (KdfBusyException) would poison every later unknown-user login until restart. This retries
    // until one derivation succeeds; a lost race only costs one extra derivation.
    private static string? _dummyPasswordHash;

    private static string GetDummyPasswordHash()
    {
        var existing = Volatile.Read(ref _dummyPasswordHash);
        if (existing != null) return existing;
        var created = HashPassword(Convert.ToHexString(SecureRandom.GetBytes(32)));
        return Interlocked.CompareExchange(ref _dummyPasswordHash, created, null) ?? created;
    }

    /// <summary>
    /// Throws unless some OTHER active superadmin would still hold a key slot after this user
    /// loses theirs. Counting rows in tbl_key_slot is not equivalent: a `recovery` slot opens
    /// only with the recovery key, and an `os_auto_unlock` slot has no KDF params at all so
    /// UnlockAsync skips it outright — either one inflates a raw count past the guard while
    /// leaving nobody able to unlock with a password. Promotion defers slot creation, so
    /// "another superadmin exists" does not imply "another superadmin can unlock".
    /// </summary>
    private async Task EnsureAnotherSuperadminHoldsAKeySlotAsync(int excludingUserId, string action)
    {
        var activeUsers = await userRepo.ListActiveAsync();
        var covered = activeUsers.Any(u =>
            u.Id != excludingUserId && u.Role == UserRoles.Superadmin && u.KeySlotId.HasValue);

        if (!covered)
            throw new InvalidOperationException(
                $"Cannot {action} this user — their key slot is the only remaining way to unlock " +
                "the vault. Have another superadmin log in first (that provisions their key slot), " +
                "then retry.");
    }

    // Re-point the user's key slot at `newPassword`. An existing slot is rewrapped (the old one
    // is dropped only once the replacement is safely stored); a superadmin who has no slot yet
    // gets one provisioned, so an admin password reset grants vault access just like the
    // promoted user's next login would (see ProvisionMissingKeySlotAsync). A locked session is
    // only fatal when a real slot has to be rewrapped — provisioning simply waits for the
    // next opportunity rather than blocking the password change.
    private async Task RewrapOrProvisionKeySlotAsync(User user, string newPassword, byte[]? providedDek = null)
    {
        if (user.KeySlotId.HasValue)
        {
            var newSlotId = await CreateUserKeySlotAsync(newPassword, providedDek);
            await SlotDoors.DeleteSlotAsync(keySlotRepo, mirror, user.KeySlotId.Value);
            user.KeySlotId = newSlotId;
        }
        else if (user.Role == UserRoles.Superadmin && (providedDek != null || session.IsUnlocked))
        {
            user.KeySlotId = await CreateUserKeySlotAsync(newPassword, providedDek);
        }
    }

    // The tail every password REPLACEMENT shares (self-service change, admin reset, recovery-key
    // reset): new login hash, the user's key slot re-pointed at the new password, then the
    // revocations that make the old password stop meaning anything — security stamp (every web
    // cookie of this user is rejected at its next revalidation) and remote API tokens.
    //
    // `user` was loaded before the Argon2id work and the slot rewrite, so it can be stale by the time it is written: a demotion that committed in
    // between must not be undone by it. The row is therefore written by TryReplacePasswordAsync - the columns a replacement owns, and only while
    // the row still has the role and the key slot this call loaded. Returns false when it no longer does: nothing is written, the slot made here is
    // dropped, and the caller says so.
    private async Task<bool> ReplacePasswordAsync(User user, string newPassword, byte[]? providedDek = null, CancellationToken ct = default, string? passwordHint = null)
    {
        ValidatePassword(newPassword);
        var loadedRole = user.Role;
        var loadedKeySlotId = user.KeySlotId;
        // A caller WITH a person replaces the hint; a caller without one (a seed or a tool) leaves the stored hint
        // untouched rather than silently deleting something a person wrote.
        if (passwordHint != null)
            user.PasswordHint = PasswordHintPolicy.NormalizeAndValidate(passwordHint, newPassword, user.Username);
        user.PasswordHash = HashPassword(newPassword);

        // The last point at which the replacement can stop (BMB-311): the hash is only in this object, nothing is written. From the key slot on - the new slot
        // stored, the old one dropped, the row, the security stamp, the remote tokens, the doors - it runs to its consistent end whatever the caller does.
        AfterHashForTests?.Invoke();
        ct.ThrowIfCancellationRequested();

        await RewrapOrProvisionKeySlotAsync(user, newPassword, providedDek);

        if (!await userRepo.TryReplacePasswordAsync(user.Id, loadedRole, loadedKeySlotId, user.KeySlotId, user.PasswordHash, passwordHint != null, user.PasswordHint))
        {
            if (user.KeySlotId is { } madeHere && madeHere != loadedKeySlotId)
                await SlotDoors.DeleteSlotAsync(keySlotRepo, mirror, madeHere);
            await SlotDoors.RewriteAsync(mirror);
            return false;
        }

        await userRepo.BumpSecurityStampAsync(user.Id);
        CallerStateCache.Invalidate(user.Id);
        await RevokeRemoteTokensAsync(user.Id);
        await SlotDoors.RewriteAsync(mirror);
        return true;
    }

    private static InvalidOperationException AccountChangedDuringReplacement() =>
        new("The account changed while the password was being replaced, so nothing was changed. Look at the account again and retry.");

    /// <summary>
    /// Gives a superadmin who has no key slot one derived from the password they just
    /// authenticated with. Promoting a user to superadmin cannot build the slot itself — that
    /// needs the plaintext password, which the promoting admin does not have — so the slot is
    /// provisioned here, at the promoted user's next successful login. No-op when the user
    /// already has a slot, isn't a superadmin, or the vault is currently locked (the master DEK
    /// is unreachable then; the next login retries).
    /// </summary>
    /// <returns>true if a slot was created.</returns>
    public async Task<bool> ProvisionMissingKeySlotAsync(User user, string password)
    {
        if (user.Replicated)
            throw await SharedDoorsGuards.ReplicatedUserRefusalAsync(accountsHomeRepo, nodeIdentityRepo, whitelistRepo);
        if (user.Role != UserRoles.Superadmin || user.KeySlotId.HasValue || !session.IsUnlocked)
            return false;

        var slotId = await CreateUserKeySlotAsync(password);

        // `user` was read before the ~100ms Argon2id derivation above, so it is stale by now.
        // Commit through a conditional UPDATE of key_slot_id alone rather than a whole-row
        // UpdateAsync: two concurrent logins would otherwise each create a slot and leave the
        // loser's orphaned in tbl_key_slot, where UnlockAsync still honours it — an old
        // password that survives every later rotation. Losing the race means our slot is the
        // redundant one, so drop it.
        if (!await userRepo.TryAssignKeySlotAsync(user.Id, slotId))
        {
            await SlotDoors.DeleteSlotAsync(keySlotRepo, mirror, slotId);
            await SlotDoors.RewriteAsync(mirror);
            return false;
        }

        user.KeySlotId = slotId;
        await SlotDoors.RewriteAsync(mirror);
        if (doorsNotifier != null) await doorsNotifier.NotifyChangedAsync(immediate: true);
        return true;
    }

    /// <summary>
    /// The upgrade of ADR 0012 at a superadmin's sign-in while the vault is already open (the unlock path upgrades the door it opened itself):
    /// the user's own slot, when it is a v1 door or its DEK is unknown, is opened with the password that just authenticated and sealed again
    /// as a v2 door for it. Costs one Argon2id derivation, and only while the door still needs it. Best effort: a failure is logged.
    /// </summary>
    /// <returns>true if the door was replaced.</returns>
    public async Task<bool> UpgradeKeySlotDoorAsync(User user, string password)
    {
        if (user.Replicated || user.Role != UserRoles.Superadmin || !user.KeySlotId.HasValue || !session.IsUnlocked) return false;
        byte[]? kek = null;
        byte[]? dek = null;
        try
        {
            var slot = (await keySlotRepo.GetAllAsync()).FirstOrDefault(s => s.SlotId == user.KeySlotId.Value);
            if (slot is null || slot.SlotType != KeySlotTypes.User || slot.Salt is null || slot.ArgonMemory is null || slot.ArgonIterations is null
                || slot.ArgonParallelism is null)
                return false;
            if (!Doors.NeedsUpgrade(slot) && slot.DekFingerprint is not null) return false;
            KeyDerivation.ValidateUntrustedParameters(slot.ArgonMemory.Value, slot.ArgonIterations.Value, slot.ArgonParallelism.Value);
            kek = KeyDerivation.DeriveKek(password, slot.Salt, slot.ArgonMemory.Value, slot.ArgonIterations.Value, slot.ArgonParallelism.Value);
            dek = session.GetMasterDek();
            if (await WalkedToCurrentDekAsync(slot, kek, dek) is { } walked)
            {
                if (walked && doorsNotifier != null)
                    await doorsNotifier.NotifyChangedAsync(immediate: true);
                return walked;
            }
            var upgraded = await SlotDoors.UpgradeOpenedSlotAsync(keySlotRepo, mirror, slot, kek, dek, logger);
            if (upgraded && doorsNotifier != null)
                await doorsNotifier.NotifyChangedAsync(immediate: true);
            return upgraded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The kind of failure only: no key material, no message that could carry it.
            logger?.LogWarning("A key slot door could not be upgraded at sign-in ({Type}); it is tried again at the next sign-in.", ex.GetType().Name);
            return false;
        }
        finally
        {
            if (kek is not null) Array.Clear(kek);
            if (dek is not null) Array.Clear(dek);
        }
    }

    /// <summary>
    /// A straggler of a quiet rotation (2.12) signing in while the vault is already open: the door yields an older master key, so the
    /// open session's key cannot be checked against it. The chain is walked from it to the current key (the lazy rewrap of the unlock path),
    /// which replaces the row with a v2 door through SlotDoors; the replace ends the bridge when this was its last v1 door (DekBridge).
    /// </summary>
    /// <returns>null when the door already yields the current key (the plain upgrade decides); otherwise whether the walk replaced it.</returns>
    private async Task<bool?> WalkedToCurrentDekAsync(MasterKeyStore slot, byte[] kek, byte[] currentDek)
    {
        byte[]? yielded = null;
        try
        {
            try { yielded = Doors.OpenSlot(slot, kek); }
            catch (CryptographicException) { return false; }
            if (CryptographicOperations.FixedTimeEquals(yielded, currentDek)) return null;
            if (lazyRewrap is null || factory is null) return false;
            byte[]? sentinel;
            using (var conn = factory.CreateConnection())
                sentinel = await Dapper.SqlMapper.ExecuteScalarAsync<byte[]?>(conn, "SELECT sentinel_value FROM tbl_node_identity LIMIT 1");
            if (MasterKeyManager.IsSentinelMissing(sentinel)) return false;
            var result = await lazyRewrap.TryRewrapAsync(slot, kek, yielded, sentinel!);
            if (result.RewrappedDek is null) return false;
            var reached = result.Success && CryptographicOperations.FixedTimeEquals(result.RewrappedDek, currentDek);
            Array.Clear(result.RewrappedDek);
            if (reached) logger?.LogInformation("A key slot door from before a master key rotation was walked to the current key at sign-in.");
            return reached;
        }
        finally
        {
            if (yielded is not null) Array.Clear(yielded);
        }
    }

    /// <param name="passwordHint">
    /// The hint a person wrote for this password. Null is for internal callers (seeds, tools, tests) that have no
    /// person; every HTTP endpoint, form, the CLI and the phone apps refuse to create a user without one.
    /// </param>
    public async Task<User> CreateUserAsync(string username, string displayName, string password, string role, bool chatAccess = true, string? passwordHint = null)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required");
        ValidatePassword(password);

        role = await CanonicalRoleNameAsync(role);

        if (role == UserRoles.Superadmin
            && await SharedDoorsGuards.IsSharedNonHomeNodeAsync(accountsHomeRepo, nodeIdentityRepo, sharedDoorsStateRepo, factory))
            throw await SharedDoorsGuards.SharedSuperadminWriteRefusalAsync(accountsHomeRepo, nodeIdentityRepo, whitelistRepo);

        var existing = await userRepo.GetByUsernameAsync(username);
        if (existing != null)
            throw new InvalidOperationException($"Username '{username}' already exists");

        var user = new User
        {
            Username = username.Trim(),
            DisplayName = displayName.Trim(),
            PasswordHash = HashPassword(password),
            PasswordHint = passwordHint == null ? null : PasswordHintPolicy.NormalizeAndValidate(passwordHint, password, username),
            Role = role,
            IsActive = true,
            ChatAccess = chatAccess,
            CreatedAt = DateTime.UtcNow
        };

        if (role == UserRoles.Superadmin)
            user.KeySlotId = await CreateUserKeySlotAsync(password);

        user.Id = await userRepo.CreateAsync(user);
        CallerStateCache.Invalidate(user.Id);
        await SlotDoors.RewriteAsync(mirror);
        if (role == UserRoles.Superadmin && user.KeySlotId.HasValue && doorsNotifier != null)
            await doorsNotifier.NotifyChangedAsync(immediate: true);
        return user;
    }

    /// <summary>
    /// Adds or replaces a hint without changing the password - the Profile page's soft prompt for accounts created
    /// before hints existed. The password is not available here, so the "must not contain the password" rule cannot
    /// run; the length, control-character and username rules still apply. Every password change that DOES know the
    /// password re-checks the hint against it.
    /// </summary>
    public async Task SetPasswordHintAsync(int userId, string passwordHint)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");
        if (user.Replicated)
            throw await SharedDoorsGuards.ReplicatedUserRefusalAsync(accountsHomeRepo, nodeIdentityRepo, whitelistRepo);
        var normalized = PasswordHintPolicy.NormalizeAndValidate(passwordHint, password: "", username: user.Username);
        await userRepo.SetPasswordHintAsync(userId, normalized);
        // A sealed node's sign-in page reads the pre-unlock mirror, not the closed database: save the new text there now.
        await SlotDoors.RewriteAsync(mirror);
        if (user.Role == UserRoles.Superadmin && user.KeySlotId.HasValue && doorsNotifier != null)
            await doorsNotifier.NotifyChangedAsync(immediate: true);
    }

    /// <summary>Test seam: called after the new login hash is made and before anything is written (a test stops the replacement from here).</summary>
    internal Action? AfterHashForTests { get; set; }

    /// <param name="ct">Stops the change before it writes anything (see <see cref="ReplacePasswordAsync"/>); never in the middle of the key-slot rewrite.</param>
    public async Task ChangePasswordAsync(int userId, string oldPassword, string newPassword, CancellationToken ct = default, string? passwordHint = null)
    {
        ct.ThrowIfCancellationRequested();
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");
        if (user.Replicated)
            throw await SharedDoorsGuards.ReplicatedUserRefusalAsync(accountsHomeRepo, nodeIdentityRepo, whitelistRepo);

        if (!VerifyPassword(oldPassword, user.PasswordHash))
            throw new UnauthorizedAccessException("Incorrect current password");
        ct.ThrowIfCancellationRequested();

        // Bumps the security stamp: any outstanding Web cookie (this user's other sessions,
        // and this session too) is rejected on next revalidation. Self-service password
        // change therefore forces a re-login — intended.
        if (!await ReplacePasswordAsync(user, newPassword, ct: ct, passwordHint: passwordHint))
            throw AccountChangedDuringReplacement();
        if (user.Role == UserRoles.Superadmin && user.KeySlotId.HasValue && doorsNotifier != null)
            await doorsNotifier.NotifyChangedAsync(immediate: true);
    }

    /// <param name="ct">Stops the reset before it writes anything (see <see cref="ReplacePasswordAsync"/>); never in the middle of the key-slot rewrite.</param>
    public async Task AdminChangePasswordAsync(int userId, string newPassword, CancellationToken ct = default, string? passwordHint = null)
    {
        ct.ThrowIfCancellationRequested();
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");
        if (user.Replicated)
            throw await SharedDoorsGuards.ReplicatedUserRefusalAsync(accountsHomeRepo, nodeIdentityRepo, whitelistRepo);

        // Admin reset bumps the stamp — logs out every session for this user (intended).
        if (!await ReplacePasswordAsync(user, newPassword, ct: ct, passwordHint: passwordHint))
            throw AccountChangedDuringReplacement();
        if (user.Role == UserRoles.Superadmin && user.KeySlotId.HasValue && doorsNotifier != null)
            await doorsNotifier.NotifyChangedAsync(immediate: true);
    }

    /// <summary>
    /// "Forgot your password": proves the recovery key opens THIS node's master key, then — for a
    /// superadmin only — replaces that user's password the way an admin reset does (login hash, key
    /// slot, security stamp, remote tokens). Reachable without signing in, so everything about it is
    /// built to say "no" in one voice:
    /// <list type="bullet">
    /// <item><description>The recovery key is checked FIRST and always at full cost, before the user is
    /// even looked up, so an unknown name, an ordinary user and a wrong key all cost the same Argon2id
    /// work and come back as the same refusal.</description></item>
    /// <item><description>The session is never touched: the DEK the key unwraps goes straight into the
    /// new key slot and is wiped. The vault is left exactly as locked or unlocked as it was, and the
    /// caller is not signed in.</description></item>
    /// <item><description>Only a superadmin qualifies. A recovery key is the owner's tool; it is
    /// not a way for the holder to take over an ordinary account.</description></item>
    /// <item><description>A key WITH an owner (every key issued since 2.12) resets only that owner. Naming another
    /// superadmin is refused like a wrong key, after the same work — no oracle for who owns which key. A key WITHOUT an
    /// owner (issued before 2.12) keeps the older rule and resets any active superadmin.</description></item>
    /// </list>
    /// Other slots — other superadmins' passwords, every recovery key — are not touched.
    /// </summary>
    /// <exception cref="ArgumentException">The new password breaks the password rules (says nothing
    /// about the key or the user).</exception>
    public async Task<RecoveryResetResult> ResetPasswordWithRecoveryKeyAsync(
        string username, string recoveryKey, string newPassword, string? passwordHint = null)
    {
        ValidatePassword(newPassword);
        // The hint is not a secret and its refusal says nothing about the key or the account, so it is checked
        // before the expensive (and deliberately uniform) refusal path below.
        var normalizedHint = passwordHint == null ? null : PasswordHintPolicy.NormalizeAndValidate(passwordHint, newPassword, username);

        // A key copied from a printout or an email tends to arrive with a line break or a space at
        // either end (or wrapped in the middle). The key itself is Base64 and holds no whitespace.
        recoveryKey = string.Concat((recoveryKey ?? "").Where(c => !char.IsWhiteSpace(c)));

        var opened = await session.TryOpenWithRecoveryKeyAsync(recoveryKey);
        var dek = opened?.Dek;
        try
        {
            // A successful reset performs two more costly operations after opening the recovery
            // slot: hashing the new login password and deriving the KEK for its replacement slot.
            // Every refusal must pay those two operations too. Otherwise a wrong key would finish
            // after just the slot attempt, while an unknown or ordinary user with a valid key
            // would take roughly three derivations -- a username oracle for someone holding a
            // recovery card. With no recovery slots at all, pay the missing slot attempt as well.
            var hasRecoverySlot = (await keySlotRepo.GetAllAsync()).Any(s => s.SlotType == "recovery");
            if (dek == null)
            {
                if (!hasRecoverySlot) BurnPasswordVerification(recoveryKey);
                BurnPasswordVerification(recoveryKey);
                BurnPasswordVerification(recoveryKey);
            }

            var user = await userRepo.GetByUsernameAsync((username ?? "").Trim());
            // A recovery key is personal (2.12): a key WITH an owner resets only that owner. Naming anybody else — even another
            // superadmin — is refused exactly like a wrong key, after the same work, so the answer is no oracle for who owns what.
            // A key WITHOUT an owner (issued before 2.12) keeps the old rule: it resets any active superadmin. An account a removal process
            // holds is never eligible (2.12, plan E4 S0).
            var ownsTheKey = opened is null || opened.OwnerUserId is null || user?.Id == opened.OwnerUserId;
            if (dek != null && ownsTheKey && user is { Replicated: true })
            {
                BurnPasswordVerification(recoveryKey);
                BurnPasswordVerification(recoveryKey);
                var (homeId, homeName) = await SharedDoorsGuards.ResolveHomeAsync(accountsHomeRepo, nodeIdentityRepo, whitelistRepo);
                return new RecoveryResetResult(RecoveryResetOutcome.ReplicatedUser, user.Id, homeId, homeName);
            }

            var eligible = user is { Role: UserRoles.Superadmin, IsBeingRemoved: false } && ownsTheKey;

            if (dek == null || user == null || !eligible)
            {
                // A VALID key with an ineligible name still pays the two derivations a real reset
                // does (new login hash, new slot), so it does not come back faster than the others.
                if (dek != null)
                {
                    BurnPasswordVerification(recoveryKey);
                    BurnPasswordVerification(recoveryKey);
                }
                return new RecoveryResetResult(
                    dek == null ? RecoveryResetOutcome.WrongKey : RecoveryResetOutcome.NotEligible,
                    user?.Id);
            }

            // The account stopped being a superadmin's (or changed otherwise) between the lookup and the write: it is not eligible any more.
            if (!await ReplacePasswordAsync(user, newPassword, dek, passwordHint: normalizedHint))
                return new RecoveryResetResult(RecoveryResetOutcome.NotEligible, user.Id);
            if (doorsNotifier != null) await doorsNotifier.NotifyChangedAsync(immediate: true);
            return new RecoveryResetResult(RecoveryResetOutcome.Reset, user.Id);
        }
        finally
        {
            if (dek != null) Array.Clear(dek);
        }
    }

    /// <summary>
    /// Deletes a user. In one transaction: the key slot, the remote API tokens, the agents (revoked, their wrapped master keys wiped), the security stamp
    /// and the account itself (a deleted person's token or agent never outlives them). See <see cref="ApplyDeletionAsync"/>.
    /// </summary>
    public async Task DeleteUserAsync(int userId, string? actor = null)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");
        if (user.Replicated)
            throw await SharedDoorsGuards.ReplicatedUserRefusalAsync(accountsHomeRepo, nodeIdentityRepo, whitelistRepo);

        // While a removal process holds the person, only that process changes the account (release 2.12.0, plan E5).
        if (user.IsBeingRemoved)
            throw new InvalidOperationException("This person is being removed: the removal process decides what happens to the account.");

        if (user.Role == UserRoles.Superadmin)
        {
            var allActiveUsers = await userRepo.ListActiveAsync();
            var remainingSuperadmins = allActiveUsers.Count(u =>
                u.Role == UserRoles.Superadmin && u.Id != userId);
            if (remainingSuperadmins == 0)
                throw new InvalidOperationException("Cannot delete the last superadmin");
        }

        // Can still throw and abort the deletion: nothing has been touched yet, and the wrapped key material of the person's agents
        // (shown once at creation, not recoverable) must not be wiped for a deletion that then fails.
        if (user.KeySlotId.HasValue)
            await EnsureAnotherSuperadminHoldsAKeySlotAsync(userId, "delete");

        var wasSuperadmin = user.Role == UserRoles.Superadmin;
        await ApplyDeletionAsync(user, actor: actor);
        if (wasSuperadmin && doorsNotifier != null)
            await doorsNotifier.NotifyChangedAsync(immediate: true);
    }

    /// <returns>
    /// true if <paramref name="password"/> was actually applied. It only is when the update
    /// promotes a slot-less user to superadmin; in every other case it is ignored, and the
    /// caller (audit log) must not claim a password change that did not happen.
    /// </returns>
    public async Task<bool> UpdateUserAsync(int userId, string displayName, string? role, string? password = null, bool? chatAccess = null, string? passwordHint = null)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");
        if (user.Replicated)
            throw await SharedDoorsGuards.ReplicatedUserRefusalAsync(accountsHomeRepo, nodeIdentityRepo, whitelistRepo);

        user.DisplayName = displayName.Trim();

        if (chatAccess.HasValue)
            user.ChatAccess = chatAccess.Value;

        // Canonicalize before anything compares against it: "User" and "user" name the same role,
        // and treating that as a change would bump the security stamp and re-run the key-slot
        // logic for a no-op edit. An unknown role throws here, before any state is touched.
        if (role != null)
            role = await CanonicalRoleNameAsync(role);

        if (role == UserRoles.Superadmin && user.Role != UserRoles.Superadmin
            && await SharedDoorsGuards.IsSharedNonHomeNodeAsync(accountsHomeRepo, nodeIdentityRepo, sharedDoorsStateRepo, factory))
            throw await SharedDoorsGuards.SharedSuperadminWriteRefusalAsync(accountsHomeRepo, nodeIdentityRepo, whitelistRepo);

        bool roleChanged = false;
        bool passwordChanged = false;
        if (role != null && role != user.Role)
        {
            // While a removal process holds the person, only that process changes the role (release 2.12.0, plan E5).
            if (user.IsBeingRemoved)
                throw new InvalidOperationException("This person is being removed: the removal process decides what happens to the account.");
            var oldRole = user.Role;

            // Any move off superadmin is a demotion, and a demotion says what the person may see afterwards, in one transaction with the loss of
            // their door, tokens and agents: it is DemoteSuperadminAsync, never a role string written on its own.
            var demotedFromSuperadmin = oldRole == UserRoles.Superadmin && role != UserRoles.Superadmin;
            if (demotedFromSuperadmin)
                throw new InvalidOperationException(
                    "Taking superadmin rights away needs an explicit choice of what the person may see afterwards: use the demotion operation.");

            // Promotion (release 2.12.0, plan E7): refused while a removal of this person is open anywhere this node knows of; a person removed before
            // gets a new person id, so the old tombstone never takes the new rights away again (a restore or a join would otherwise apply it).
            if (role == UserRoles.Superadmin && oldRole != UserRoles.Superadmin)
                await PrepareForPromotionAsync(user);

            user.Role = role;
            roleChanged = true;

            // Key slots are keyed off the slot the user actually holds, not off the role they
            // used to have — a demote that failed midway leaves a superadmin's slot behind, and
            // re-promoting must not orphan it by creating a second one.
            if (role != UserRoles.Superadmin && user.KeySlotId.HasValue)
            {
                // A user who is not a superadmin but still holds a slot: what a demotion that failed midway left behind before
                // demotion became one transaction. Dropping the only slot left would lock the vault permanently, so this can still refuse.
                await EnsureAnotherSuperadminHoldsAKeySlotAsync(userId, "demote");
                // ...and, as in the frame, only while another superadmin holds a sealed door: the device box is published again from one.
                if (factory is not null)
                {
                    using var conn = factory.CreateConnection();
                    await EnsureAnotherSuperadminHoldsASealedDoorAsync(conn, null, userId);
                }

                await SlotDoors.DeleteSlotAsync(keySlotRepo, mirror, user.KeySlotId.Value);
                user.KeySlotId = null;
                await RepublishDeviceBoxAsync();
            }
            else if (role == UserRoles.Superadmin && !user.KeySlotId.HasValue)
            {
                // A key slot wraps the master DEK with a KEK derived from the user's *plaintext*
                // password, which the promoting admin does not have. So promotion deliberately
                // leaves the slot unset: the user keeps their own password and the slot is
                // provisioned at their next login (ProvisionMissingKeySlotAsync) or on an admin
                // password reset. Until then they hold the role but cannot unlock a locked vault.
                // A caller that does know the password can hand it over to skip the wait — that
                // also resets the login password, since the slot's password and the login
                // password must stay the same secret.
                //
                // The SAME limitation applies to this user's existing agents, and there is no
                // workaround for it: an agent's wrapped DEK is derived from its own plaintext API
                // key (AgentKeyHelper.EncryptDekV1), which — unlike the user's login password —
                // was only ever shown once at creation and is not recoverable from the KeyHash
                // stored in tbl_agent. A promoted user's pre-existing agents therefore stay
                // exactly as they were (CanAutoUnlock == false) permanently; only a NEWLY created
                // agent, made after this promotion, will be wrapped. The Admin/Profile UI's
                // per-agent "can wake a locked node" indicator reflects this truthfully rather
                // than implying promotion silently upgraded old keys.
                if (!string.IsNullOrWhiteSpace(password))
                {
                    ValidatePassword(password);
                    if (passwordHint != null)
                        user.PasswordHint = PasswordHintPolicy.NormalizeAndValidate(passwordHint, password, user.Username);
                    user.KeySlotId = await CreateUserKeySlotAsync(password);
                    user.PasswordHash = HashPassword(password);
                    passwordChanged = true;
                }
            }
        }

        await userRepo.UpdateAsync(user);

        // A role change (promote/demote) is identity-affecting: bump the stamp so the
        // user's existing cookie is revalidated against the new role promptly.
        if (roleChanged)
        {
            await userRepo.BumpSecurityStampAsync(userId);
            // The folder-ACL cache is keyed per user and holds the rules resolved through the
            // PREVIOUS role. Without this the user keeps their old role's folder access for up
            // to the cache TTL — permissive-stale, i.e. a security bug, whenever the new role is
            // the more restricted one.
            folderAccess.InvalidateCache(userId);
        }

        // Promoting with an explicit password also reset the login password, so treat it like
        // any other admin reset: outstanding remote API tokens must not survive it.
        if (passwordChanged)
            await RevokeRemoteTokensAsync(userId);

        // What the API says this person is (role, active) is read from the row: forget the answer it may have kept.
        CallerStateCache.Invalidate(userId);

        // A promotion with a password gives a slot; a leftover slot of a non-superadmin is dropped.
        await SlotDoors.RewriteAsync(mirror);
        if (user.Role == UserRoles.Superadmin && (passwordChanged || user.KeySlotId.HasValue) && doorsNotifier != null)
            await doorsNotifier.NotifyChangedAsync(immediate: true);
        return passwordChanged;
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new ArgumentException("Password must be at least 8 characters long.");
        if (!password.Any(char.IsUpper))
            throw new ArgumentException("Password must contain at least one uppercase letter.");
        if (!password.Any(char.IsLower))
            throw new ArgumentException("Password must contain at least one lowercase letter.");
        if (!password.Any(char.IsDigit))
            throw new ArgumentException("Password must contain at least one digit.");
    }

    public static string HashPassword(string password)
    {
        var salt = SecureRandom.GetBytes(CryptoConstants.SaltSize);
        var hash = KeyDerivation.DeriveKek(password, salt);
        var result = $"$argon2id${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        Array.Clear(hash);
        return result;
    }

    public static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "argon2id") return false;

        var salt = Convert.FromBase64String(parts[1]);
        var expectedHash = Convert.FromBase64String(parts[2]);

        var actualHash = KeyDerivation.DeriveKek(password, salt);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        finally
        {
            Array.Clear(actualHash);
            Array.Clear(expectedHash);
        }
    }
}

/// <summary>Why a recovery-key password reset did, or did not, happen. For the audit log only: callers must answer all refusals alike.</summary>
public enum RecoveryResetOutcome
{
    Reset,
    /// <summary>No recovery slot of this node opens with the key.</summary>
    WrongKey,
    /// <summary>The key is right, but the name is unknown, inactive, not a superadmin, or not the key's owner.</summary>
    NotEligible,
    /// <summary>The key is right, but the named account is replicated from the accounts node.</summary>
    ReplicatedUser,
}

/// <param name="UserId">The account that was reset, or the one that refused (null when no such user).</param>
public sealed record RecoveryResetResult(
    RecoveryResetOutcome Outcome,
    int? UserId,
    Guid? AccountsHomeNodeId = null,
    string? AccountsHomeName = null)
{
    public bool Succeeded => Outcome == RecoveryResetOutcome.Reset;
}
