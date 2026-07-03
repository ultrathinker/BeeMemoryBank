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
    IRemoteApiTokenRepository? remoteTokenRepo = null)
{
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

    public async Task<User?> AuthenticateAsync(string username, string password)
    {
        var user = await userRepo.GetByUsernameAsync(username);
        if (user == null) return null;

        if (!VerifyPassword(password, user.PasswordHash)) return null;

        await userRepo.UpdateLastLoginAsync(user.Id);
        return user;
    }

    public async Task<User> CreateUserAsync(string username, string displayName, string password, string role)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required");
        ValidatePassword(password);

        var validRoles = new[] { UserRoles.Superadmin, UserRoles.User };
        if (!validRoles.Contains(role))
            throw new ArgumentException($"Invalid role. Must be one of: {string.Join(", ", validRoles)}");

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
            CreatedAt = DateTime.UtcNow
        };

        if (role == UserRoles.Superadmin)
        {
            if (!session.IsUnlocked)
                throw new InvalidOperationException("Session must be unlocked to create a user with key slot");

            var masterDek = session.GetMasterDek();
            byte[]? kek = null;
            try
            {
                var salt = KeyDerivation.GenerateSalt();
                kek = KeyDerivation.DeriveKek(password, salt);
                var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, kek);

                var slot = new MasterKeyStore
                {
                    SlotType = "user",
                    EncryptedMasterDek = encryptedDek,
                    IV = iv,
                    Salt = salt,
                    ArgonMemory = CryptoConstants.DefaultArgonMemory,
                    ArgonIterations = CryptoConstants.DefaultArgonIterations,
                    ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                    CreatedAt = DateTime.UtcNow
                };
                var slotId = await keySlotRepo.CreateAsync(slot);
                user.KeySlotId = slotId;
            }
            finally
            {
                Array.Clear(masterDek);
                Array.Clear(kek);
            }
        }

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

        if (user.KeySlotId.HasValue)
        {
            if (!session.IsUnlocked)
                throw new InvalidOperationException("Session must be unlocked to change password for user with key slot");

            var masterDek = session.GetMasterDek();
            byte[]? kek = null;
            try
            {
                var salt = KeyDerivation.GenerateSalt();
                kek = KeyDerivation.DeriveKek(newPassword, salt);
                var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, kek);

                var slot = new MasterKeyStore
                {
                    SlotType = "user",
                    EncryptedMasterDek = encryptedDek,
                    IV = iv,
                    Salt = salt,
                    ArgonMemory = CryptoConstants.DefaultArgonMemory,
                    ArgonIterations = CryptoConstants.DefaultArgonIterations,
                    ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                    CreatedAt = DateTime.UtcNow
                };
                var newSlotId = await keySlotRepo.CreateAsync(slot);
                await keySlotRepo.DeleteAsync(user.KeySlotId.Value);
                user.KeySlotId = newSlotId;
            }
            finally
            {
                Array.Clear(masterDek);
                Array.Clear(kek);
            }
        }

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

        if (user.KeySlotId.HasValue)
        {
            if (!session.IsUnlocked)
                throw new InvalidOperationException("Session must be unlocked to change password for user with key slot");

            var masterDek = session.GetMasterDek();
            byte[]? kek = null;
            try
            {
                var salt = KeyDerivation.GenerateSalt();
                kek = KeyDerivation.DeriveKek(newPassword, salt);
                var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, kek);

                var slot = new MasterKeyStore
                {
                    SlotType = "user",
                    EncryptedMasterDek = encryptedDek,
                    IV = iv,
                    Salt = salt,
                    ArgonMemory = CryptoConstants.DefaultArgonMemory,
                    ArgonIterations = CryptoConstants.DefaultArgonIterations,
                    ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                    CreatedAt = DateTime.UtcNow
                };
                var newSlotId = await keySlotRepo.CreateAsync(slot);
                await keySlotRepo.DeleteAsync(user.KeySlotId.Value);
                user.KeySlotId = newSlotId;
            }
            finally
            {
                Array.Clear(masterDek);
                Array.Clear(kek);
            }
        }

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
            var allSlots = await keySlotRepo.GetAllAsync();
            if (allSlots.Count <= 1)
                throw new InvalidOperationException(
                    "Cannot delete the last user with a key slot — this would permanently lock the vault.");
            await keySlotRepo.DeleteAsync(user.KeySlotId.Value);
        }

        // Bump the stamp so the deleted user's outstanding Web cookie is rejected on next
        // revalidation. Done before the soft-delete so it lands even though DeleteAsync only
        // flips is_active (the stamp lookup ignores is_active, so the bumped value still resolves).
        await userRepo.BumpSecurityStampAsync(userId);

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

    public async Task UpdateUserAsync(int userId, string displayName, string? role, string? password = null)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");

        user.DisplayName = displayName.Trim();

        bool roleChanged = false;
        if (role != null && role != user.Role)
        {
            var validRoles = new[] { UserRoles.Superadmin, UserRoles.User };
            if (!validRoles.Contains(role))
                throw new ArgumentException($"Invalid role. Must be one of: {string.Join(", ", validRoles)}");

            var oldRole = user.Role;

            if (oldRole == UserRoles.Superadmin && role == UserRoles.User)
            {
                var allActiveUsers = await userRepo.ListActiveAsync();
                var remainingSuperadmins = allActiveUsers.Count(u =>
                    u.Role == UserRoles.Superadmin && u.Id != userId);
                if (remainingSuperadmins == 0)
                    throw new InvalidOperationException("Cannot demote the last superadmin");
            }

            user.Role = role;
            roleChanged = true;

            var hadKeySlot = oldRole == UserRoles.Superadmin;
            var needsKeySlot = role == UserRoles.Superadmin;

            if (hadKeySlot && !needsKeySlot && user.KeySlotId.HasValue)
            {
                await keySlotRepo.DeleteAsync(user.KeySlotId.Value);
                user.KeySlotId = null;
            }
            else if (!hadKeySlot && needsKeySlot)
            {
                if (string.IsNullOrWhiteSpace(password))
                    throw new InvalidOperationException(
                        "Password is required when promoting a user to superadmin. " +
                        "Use the change-password endpoint after role change, or provide password.");

                if (!session.IsUnlocked)
                    throw new InvalidOperationException("Session must be unlocked to create a key slot");

                var masterDek = session.GetMasterDek();
                byte[]? kek = null;
                try
                {
                    var salt = KeyDerivation.GenerateSalt();
                    kek = KeyDerivation.DeriveKek(password, salt);
                    var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, kek);

                    var slot = new MasterKeyStore
                    {
                        SlotType = "user",
                        EncryptedMasterDek = encryptedDek,
                        IV = iv,
                        Salt = salt,
                        ArgonMemory = CryptoConstants.DefaultArgonMemory,
                        ArgonIterations = CryptoConstants.DefaultArgonIterations,
                        ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                        CreatedAt = DateTime.UtcNow
                    };
                    var slotId = await keySlotRepo.CreateAsync(slot);
                    user.KeySlotId = slotId;
                }
                finally
                {
                    Array.Clear(masterDek);
                    Array.Clear(kek);
                }
            }
        }

        await userRepo.UpdateAsync(user);

        // A role change (promote/demote) is identity-affecting: bump the stamp so the
        // user's existing cookie is revalidated against the new role promptly.
        if (roleChanged)
            await userRepo.BumpSecurityStampAsync(userId);
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
