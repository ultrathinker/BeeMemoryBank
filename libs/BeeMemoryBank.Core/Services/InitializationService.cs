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
    IDbConnectionFactory dbFactory,
    RestoreBootstrapMarker? restoreMarker = null)
{
    // Optional only for the callers that build this service by hand (tests, the odd tool): the
    // default is a working marker over the same database, never "no marker" — a flag that silently
    // stops being read is worse than no flag, and the host passes the registered one.
    private readonly RestoreBootstrapMarker _restoreMarker = restoreMarker ?? new RestoreBootstrapMarker(dbFactory);
    /// <summary>
    /// Whether this database holds a node: the identity row exists — 1.0.11's answer, deliberately
    /// unchanged — and no restore bootstrap is in progress.
    ///
    /// <para><b>Why the old question was wrong, in one number: NULL.</b> The rehearsal on a copy of
    /// a real 1.0.11 server vault (review release-a2 batch 2) found an identity row whose
    /// <c>sentinel_value</c> is NULL, because the sentinel only became part of the vault later.
    /// 1.0.11 counts any identity row as initialized and unlocks such a vault happily
    /// (<c>SessionService</c> treats a missing sentinel as nothing to check against, then heals it);
    /// a release that asks for the sentinel, an owner or a slot on top of the row therefore reads
    /// that vault as an empty one and sends its operator to the setup wizard — where the wizard
    /// would have written a second identity and a second master DEK over data still sealed under
    /// the first (R2's MIXED-A U2c). Legacy password slots were the same mistake in a different
    /// shape: an upgraded vault is not usable until its first unlock runs the migration that
    /// promotes the slot, so any test that describes the post-migration shape re-bricks exactly the
    /// vault it is meant to protect.</para>
    ///
    /// <para>The identity row is not enough in exactly one case, and it is a writer's own: the
    /// restore bootstrap writes that row first and the slot, the admin, the sentinel and the older
    /// keys after it. A crash in between leaves a node that cannot be unlocked and must be restored
    /// again — so while that bootstrap is running, <see cref="RestoreBootstrapMarker"/> is up and
    /// this answers false, which is what lets the retry in (and the retry resumes rather than
    /// repeats). The marker is lowered in the same transaction as the write that completes the
    /// bootstrap.</para>
    ///
    /// <para>A setup path must not rely on this answer for anything: it is shape-reading, and the
    /// shapes are open-ended (a torn restore is "not initialized" on purpose, and its data must
    /// still not be written over). <see cref="IsClaimedAsync"/> is the check setup paths make.</para>
    /// </summary>
    public async Task<bool> IsInitializedAsync()
    {
        if (await nodeRepo.GetAsync() is null) return false;
        return !await _restoreMarker.IsSetAsync();
    }

    /// <summary>
    /// Whether this database already belongs to a node, however uninitialized its shape looks: any
    /// identity row, or any key slot.
    ///
    /// <para>Deliberately coarser than <see cref="IsInitializedAsync"/>, and it is the check a SETUP
    /// path makes before writing a new identity and a new master DEK. IsInitializedAsync answers a
    /// question about a shape, and one shape — a restore bootstrap in progress — must answer "not
    /// initialized" so the restore can be retried. Setup must not read that as an invitation: the
    /// half-written identity is still this node's, its data is still wrapped under a key only the
    /// recovery material holds, and a new master DEK written next to it would be the only one the
    /// node knows. R2's live mixed-version test caught exactly that outcome on a legacy vault:
    /// /api/init/standalone succeeded and wrote a SECOND tbl_node_identity row and a setupadmin
    /// with a fresh DEK (review release-a2, U2c).</para>
    /// </summary>
    public async Task<bool> IsClaimedAsync()
        => await nodeRepo.GetAsync() is not null || (await keySlotRepo.GetAllAsync()).Count > 0;

    public async Task InitializeAsync(string adminUsername, string nodeDisplayName, string password, bool canGenerateEmbeddings = true)
    {
        if (await IsInitializedAsync())
            throw new InvalidOperationException("Node is already initialized.");

        // And the coarse guard, whatever IsInitializedAsync answered about this database's SHAPE:
        // a database that already carries an identity or a key slot is somebody's vault.
        if (await IsClaimedAsync())
            throw new InvalidOperationException(
                "This database already holds a node (an identity row or a key slot); refusing to initialize over it. " +
                "Unlock it with its own password, or start from an empty data directory.");

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
            // so a stolen DB file alone cannot be used to impersonate the node. AAD binds to
            // nodeId.
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
            // kek is still null when a step before the key derivation failed; clearing it
            // unconditionally threw ArgumentNullException here and hid that real error.
            if (kek != null) Array.Clear(kek);
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
