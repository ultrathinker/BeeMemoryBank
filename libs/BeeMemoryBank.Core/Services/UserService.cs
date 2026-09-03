using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Core.Services;

public class UserService(
    IUserRepository userRepo,
    IKeySlotRepository keySlotRepo,
    SessionService session,
    IRoleRepository roleRepo,
    FolderAccessService folderAccess,
    IRemoteApiTokenRepository? remoteTokenRepo = null,
    IAgentRepository? agentRepo = null)
{
    // Roles are rows now, not a hard-coded pair. The only role this service still special-cases
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
    // changes (Claude round-3 finding). Without this, an attacker who captured
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
    // while the vault is open, and there is no other way to obtain it.
    private async Task<int> CreateUserKeySlotAsync(string password)
    {
        if (!session.IsUnlocked)
            throw new InvalidOperationException("Session must be unlocked to create a key slot");

        var masterDek = session.GetMasterDek();
        byte[]? kek = null;
        try
        {
            var salt = KeyDerivation.GenerateSalt();
            kek = KeyDerivation.DeriveKek(password, salt);
            var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, kek);

            return await keySlotRepo.CreateAsync(new MasterKeyStore
            {
                SlotType = "user",
                EncryptedMasterDek = encryptedDek,
                IV = iv,
                Salt = salt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory,
                ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                CreatedAt = DateTime.UtcNow
            });
        }
        finally
        {
            Array.Clear(masterDek);
            Array.Clear(kek);
        }
    }

    public async Task<User?> AuthenticateAsync(string username, string password)
    {
        var user = await userRepo.GetByUsernameAsync(username);
        if (user == null) return null;

        if (!VerifyPassword(password, user.PasswordHash)) return null;

        await userRepo.UpdateLastLoginAsync(user.Id);
        return user;
    }

    /// <summary>
    /// Throws unless some OTHER active superadmin would still hold a key slot after this user
    /// loses theirs. Counting rows in tbl_key_slot is not equivalent: a `recovery` slot opens
    /// only with the recovery key, and an `os_auto_unlock` slot has no KDF params at all so
    /// UnlockAsync skips it outright — either one inflates a raw count past the guard while
    /// leaving nobody able to unlock with a password. Since promotion now defers slot creation,
    /// "another superadmin exists" no longer implies "another superadmin can unlock".
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
    private async Task RewrapOrProvisionKeySlotAsync(User user, string newPassword)
    {
        if (user.KeySlotId.HasValue)
        {
            var newSlotId = await CreateUserKeySlotAsync(newPassword);
            await keySlotRepo.DeleteAsync(user.KeySlotId.Value);
            user.KeySlotId = newSlotId;
        }
        else if (user.Role == UserRoles.Superadmin && session.IsUnlocked)
        {
            user.KeySlotId = await CreateUserKeySlotAsync(newPassword);
        }
    }

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
            await keySlotRepo.DeleteAsync(slotId);
            return false;
        }

        user.KeySlotId = slotId;
        return true;
    }

    public async Task<User> CreateUserAsync(string username, string displayName, string password, string role, bool chatAccess = true)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required");
        ValidatePassword(password);

        role = await CanonicalRoleNameAsync(role);

        var existing = await userRepo.GetByUsernameAsync(username);
        if (existing != null)
            throw new InvalidOperationException($"Username '{username}' already exists");

        var user = new User
        {
            Username = username.Trim(),
            DisplayName = displayName.Trim(),
            PasswordHash = HashPassword(password),
            Role = role,
            IsActive = true,
            ChatAccess = chatAccess,
            CreatedAt = DateTime.UtcNow
        };

        if (role == UserRoles.Superadmin)
            user.KeySlotId = await CreateUserKeySlotAsync(password);

        user.Id = await userRepo.CreateAsync(user);
        return user;
    }

    public async Task ChangePasswordAsync(int userId, string oldPassword, string newPassword)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");

        if (!VerifyPassword(oldPassword, user.PasswordHash))
            throw new UnauthorizedAccessException("Incorrect current password");

        ValidatePassword(newPassword);
        user.PasswordHash = HashPassword(newPassword);

        await RewrapOrProvisionKeySlotAsync(user, newPassword);

        await userRepo.UpdateAsync(user);
        // Bump the security stamp: any outstanding Web cookie (this user's other sessions,
        // and this session too) is rejected on next revalidation. Self-service password
        // change therefore forces a re-login — acceptable per the W3 design.
        await userRepo.BumpSecurityStampAsync(userId);
        await RevokeRemoteTokensAsync(userId);
    }

    public async Task AdminChangePasswordAsync(int userId, string newPassword)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");

        ValidatePassword(newPassword);
        user.PasswordHash = HashPassword(newPassword);

        await RewrapOrProvisionKeySlotAsync(user, newPassword);

        await userRepo.UpdateAsync(user);
        // Admin reset bumps the stamp — logs out every session for this user (intended).
        await userRepo.BumpSecurityStampAsync(userId);
        await RevokeRemoteTokensAsync(userId);
    }

    public async Task DeleteUserAsync(int userId)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");

        if (user.Role == UserRoles.Superadmin)
        {
            var allActiveUsers = await userRepo.ListActiveAsync();
            var remainingSuperadmins = allActiveUsers.Count(u =>
                u.Role == UserRoles.Superadmin && u.Id != userId);
            if (remainingSuperadmins == 0)
                throw new InvalidOperationException("Cannot delete the last superadmin");
        }

        if (user.KeySlotId.HasValue)
        {
            await EnsureAnotherSuperadminHoldsAKeySlotAsync(userId, "delete");
            await keySlotRepo.DeleteAsync(user.KeySlotId.Value);
        }

        // H6: strip the wrapped master DEK from every agent this user owns. Deleting the account
        // leaves its agent rows in tbl_agent (owner_user_id is ON DELETE RESTRICT and DeleteAsync
        // only flips is_active), so without this a deleted superadmin's agent keys stay vault keys
        // forever — the same hole demotion already closes, reached by a different door. Placed
        // after the last-superadmin and key-slot guards above, which can still throw and abort the
        // deletion: wiping key material that cannot be re-wrapped (the plaintext API key was shown
        // once at creation and is not recoverable from key_hash) must not happen for a deletion
        // that then fails.
        if (agentRepo != null)
            await agentRepo.ClearWrappedDekForOwnerAsync(userId);

        // Bump the stamp so the deleted user's outstanding Web cookie is rejected on next
        // revalidation. Done before the soft-delete so it lands even though DeleteAsync only
        // flips is_active (the stamp lookup ignores is_active, so the bumped value still resolves).
        await userRepo.BumpSecurityStampAsync(userId);
        // Drop their cached folder rules too. Nothing should be able to authenticate as them
        // afterwards, but leaving a permissive entry behind for the cache TTL is not a bet worth
        // taking for one dictionary removal.
        folderAccess.InvalidateCache(userId);

        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var suffix = new string(Enumerable.Range(0, 3)
                .Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
            var releasedUsername = $"{user.Username}_del_{suffix}";
            try
            {
                await userRepo.DeleteAsync(userId, releasedUsername);
                return;
            }
            catch (InvalidOperationException ex) when (ex.Message == "username_conflict" && attempt < 2)
            {
                // retry with a different suffix
            }
        }
        throw new InvalidOperationException("Failed to release username after 3 attempts. Please try again.");
    }

    /// <returns>
    /// true if <paramref name="password"/> was actually applied. It only is when the update
    /// promotes a slot-less user to superadmin; in every other case it is ignored, and the
    /// caller (audit log) must not claim a password change that did not happen.
    /// </returns>
    public async Task<bool> UpdateUserAsync(int userId, string displayName, string? role, string? password = null, bool? chatAccess = null)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");

        user.DisplayName = displayName.Trim();

        if (chatAccess.HasValue)
            user.ChatAccess = chatAccess.Value;

        // Canonicalize before anything compares against it: "User" and "user" name the same role,
        // and treating that as a change would bump the security stamp and re-run the key-slot
        // logic for a no-op edit. An unknown role throws here, before any state is touched.
        if (role != null)
            role = await CanonicalRoleNameAsync(role);

        bool roleChanged = false;
        bool passwordChanged = false;
        if (role != null && role != user.Role)
        {
            var oldRole = user.Role;

            // Any move off superadmin is a demotion — not just a move to the built-in 'user'
            // role. Before custom roles existed those were the same thing.
            var demotedFromSuperadmin = oldRole == UserRoles.Superadmin && role != UserRoles.Superadmin;

            if (demotedFromSuperadmin)
            {
                var allActiveUsers = await userRepo.ListActiveAsync();
                var remainingSuperadmins = allActiveUsers.Count(u =>
                    u.Role == UserRoles.Superadmin && u.Id != userId);
                if (remainingSuperadmins == 0)
                    throw new InvalidOperationException("Cannot demote the last superadmin");
            }

            user.Role = role;
            roleChanged = true;

            // Key slots are keyed off the slot the user actually holds, not off the role they
            // used to have — a demote that failed midway leaves a superadmin's slot behind, and
            // re-promoting must not orphan it by creating a second one.
            if (role != UserRoles.Superadmin && user.KeySlotId.HasValue)
            {
                // "Cannot demote the last superadmin" above is not enough on its own: the
                // remaining superadmins may all be promoted-but-not-yet-logged-in, and so hold
                // no slot. Dropping the only slot left would lock the vault permanently. This can
                // still throw and abort the whole role change — see the H6 comment below for why
                // that ordering matters.
                await EnsureAnotherSuperadminHoldsAKeySlotAsync(userId, "demote");

                await keySlotRepo.DeleteAsync(user.KeySlotId.Value);
                user.KeySlotId = null;
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
                    user.KeySlotId = await CreateUserKeySlotAsync(password);
                    user.PasswordHash = HashPassword(password);
                    passwordChanged = true;
                }
            }

            // H6 fix: a demoted user must not keep agents that can auto-unlock the vault. Only a
            // superadmin's agents are allowed to carry a wrapped master DEK (AgentEndpoints /
            // Agent.CanAutoUnlock) — leaving a stale one behind on this user's existing agents
            // would let a demoted admin (or anyone who steals one of their old keys) keep unlocking
            // the vault indefinitely, exactly the backdoor this fix closes. This does NOT depend on
            // whether the user held a key slot above — an agent can have been minted at any point
            // while its owner was still a superadmin, key slot or not. Only clears wrapped key
            // material; the agent keeps authenticating exactly like an ordinary user's agent
            // always has. Placed AFTER the key-slot branch above deliberately: that branch can
            // still throw ("their key slot is the only remaining way to unlock the vault"), and an
            // aborted demotion must not have already, irreversibly, wiped agent key material that
            // cannot be re-wrapped without the plaintext API key. Also deliberately not swallowed
            // in a try/catch the way RevokeRemoteTokensAsync is below — unlike a stale remote
            // token, a wrapped DEK left behind IS the H6 vulnerability, so a failure here must
            // fail the whole role change rather than silently succeed with the backdoor still open.
            if (demotedFromSuperadmin && agentRepo != null)
                await agentRepo.ClearWrappedDekForOwnerAsync(userId);
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
