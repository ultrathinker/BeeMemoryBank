using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Vault.Sealed;
using BeeMemoryBank.Vault.SharedDoors;
using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Manages access slots for the master DEK: password change, recovery key, slot deletion.
/// </summary>
public class KeyManagementService(
    IKeySlotRepository keySlotRepo,
    SessionService session,
    IUserRepository userRepo,
    IDbConnectionFactory connFactory,
    IRecoveryBoxPublisher? recoveryBoxes = null,
    IUnlockMaterialMirror? mirror = null,
    IAuditLogRepository? audit = null,
    IAccountsHomeRepository? accountsHomeRepo = null,
    IWhitelistRepository? whitelistRepo = null,
    INodeIdentityRepository? nodeIdentityRepo = null,
    ISuperadminDoorsNotifier? doorsNotifier = null)
{
    // Step B (docs/design/step-b-spec.md 3.6, 4.3): a key slot is a door of a sealed node. The pending door is written before the sealed
    // transaction commits, the copies of a deleted slot's door are retired before the delete, and the boot store is rewritten once the
    // operation is done. With no mirror (or on a plain node, where the mirror does nothing) every call below is the plain call it was.

    /// <summary>
    /// Legacy password-change endpoint, kept for backward compatibility with older mobile app
    /// builds. The canonical path is UserService.ChangePasswordAsync(userId, ...). When a
    /// "password" or "user" slot exists and the old password unwraps it, the slot is rotated and
    /// any tbl_user pointing at the old slot is updated to point at the new one. To be removed
    /// once those mobile builds are retired.
    /// </summary>
    /// <summary>Test seam: called after the new key is derived and before the slot is written (a test stops the change from here).</summary>
    internal Action? AfterDerivationForTests { get; set; }

    /// <summary>Test seam: awaited in <see cref="RemoveSlotAsync"/> after the last-door guard and before the delete (a test widens the
    /// check-then-act window to prove two concurrent revokes are serialised).</summary>
    internal Func<Task>? AfterRemoveGuardForTests { get; set; }

    /// <param name="ct">Stops the change while it is still only computation - before the old password is tried, and after the new key is derived - and never once the
    /// new slot is being written: the swap of the two slots is one transaction that runs to its end.</param>
    public async Task ChangePasswordAsync(string oldPassword, string newPassword, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var slots = await keySlotRepo.GetAllAsync();
        // Try ALL password-derived slots (legacy "password" + post-migration "user").
        // Recovery slots are intentionally excluded — they're not changed via this flow.
        var candidateSlots = slots.Where(s => s.SlotType == "password" || s.SlotType == "user").ToList();

        MasterKeyStore? oldSlot = null;
        byte[]? masterDek = null;

        foreach (var slot in candidateSlots)
        {
            byte[]? oldKek = null;
            try
            {
                oldKek = KeyDerivation.DeriveKek(
                    oldPassword, slot.Salt!,
                    slot.ArgonMemory!.Value, slot.ArgonIterations!.Value, slot.ArgonParallelism!.Value);
                masterDek = Doors.OpenSlot(slot, oldKek);
                oldSlot = slot;
                Array.Clear(oldKek);
                break;
            }
            catch (KdfBusyException) { throw; }
            catch { if (oldKek != null) Array.Clear(oldKek); }
        }

        if (masterDek == null || oldSlot == null)
            throw new InvalidOperationException("Incorrect old password.");

        byte[]? newKek = null;
        try
        {
            var owner = (await userRepo.ListActiveAsync()).FirstOrDefault(u =>
                u.KeySlotId == oldSlot.SlotId || (oldSlot.OwnerUserId is { } oid && u.Id == oid));
            if (owner is { Replicated: true })
                throw await SharedDoorsGuards.ReplicatedUserRefusalAsync(accountsHomeRepo, nodeIdentityRepo, whitelistRepo);

            ct.ThrowIfCancellationRequested();
            var newSalt = KeyDerivation.GenerateSalt();
            newKek = KeyDerivation.DeriveKek(newPassword, newSalt);
            AfterDerivationForTests?.Invoke();
            ct.ThrowIfCancellationRequested();   // the last point: from the transaction below it is one atomic swap of the two slots

            // Preserve the original slot type so we don't accidentally re-introduce "password"
            // slots on a node that's already been migrated to "user". For the unupgraded-mobile
            // case the old slot is "user" already, so the new slot is also "user". The new door is a
            // sealed (v2) door (ADR 0012), except for the legacy single slot, whose type changes at its
            // migration: it stays a v1 wrap that names its DEK.
            MasterKeyStore newSlot;
            if (oldSlot.SlotType == KeySlotTypes.Password)
            {
                var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, newKek);
                newSlot = new MasterKeyStore
                {
                    SlotType = oldSlot.SlotType,
                    EncryptedMasterDek = encryptedDek,
                    IV = iv,
                    Salt = newSalt,
                    ArgonMemory = CryptoConstants.DefaultArgonMemory,
                    ArgonIterations = CryptoConstants.DefaultArgonIterations,
                    ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                    CreatedAt = DateTime.UtcNow,
                    DekFingerprint = DekFingerprint.Of(masterDek),
                    OwnerUserId = oldSlot.OwnerUserId,
                    CreatedByUserId = oldSlot.CreatedByUserId,
                    Label = oldSlot.Label,
                };
            }
            else
            {
                // The owner and the maker go with the door: the triggers of 046 set the owner only once tbl_user is repointed
                // below, after the commit, and nothing would ever set the maker again.
                newSlot = Doors.NewPasswordSlot(oldSlot.SlotType, newKek, newSalt, CryptoConstants.DefaultArgonMemory,
                    CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism, masterDek,
                    ownerUserId: oldSlot.OwnerUserId, createdByUserId: oldSlot.CreatedByUserId, label: oldSlot.Label);
            }
            // SECURITY: create the new slot and delete the old one as ONE atomic transaction.
            // As two independent writes, a crash or process kill in between would leave BOTH the
            // old and the new password permanently able to unlock the vault — a "change your
            // password" that silently fails to revoke the old one. IKeySlotRepository.CreateAsync/
            // DeleteAsync accept an optional transaction (mirroring the IArticleRepository
            // contract — see its doc comment) so both writes commit or roll back together.
            int newSlotId;
            using (var conn = connFactory.CreateConnection())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    newSlotId = await keySlotRepo.CreateAsync(newSlot, tx);
                    newSlot.SlotId = newSlotId;
                    if (mirror != null)
                    {
                        // Rules 1 and 2 of the write protocol, while the sealed transaction is still open.
                        await mirror.AddPendingDoorAsync(SlotDoors.PendingFor(newSlot, masterDek));
                        await mirror.RetireDoorAsync(BeeMemoryBank.Storage.Boot.BootDoorKinds.Slot, oldSlot.SlotId);
                    }
                    await keySlotRepo.DeleteAsync(oldSlot.SlotId, tx);
                    tx.Commit();
                }
                catch
                {
                    try { tx.Rollback(); } catch { /* SQLite may have already auto-rolled back; don't mask the real failure */ }
                    await SlotDoors.RewriteAsync(mirror);   // restores the retired door, drops the pending one
                    throw;
                }
            }

            // Repoint any tbl_user row that pointed at the old slot. This intentionally runs
            // AFTER the transaction above commits: IUserRepository doesn't (yet) accept a
            // transaction parameter, and calling it from inside the still-open transaction above
            // would contend for SQLite's single writer lock against itself. The residual
            // non-atomic window here is benign — if a crash lands exactly here, the old slot is
            // already gone (so the old password no longer
            // works) and the new slot already exists and is fully usable via the normal
            // UnlockAsync path (which tries every row in tbl_key_slot regardless of user
            // association); only this user's key_slot_id bookkeeping would be left stale, not
            // vault access.
            await userRepo.RepointKeySlotAsync(oldSlot.SlotId, newSlotId);

            newSlot.SlotId = newSlotId;
            if (recoveryBoxes != null)
                await recoveryBoxes.PublishDeviceBoxAsync(newSlot, masterDek, newKek);
            await SlotDoors.RewriteAsync(mirror);
            if (doorsNotifier != null)
                await doorsNotifier.NotifyChangedAsync(immediate: true, ct);
        }
        finally
        {
            Array.Clear(masterDek);
            if (newKek != null) Array.Clear(newKek);
        }
    }

    /// <summary>
    /// Adds a recovery slot. Returns the recovery key in Base64 for the user to save. The key belongs to the superadmin it is issued for
    /// (release 2.12: a recovery key is personal, not node-wide); <paramref name="ownerUserId"/> null means the node's only active superadmin,
    /// the personal-device case — with nobody or several to choose from it is refused rather than guessed at.
    /// </summary>
    public async Task<string> AddRecoveryKeyAsync(int? ownerUserId = null, string? label = null) =>
        (await IssueRecoveryKeyAsync(ownerUserId, label)).Key;

    /// <summary>
    /// <see cref="AddRecoveryKeyAsync"/> that also says which slot the key opens, for the caller's audit entry (an entry names the slot, never the key).
    /// A key is issued by a superadmin for themself: the caller names its owner (the Web endpoint passes the signed-in person), and the slot records
    /// that person as both owner and creator. Only an active superadmin qualifies; anyone else is refused before a key is made.
    /// </summary>
    /// <param name="ownerUserId">The superadmin the key belongs to; null resolves to the node's only active superadmin (a personal device).</param>
    /// <param name="label">A free text label the person gives the key ("the card in the safe"), at most 120 characters; never key material.</param>
    public async Task<(string Key, int SlotId)> IssueRecoveryKeyAsync(int? ownerUserId = null, string? label = null)
    {
        var masterDek = session.GetMasterDek();
        byte[]? kek = null;
        byte[]? recoveryKeyBytes = null;
        try
        {
            // The owner is checked before a key is made: a refusal must not leave a slot behind.
            var owner = await ResolveRecoveryKeyOwnerAsync(ownerUserId);
            label = NormalizeLabel(label);

            // Recovery key = 32 random bytes, represented as Base64. This string is the ONE secret
            // the user is expected to write down and keep offline — nothing that derives it may be
            // persisted anywhere in the database.
            recoveryKeyBytes = SecureRandom.GetBytes(32);
            var recoveryKeyString = Convert.ToBase64String(recoveryKeyBytes);

            // SECURITY: the salt MUST be independent random material, exactly like every other
            // slot type — see UserService.CreateUserKeySlotAsync. Salt is a plaintext column in
            // tbl_key_slot, so a salt derived from the recovery key (e.g. recoveryKeyBytes itself)
            // would let anyone with read access to the database file (backup, stolen disk, a bug
            // exposing raw rows) rebuild the KEK from the salt column and stored params, unwrap
            // encrypted_master_dek, and read the entire vault. A fresh, unrelated salt reveals
            // nothing; the KEK cannot be reconstructed from the database alone.
            var salt = KeyDerivation.GenerateSalt();
            kek = KeyDerivation.DeriveKek(
                recoveryKeyString,
                salt,
                memory: CryptoConstants.DefaultArgonMemory,
                iterations: CryptoConstants.DefaultArgonIterations,
                parallelism: CryptoConstants.DefaultArgonParallelism);

            // A recovery key is personal (release 2.12): its slot names the superadmin it belongs to, who is also its creator.
            // A key without an owner is only ever a row written before 2.12 — the unknown-owner set of a removal.
            var slot = Doors.NewPasswordSlot(KeySlotTypes.Recovery, kek, salt, CryptoConstants.DefaultArgonMemory,
                CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism, masterDek,
                ownerUserId: owner.Id, createdByUserId: owner.Id, label: label);
            await SlotDoors.CreateSlotAsync(keySlotRepo, connFactory, mirror, slot, masterDek);
            await SlotDoors.RewriteAsync(mirror);

            return (recoveryKeyString, slot.SlotId);
        }
        finally
        {
            Array.Clear(masterDek);
            if (kek != null) Array.Clear(kek);
            if (recoveryKeyBytes != null) Array.Clear(recoveryKeyBytes);
        }
    }

    /// <summary>
    /// Who a new recovery key belongs to: the named user when they are an active superadmin, else the node's ONLY active superadmin
    /// (a personal device issues for its one person). Nobody, several, or a named user who is not an active superadmin: refused —
    /// a key that belongs to nobody is the hole this release closes, so it is never created by guessing.
    /// </summary>
    private async Task<User> ResolveRecoveryKeyOwnerAsync(int? ownerUserId)
    {
        if (ownerUserId is { } id)
        {
            var named = await userRepo.GetByIdAsync(id);
            if (named is null || named.Role != UserRoles.Superadmin)
                throw new InvalidOperationException("A recovery key can only be issued for an active superadmin.");
            return named;
        }

        var superadmins = (await userRepo.ListActiveAsync()).Where(u => u.Role == UserRoles.Superadmin).ToList();
        if (superadmins.Count != 1)
            throw new InvalidOperationException(
                "A recovery key belongs to the superadmin who issues it: name the owner (this node has " +
                (superadmins.Count == 0 ? "no active superadmin" : "several active superadmins") + ").");
        return superadmins[0];
    }

    /// <summary>A label is a memory aid, not a secret: trimmed, one line, at most 120 characters (the hint length).</summary>
    private static string? NormalizeLabel(string? label)
    {
        if (label is null) return null;
        label = label.Trim();
        if (label.Length == 0) return null;
        if (label.Length > 120)
            throw new ArgumentException("The label of a recovery key is at most 120 characters.", nameof(label));
        if (label.Any(c => char.IsControl(c)))
            throw new ArgumentException("The label of a recovery key must be a single line of plain text.", nameof(label));
        return label;
    }

    /// <summary>
    /// Adds an extra per-user password slot (a testing/dev door). Master DEK is taken from the current session — session must be unlocked.
    /// A recovery key is NOT made here: since 2.12 a recovery key is personal and must go through <see cref="IssueRecoveryKeyAsync"/>, which
    /// records its owner — this path would leave it ownerless.
    /// </summary>
    public async Task AddPasswordSlotAsync(string slotType, string password)
    {
        // Whitelist allowed slot types — only "user" (per-user login slots). Legacy "password", "recovery" (a recovery key must be issued
        // with an owner via IssueRecoveryKeyAsync) and any other arbitrary string are rejected to prevent backdoor creation.
        if (slotType != "user")
            throw new ArgumentException(
                $"Slot type '{slotType}' is not supported. Only 'user' is allowed; issue a recovery key with the recovery-key operation.",
                nameof(slotType));

        var masterDek = session.GetMasterDek();
        byte[]? kek = null;
        try
        {
            var salt = KeyDerivation.GenerateSalt();
            kek = KeyDerivation.DeriveKek(password, salt);
            var slot = Doors.NewPasswordSlot(slotType, kek, salt, CryptoConstants.DefaultArgonMemory,
                CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism, masterDek);
            await SlotDoors.CreateSlotAsync(keySlotRepo, connFactory, mirror, slot, masterDek);
            if (recoveryBoxes != null)
                await recoveryBoxes.PublishDeviceBoxAsync(slot, masterDek, kek);
            await SlotDoors.RewriteAsync(mirror);
        }
        finally
        {
            Array.Clear(masterDek);
            Array.Clear(kek);
        }
    }

    /// <summary>
    /// Deletes a slot. Any delete is refused if it would leave the node with no door a person can open (a superadmin's own
    /// password door went while only a machine door - Face ID, OS auto-unlock - was left); the last slot of all is refused as before. Revoking a
    /// recovery key is written to the audit log with the slot's id, its owner, its age and the person who did it; no entry holds any key material.
    /// The guard and the delete hold the revoke gate, so they cannot interleave with another revoke or delete.
    /// </summary>
    /// <param name="actor">Who asked, for the audit entry of a recovery key (the user id the Web layer forwarded); null for a local caller that names nobody.</param>
    public async Task RemoveSlotAsync(int slotId, string? actor = null)
    {
        await SlotDoors.RevokeGate.WaitAsync();
        try { await RemoveSlotUngatedAsync(slotId, actor); }
        finally { SlotDoors.RevokeGate.Release(); }
    }

    /// <summary><see cref="RemoveSlotAsync"/> for a caller that already holds <see cref="SlotDoors.RevokeGate"/>.</summary>
    private async Task RemoveSlotUngatedAsync(int slotId, string? actor)
    {
        var slots = await keySlotRepo.GetAllAsync();
        var slot = slots.FirstOrDefault(s => s.SlotId == slotId)
            ?? throw new KeyNotFoundException($"Slot {slotId} not found.");

        if (slot.SlotType != KeySlotTypes.Recovery && slots.Count <= 1)
            throw new InvalidOperationException("Cannot delete the last access slot.");
        await SlotDoors.EnsureAPersonDoorRemainsAsync(keySlotRepo, userRepo, [slotId], recoveryKey: slot.SlotType == KeySlotTypes.Recovery);
        // A superadmin's password door goes only while another active superadmin holds a sealed (v2) door here (2.12, item A of the removal
        // ): the device box is published again from a sealed door only, and with v1 stragglers alone the box that copied this door would stay.
        if (slot.SlotType == KeySlotTypes.User
            && (await userRepo.ListActiveAsync()).FirstOrDefault(u => u.KeySlotId == slotId && u.Role == UserRoles.Superadmin) is { } owner)
        {
            using var conn = connFactory.CreateConnection();
            await UserService.EnsureAnotherSuperadminHoldsASealedDoorAsync(conn, null, owner.Id);
        }

        if (AfterRemoveGuardForTests != null) await AfterRemoveGuardForTests();
        await SlotDoors.DeleteSlotAsync(keySlotRepo, mirror, slotId);
        if (slot.SlotType == KeySlotTypes.Recovery && audit != null)
            await audit.LogAsync("key_slot", slotId.ToString(), "recovery_key_revoked", actor is null ? "system" : "web",
                $"Recovery key (slot #{slotId}, {OwnerWords(slot.OwnerUserId)}, issued {slot.CreatedAt:yyyy-MM-dd HH:mm} UTC) revoked by {(actor is null ? "a local caller" : "user " + actor)}");
        // A user still pointing at the deleted slot would look like they hold one, which
        // suppresses re-provisioning at their next login (UserService.ProvisionMissingKeySlotAsync)
        // and leaves them unable to unlock with no way to recover short of an admin password reset.
        await userRepo.ClearKeySlotAsync(slotId);
        // The device box may have copied that door (2.12, plan WS-A4).
        if (recoveryBoxes != null && session.IsUnlocked)
        {
            var dek = session.GetMasterDek();
            try { await recoveryBoxes.RepublishForDoorsAsync(dek); }
            finally { Array.Clear(dek); }
        }
        await SlotDoors.RewriteAsync(mirror);
    }

    /// <summary>
    /// Revokes one RECOVERY key through the recovery-revoke route. Unlike <see cref="RemoveSlotAsync"/> (which also deletes other slot types
    /// for the device/security pages), this refuses any slot that is not a recovery key, so the route cannot destroy a password or auto-unlock
    /// door. The last-person-door guard and the audit are the same as a recovery removal.
    /// </summary>
    public async Task RevokeRecoveryKeyAsync(int slotId, string? actor = null)
    {
        // The guard is check-then-act: serialise it with the delete so two concurrent revokes cannot both pass while the other's key still exists.
        await SlotDoors.RevokeGate.WaitAsync();
        try
        {
            var slot = (await keySlotRepo.GetAllAsync()).FirstOrDefault(s => s.SlotId == slotId)
                ?? throw new KeyNotFoundException($"Slot {slotId} not found.");
            if (slot.SlotType != KeySlotTypes.Recovery)
                throw new InvalidOperationException("That slot is not a recovery key; only a recovery key can be revoked here.");
            await RemoveSlotUngatedAsync(slotId, actor);
        }
        finally { SlotDoors.RevokeGate.Release(); }
    }

    /// <summary>
    /// Every recovery key of this node, for the Admin "Recovery keys" page: its slot, its owner (or "no owner" for a key issued before 2.12),
    /// when it was issued and its label. Never any key material — the key itself is shown once at issuance and is not recoverable.
    /// </summary>
    public async Task<List<RecoveryKeyInfo>> ListRecoveryKeysAsync()
    {
        var slots = (await keySlotRepo.GetAllAsync()).Where(s => s.SlotType == KeySlotTypes.Recovery).OrderBy(s => s.SlotId).ToList();
        var users = (await userRepo.ListActiveAsync()).ToDictionary(u => u.Id);
        return slots.Select(s =>
        {
            var owner = s.OwnerUserId is { } oid && users.TryGetValue(oid, out var u) ? u : null;
            return new RecoveryKeyInfo(s.SlotId, s.OwnerUserId, owner?.Username, owner?.DisplayName, s.CreatedAt, s.Label);
        }).ToList();
    }

    /// <summary>
    /// Revokes every recovery key that has NO owner (issued before 2.12, so nobody can say whose it is). Refused as a whole if doing so would
    /// leave the node with no door a person can open. Each revocation goes through the boot-store order and is audited like a single revoke.
    /// </summary>
    /// <returns>The number of keys revoked.</returns>
    public async Task<int> RevokeUnknownOwnerRecoveryKeysAsync(string? actor = null)
    {
        // Same gate as a single revoke: the whole-set guard and the deletes are one check-then-act against any other revoke.
        await SlotDoors.RevokeGate.WaitAsync();
        try
        {
            var unknown = (await keySlotRepo.GetAllAsync())
                .Where(s => s.SlotType == KeySlotTypes.Recovery && s.OwnerUserId is null).Select(s => s.SlotId).ToList();
            if (unknown.Count == 0) return 0;

            // All-or-nothing: the guard looks at the node AFTER every one of these keys is gone.
            await SlotDoors.EnsureAPersonDoorRemainsAsync(keySlotRepo, userRepo, unknown);
            foreach (var slotId in unknown)
                await RemoveSlotUngatedAsync(slotId, actor);
            return unknown.Count;
        }
        finally { SlotDoors.RevokeGate.Release(); }
    }

    /// <summary>How an audit entry names a recovery key's owner: a person, or nobody (a key from before 2.12).</summary>
    internal static string OwnerWords(int? ownerUserId) =>
        ownerUserId is { } oid ? $"owner user #{oid}" : "owner unknown (issued before 2.12)";
}

/// <summary>
/// One recovery key as the Admin "Recovery keys" page lists it (release 2.12). Never key material: the slot id, the owner it belongs to
/// (null for a key issued before 2.12, whose owner is unknown), when it was issued, and the label its owner gave it.
/// </summary>
/// <param name="OwnerUsername">The owner's sign-in name; null when the key has no owner, or the owner's account is no longer active.</param>
/// <param name="OwnerDisplayName">The owner's display name; null on the same terms as <paramref name="OwnerUsername"/>.</param>
public sealed record RecoveryKeyInfo(int SlotId, int? OwnerUserId, string? OwnerUsername, string? OwnerDisplayName, DateTime CreatedAt, string? Label);
