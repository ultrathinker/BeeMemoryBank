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
    /// <summary>
    /// Whether this database holds a node somebody can actually use — which is not the same as
    /// "there is a row in tbl_node_identity".
    ///
    /// <para>An external-key identity (v=2, a blind node) is the whole of that node's
    /// initialization: it has no vault, so there is no owner, slot or sentinel to look for.</para>
    ///
    /// <para>Every other node needs a vault that can be opened: an active user with a key slot, and
    /// the sentinel that says which master key that slot is bound to. The identity row alone used to
    /// be the entire test, and the restore bootstrap writes that row first — a crash anywhere in the
    /// rest of it (the slot, the admin, the sentinel) left a node that called itself initialized,
    /// refused the next restore with "restore needs a fresh node", and had no slot to unlock with.
    /// Nothing but wiping the volume cleared that (review release-a #6). Writing the whole bootstrap
    /// in one transaction is the other way to close it; this closes it for every partial write, from
    /// any version, not only the ones a transaction would cover.</para>
    ///
    /// <para>Both halves have been written by every initialization path since the first release —
    /// init, join, the phone's setup, restore — so no healthy node changes its answer here. The one
    /// way a real node looks uninitialized is a database somebody emptied by hand, which is exactly
    /// a node that should get the setup wizard rather than fail to unlock forever.</para>
    ///
    /// <para><b>A vault from before the password unification is the exception that must not be
    /// missed.</b> Nodes older than the per-user slot era carry a single shared
    /// <c>slot_type='password'</c> row and no user pointing at a slot — the users are linked, and
    /// that row is promoted, by <see cref="LegacyPasswordSlotMigrationService"/> on the FIRST
    /// SUCCESSFUL UNLOCK. So on an upgraded vault that has not been unlocked yet, the modern half
    /// of this test is false while the vault is perfectly intact and one password away from being
    /// usable again. Answering "not initialized" there sends the operator (and the Web and phone
    /// clients) to the setup wizard instead of the unlock prompt, and the migration that would fix
    /// the shape never runs — the upgrade brick (review release-a2, agy#2 / sec#5).</para>
    /// </summary>
    public async Task<bool> IsInitializedAsync()
    {
        var identity = await nodeRepo.GetAsync();
        if (identity is null) return false;
        if (identity.Ed25519PrivateKeyV == NodeIdentityCrypto.ExternalKeyVersion) return true;

        var slots = await keySlotRepo.GetAllAsync();

        // The legacy shape, checked first because it is proof on its own: a shared password slot is
        // a vault with a password, and nothing in this build creates one (AddPasswordSlotAsync
        // allows "user" and "recovery" only, and every initialization path writes a user slot). The
        // sentinel is deliberately not required here: it would re-brick exactly the vaults this
        // branch exists for, and a torn restore — the case the sentinel guards — writes a user slot,
        // never this one.
        if (slots.Any(s => s.SlotType == LegacyPasswordSlotMigrationService.LegacySlotType)) return true;

        // The sentinel: it is one value on the row already read, and a node without it cannot be
        // unlocked by any slot (SessionService verifies every slot against it).
        if (await nodeRepo.GetSentinelAsync() is null) return false;

        // Then an owner: an active user that has a key slot. The slot rows and the users are read
        // through the repositories, not by joining tables here.
        var slotIds = slots.Select(s => s.SlotId).ToHashSet();
        return (await userRepo.ListActiveAsync()).Any(u => u.KeySlotId is { } id && slotIds.Contains(id));
    }

    /// <summary>
    /// Whether this database already belongs to a node, however uninitialized its shape looks: any
    /// identity row, or any key slot.
    ///
    /// <para>Deliberately coarser than <see cref="IsInitializedAsync"/> — it is the check a SETUP
    /// path makes before writing a new identity and a new master DEK. On a vault whose shape nobody
    /// recognizes (a pre-unification one, a half-written one), answering "not initialized" is
    /// survivable; initializing over it is not: the operator's data is still there, still wrapped
    /// under the old key, and the new DEK is the only one the node knows. R2's live mixed-version
    /// test caught exactly that: on a legacy vault, /api/init/standalone succeeded, wrote a SECOND
    /// tbl_node_identity row and a setupadmin with a fresh DEK. IsInitializedAsync now recognizes
    /// the legacy shape, and this is the second line of defence behind it (review release-a2, U2c).</para>
    /// </summary>
    public async Task<bool> IsClaimedAsync()
        => await nodeRepo.GetAsync() is not null || (await keySlotRepo.GetAllAsync()).Count > 0;

    public async Task InitializeAsync(string adminUsername, string nodeDisplayName, string password, bool canGenerateEmbeddings = true)
    {
        if (await IsInitializedAsync())
            throw new InvalidOperationException("Node is already initialized.");

        // The coarse guard too: whatever IsInitializedAsync answered about this database's SHAPE,
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
