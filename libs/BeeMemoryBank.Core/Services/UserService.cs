using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Core.Services;

public class UserService(
    IUserRepository userRepo,
    IKeySlotRepository keySlotRepo,
    SessionService session)
{
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

        var validRoles = new[] { "superadmin", "unlocker", "user" };
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

        if (role is "superadmin" or "unlocker")
        {
            if (!session.IsUnlocked)
                throw new InvalidOperationException("Session must be unlocked to create a user with key slot");

            var masterDek = session.GetMasterDek();
            try
            {
                var salt = KeyDerivation.GenerateSalt();
                var kek = KeyDerivation.DeriveKek(password, salt);
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
            try
            {
                var salt = KeyDerivation.GenerateSalt();
                var kek = KeyDerivation.DeriveKek(newPassword, salt);
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
            }
        }

        await userRepo.UpdateAsync(user);
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
            try
            {
                var salt = KeyDerivation.GenerateSalt();
                var kek = KeyDerivation.DeriveKek(newPassword, salt);
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
            }
        }

        await userRepo.UpdateAsync(user);
    }

    public async Task DeleteUserAsync(int userId)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");

        if (user.KeySlotId.HasValue)
        {
            var allSlots = await keySlotRepo.GetAllAsync();
            if (allSlots.Count <= 1)
                throw new InvalidOperationException(
                    "Cannot delete the last user with a key slot — this would permanently lock the vault.");
            await keySlotRepo.DeleteAsync(user.KeySlotId.Value);
        }

        await userRepo.DeleteAsync(userId);
    }

    public async Task UpdateUserAsync(int userId, string displayName, string? role, string? password = null)
    {
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");

        user.DisplayName = displayName.Trim();

        if (role != null && role != user.Role)
        {
            var validRoles = new[] { "superadmin", "unlocker", "user" };
            if (!validRoles.Contains(role))
                throw new ArgumentException($"Invalid role. Must be one of: {string.Join(", ", validRoles)}");

            var oldRole = user.Role;
            user.Role = role;

            var wasUnlocker = oldRole is "superadmin" or "unlocker";
            var isNowUnlocker = role is "superadmin" or "unlocker";

            if (wasUnlocker && !isNowUnlocker && user.KeySlotId.HasValue)
            {
                // Downgrading from unlock-capable role — remove key slot
                await keySlotRepo.DeleteAsync(user.KeySlotId.Value);
                user.KeySlotId = null;
            }
            else if (!wasUnlocker && isNowUnlocker)
            {
                // Upgrading to unlock-capable role — need to create key slot
                // This requires the user's password (to derive KEK) and an unlocked session
                if (string.IsNullOrWhiteSpace(password))
                    throw new InvalidOperationException(
                        "Password is required when promoting a user to superadmin or unlocker. " +
                        "Use the change-password endpoint after role change, or provide password.");

                if (!session.IsUnlocked)
                    throw new InvalidOperationException("Session must be unlocked to create a key slot");

                var masterDek = session.GetMasterDek();
                try
                {
                    var salt = KeyDerivation.GenerateSalt();
                    var kek = KeyDerivation.DeriveKek(password, salt);
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
                }
            }
        }

        await userRepo.UpdateAsync(user);
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            throw new ArgumentException("Password must be at least 6 characters long.");
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
