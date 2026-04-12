using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Initial node setup: key generation, master DEK creation, whitelist addition.
/// </summary>
public class InitializationService(
    INodeIdentityRepository nodeRepo,
    IKeySlotRepository keySlotRepo,
    IWhitelistRepository whitelistRepo)
{
    public async Task<bool> IsInitializedAsync()
        => await nodeRepo.GetAsync() != null;

    /// <summary>
    /// Initializes the node:
    /// 1. Generates Ed25519 keys
    /// 2. Generates master DEK
    /// 3. Derives KEK from password, wraps master DEK
    /// 4. Adds itself to the whitelist
    /// </summary>
    public async Task InitializeAsync(string displayName, string password, bool canGenerateEmbeddings = false)
    {
        if (await IsInitializedAsync())
            throw new InvalidOperationException("Node is already initialized.");

        // 1. Ed25519 key pair
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var nodeId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var identity = new NodeIdentity
        {
            NodeId = nodeId,
            DisplayName = displayName,
            Ed25519PublicKey = publicKey,
            Ed25519PrivateKey = privateKey,
            CanGenerateEmbeddings = canGenerateEmbeddings,
            CreatedAt = now
        };
        await nodeRepo.CreateAsync(identity);

        // 2. Master DEK
        var masterDek = MasterKeyManager.GenerateMasterDek();

        // 3. Password slot
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek(password, salt);
        var (encryptedDek, iv) = MasterKeyManager.WrapMasterDek(masterDek, kek);

        var slot = new MasterKeyStore
        {
            SlotType = "password",
            EncryptedMasterDek = encryptedDek,
            IV = iv,
            Salt = salt,
            ArgonMemory = CryptoConstants.DefaultArgonMemory,
            ArgonIterations = CryptoConstants.DefaultArgonIterations,
            ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
            CreatedAt = now
        };
        await keySlotRepo.CreateAsync(slot);

        // Save sentinel for DEK compatibility check between nodes
        var sentinel = MasterKeyManager.ComputeSentinel(masterDek);
        await nodeRepo.StoreSentinelAsync(sentinel);

        // Clear master DEK (no longer needed — session will be opened via UnlockAsync)
        Array.Clear(masterDek);

        // 4. Add ourselves to the whitelist
        var entry = new WhitelistEntry
        {
            NodeId = nodeId,
            DisplayName = displayName,
            Ed25519PublicKey = publicKey,
            CanGenerateEmbeddings = canGenerateEmbeddings,
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        };
        await whitelistRepo.CreateAsync(entry);
    }
}
