using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// A vault database (a plain test file: the mirror works on any connection factory), a boot store beside it, a gate that says sealed,
/// a session whose DEKs the test controls, and the mirror over all of them (docs/design/step-b-spec.md 15.2.2).
/// </summary>
internal sealed class SealedMirrorFixture : IAsyncDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "bmb_mirror_" + Guid.NewGuid().ToString("N"));
    public DbConnectionFactory Vault { get; }
    public BootStore Boot { get; }
    public BootDoorStore Doors { get; }
    public BootDbKeyStore Wrappers { get; }
    public FakeGate Gate { get; }
    public FakeVaultKeys VaultKeys { get; }
    public byte[] Dbk { get; } = RandomNumberGenerator.GetBytes(32);
    public Guid BootNodeId { get; } = Guid.NewGuid();

    /// <summary>The DEKs the fake session holds, current first.</summary>
    public List<byte[]> SessionDeks { get; } = [];

    public UnlockMaterialMirror Mirror { get; }

    public SealedMirrorFixture(bool isSealed = true)
    {
        Directory.CreateDirectory(Dir);
        Vault = new DbConnectionFactory(Path.Combine(Dir, "beememorybank.db"));
        Boot = new BootStore(Path.Combine(Dir, "beememorybank.boot.db"));
        Doors = new BootDoorStore(Boot.Connections);
        Wrappers = new BootDbKeyStore(Boot.Connections);
        Gate = new FakeGate(isSealed: isSealed);
        VaultKeys = new FakeVaultKeys(Dbk);
        Mirror = new UnlockMaterialMirror(Vault, Boot, Doors, Wrappers, new BootStoreWriter(Boot.Connections), Gate, VaultKeys,
            () => SessionDeks.Select(d => (byte[])d.Clone()).ToArray());
    }

    public static async Task<SealedMirrorFixture> CreateAsync(bool isSealed = true, bool bootExists = true)
    {
        var f = new SealedMirrorFixture(isSealed);
        await new MigrationRunner(f.Vault).RunMigrationsAsync();
        if (bootExists)
        {
            await f.Boot.EnsureCreatedAsync();
            await f.Boot.SetMetaAsync(BootMetaKeys.NodeId, f.BootNodeId.ToString("D"));
            await f.Boot.SetMetaAsync(BootMetaKeys.DbkGeneration, "1");
        }
        return f;
    }

    public byte[] NewDek()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        return dek;
    }

    /// <summary>The wrapped copy of <paramref name="dek"/> a key slot holds (random bytes: the mirror never opens it).</summary>
    public async Task<long> AddSlotAsync(string type = "user", bool withSalt = true)
    {
        using var c = Vault.CreateConnection();
        return await c.ExecuteScalarAsync<long>(
            @"INSERT INTO tbl_key_slot (slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, created_at)
              VALUES (@type, @wrapped, @iv, @salt, @mem, @it, @par, @now); SELECT last_insert_rowid()",
            new
            {
                type, wrapped = RandomNumberGenerator.GetBytes(49), iv = RandomNumberGenerator.GetBytes(12),
                salt = withSalt ? RandomNumberGenerator.GetBytes(32) : null, mem = withSalt ? 65536 : (int?)null,
                it = withSalt ? 3 : (int?)null, par = withSalt ? 4 : (int?)null, now = DateTime.UtcNow.ToString("o"),
            });
    }

    public async Task<long> AddUserAsync(string username, string role = "user", long? slotId = null)
    {
        using var c = Vault.CreateConnection();
        return await c.ExecuteScalarAsync<long>(
            @"INSERT INTO tbl_user (username, display_name, password_hash, role, key_slot_id, created_at)
              VALUES (@username, @username, 'x', @role, @slotId, @now); SELECT last_insert_rowid()",
            new { username, role, slotId, now = DateTime.UtcNow.ToString("o") });
    }

    public async Task<long> AddAgentAsync(string name, long ownerId, bool withDek = true, string status = "A")
    {
        using var c = Vault.CreateConnection();
        return await c.ExecuteScalarAsync<long>(
            @"INSERT INTO tbl_agent (name, key_prefix, key_hash, encrypted_dek, dek_iv, kdf_version, salt, status, created_at, owner_user_id)
              VALUES (@name, 'bee_', @hash, @dek, @iv, 1, @salt, @status, @now, @ownerId); SELECT last_insert_rowid()",
            new
            {
                name, hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
                dek = withDek ? RandomNumberGenerator.GetBytes(49) : null, iv = withDek ? RandomNumberGenerator.GetBytes(12) : null,
                salt = RandomNumberGenerator.GetBytes(16), status, ownerId, now = DateTime.UtcNow.ToString("o"),
            });
    }

    public async Task AddIdentityAsync(string displayName = "Home node")
    {
        using var c = Vault.CreateConnection();
        await c.ExecuteAsync(
            @"INSERT INTO tbl_node_identity (node_id, display_name, ed25519_public_key, ed25519_private_key, created_at)
              VALUES (@id, @displayName, @pub, @priv, @now)",
            new { id = BootNodeId.ToString("D"), displayName, pub = RandomNumberGenerator.GetBytes(32), priv = RandomNumberGenerator.GetBytes(48), now = DateTime.UtcNow.ToString("o") });
    }

    public async Task<Guid> AddPeerAsync(string displayName = "Peer", string status = "A", bool superadmin = false)
    {
        var id = Guid.NewGuid();
        using var c = Vault.CreateConnection();
        await c.ExecuteAsync(
            @"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, is_superadmin, created_at, updated_at)
              VALUES (@id, @displayName, @pub, @status, @sa, @now, @now)",
            new { id = id.ToString("D"), displayName, pub = RandomNumberGenerator.GetBytes(32), status, sa = superadmin ? 1 : 0, now = DateTime.UtcNow.ToString("o") });
        return id;
    }

    public async Task<string> SealedVersionAsync()
    {
        using var c = Vault.CreateConnection();
        return (await c.ExecuteScalarAsync<string>("SELECT value FROM tbl_migration_marker WHERE key = 'unlock_material_version'"))!;
    }

    public async Task<List<(string Kind, long Source)>> BootDoorsAsync() =>
        (await Doors.ListAsync()).Select(d => (d.Kind, d.SourceId)).OrderBy(d => d.Kind).ThenBy(d => d.SourceId).ToList();

    public async Task<List<string>> WrapperFingerprintsAsync() => (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).ToList();

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Vault.Dispose();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
