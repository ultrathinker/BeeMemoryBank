using System.Data;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services;

public class InitializationService(
    INodeIdentityRepository nodeRepo,
    IKeySlotRepository keySlotRepo,
    IUserRepository userRepo,
    IDbConnectionFactory dbFactory)
{
    public async Task<bool> IsInitializedAsync()
        => await nodeRepo.GetAsync() != null;

    public async Task InitializeAsync(string adminUsername, string nodeDisplayName, string password, bool canGenerateEmbeddings = false)
    {
        if (await IsInitializedAsync())
            throw new InvalidOperationException("Node is already initialized.");

        if (string.IsNullOrWhiteSpace(adminUsername))
            throw new ArgumentException("Admin username is required.", nameof(adminUsername));

        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var nodeId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var masterDek = MasterKeyManager.GenerateMasterDek();
        byte[]? kek = null;
        try
        {
            // Encrypt the Ed25519 private key with the master DEK before persisting,
            // so a stolen DB file alone cannot be used to impersonate the node (closes the
            // CRIT finding from Wave 1 RERUN audit gemini2 #2). AAD binds to nodeId.
            var (wrappedPk, pkIv) = NodeIdentityCrypto.EncryptPrivateKey(privateKey, masterDek, nodeId);
            Array.Clear(privateKey);

            var identity = new NodeIdentity
            {
                NodeId = nodeId,
                DisplayName = nodeDisplayName,
                Ed25519PublicKey = publicKey,
                Ed25519PrivateKey = wrappedPk,
                Ed25519PrivateKeyIV = pkIv,
                Ed25519PrivateKeyV = 1,
                CanGenerateEmbeddings = canGenerateEmbeddings,
                CreatedAt = now
            };
            await nodeRepo.CreateAsync(identity);

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
                CreatedAt = now
            };
            var slotId = await keySlotRepo.CreateAsync(slot);

            var user = new User
            {
                Username = adminUsername.Trim(),
                DisplayName = adminUsername.Trim(),
                PasswordHash = HashPassword(password),
                Role = UserRoles.Superadmin,
                KeySlotId = slotId,
                IsActive = true,
                CreatedAt = now
            };
            await userRepo.CreateAsync(user);

            var sentinel = MasterKeyManager.ComputeSentinel(masterDek);
            await nodeRepo.StoreSentinelAsync(sentinel);

            WriteMigrationMarker();
        }
        finally
        {
            Array.Clear(masterDek);
            Array.Clear(kek);
        }
    }

    private void WriteMigrationMarker()
    {
        using var conn = dbFactory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO tbl_migration_marker (key, value, set_at)
            VALUES (@k, '1', @ts)";
        AddParam(cmd, "k", "legacy_password_unified");
        AddParam(cmd, "ts", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void AddParam(IDbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static string HashPassword(string password)
    {
        var salt = SecureRandom.GetBytes(CryptoConstants.SaltSize);
        var hash = KeyDerivation.DeriveKek(password, salt);
        var result = $"$argon2id${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        Array.Clear(hash);
        return result;
    }
}
