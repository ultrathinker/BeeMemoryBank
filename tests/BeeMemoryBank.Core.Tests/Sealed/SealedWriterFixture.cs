using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// The real services of the vault (user management, key management, session) over an initialised test vault, with an
/// <see cref="UnlockMaterialMirror"/> that thinks the node is sealed: the boot store lives in a temporary folder beside it. A test calls a
/// writer and then <see cref="AssertDoorsEqualSealedSetAsync"/> (docs/design/step-b-spec.md 15.2.3).
/// </summary>
public abstract class SealedWriterFixture : TestFixture
{
    protected const string AdminPassword = "AdminPass1";

    internal string BootDir { get; private set; } = null!;
    internal BootStore Boot { get; private set; } = null!;
    internal BootDoorStore Doors { get; private set; } = null!;
    internal BootDbKeyStore Wrappers { get; private set; } = null!;
    internal FakeGate Gate { get; private set; } = null!;
    internal UnlockMaterialMirror Mirror { get; private set; } = null!;
    protected byte[] Dbk { get; } = RandomNumberGenerator.GetBytes(32);
    protected Guid BootNodeId { get; } = Guid.NewGuid();

    protected UserService Users { get; private set; } = null!;
    protected KeyManagementService Keys { get; private set; } = null!;
    protected UserRepository UserRepo { get; private set; } = null!;
    protected KeySlotRepository KeySlots { get; private set; } = null!;
    protected AgentRepository AgentRepo { get; private set; } = null!;
    protected RoleRepository Roles { get; private set; } = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", AdminPassword);
        await Session.UnlockAsync(AdminPassword);

        BootDir = Path.Combine(Path.GetTempPath(), "bmb_writers_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(BootDir);
        Boot = new BootStore(Path.Combine(BootDir, "beememorybank.boot.db"));
        await Boot.EnsureCreatedAsync();
        await Boot.SetMetaAsync(BootMetaKeys.NodeId, BootNodeId.ToString("D"));
        await Boot.SetMetaAsync(BootMetaKeys.DbkGeneration, "1");
        Doors = new BootDoorStore(Boot.Connections);
        Wrappers = new BootDbKeyStore(Boot.Connections);
        Gate = new FakeGate();
        Mirror = new UnlockMaterialMirror(Factory, Boot, Doors, Wrappers, new BootStoreWriter(Boot.Connections), Gate, new FakeVaultKeys(Dbk),
            () => Session.GetCandidateDeks());

        UserRepo = new UserRepository(Factory);
        KeySlots = new KeySlotRepository(Factory);
        Roles = new RoleRepository(Factory);
        AgentRepo = new AgentRepository(Factory);
        var folderAccess = new FolderAccessService(new ServiceCollection()
            .AddSingleton<IDbConnectionFactory>(_ => Factory)
            .AddScoped<IUserRepository>(_ => UserRepo)
            .AddScoped<IRoleRepository>(_ => Roles)
            .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(Factory))
            .AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(Factory))
            .AddScoped<IFolderRepository>(_ => new FolderRepository(Factory, ScopeHolder))
            .AddScoped(_ => ScopeHolder)
            .BuildServiceProvider());
        Users = new UserService(UserRepo, KeySlots, Session, Roles, folderAccess, agentRepo: AgentRepo, factory: Factory, mirror: Mirror);
        Keys = new KeyManagementService(KeySlots, Session, UserRepo, Factory, mirror: Mirror);

        // The migration of a node (spec 10.4 step 6) writes the first complete boot store; the rewrite here stands in for it.
        await Mirror.RewriteAsync();
    }

    public override Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(BootDir, true); } catch (IOException) { }
        return base.DisposeAsync();
    }

    internal async Task<long> SealedVersionAsync()
    {
        using var c = Factory.CreateConnection();
        return await c.ExecuteScalarAsync<long>("SELECT CAST(value AS INTEGER) FROM tbl_migration_marker WHERE key = 'unlock_material_version'");
    }

    /// <summary>The set of doors the sealed database implies: every key slot and every active agent with a wrapped DEK, by the formula of spec 4.2.</summary>
    internal async Task<List<(string Kind, long Source, string Hash)>> SealedDoorsAsync()
    {
        using var c = Factory.CreateConnection();
        var list = new List<(string, long, string)>();
        foreach (var s in await c.QueryAsync<(long Id, string Type, byte[] W, byte[] Iv, byte[]? Salt, long? Mem, long? It, long? Par)>(
                     "SELECT slot_id, slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism FROM tbl_key_slot"))
            list.Add((BootDoorKinds.Slot, s.Id, BootDoorHash.OfSlot(s.Id, s.Type, s.W, s.Iv, s.Salt, (int?)s.Mem, (int?)s.It, (int?)s.Par)));
        foreach (var a in await c.QueryAsync<(long Id, string KeyHash, long? Kdf, byte[]? Salt, byte[] W, byte[] Iv)>(
                     "SELECT id, key_hash, kdf_version, salt, encrypted_dek, dek_iv FROM tbl_agent WHERE encrypted_dek IS NOT NULL AND dek_iv IS NOT NULL AND status = 'A'"))
            list.Add((BootDoorKinds.Agent, a.Id, BootDoorHash.OfAgent(a.Id, a.KeyHash, (int?)a.Kdf, a.Salt, a.W, a.Iv)));
        return list.OrderBy(d => d.Item1).ThenBy(d => d.Item2).ToList();
    }

    /// <summary>After a writer: the boot doors equal the sealed set, none is pending, and the mirrored version is the sealed one.</summary>
    internal async Task AssertDoorsEqualSealedSetAsync(string because = "")
    {
        var boot = (await Doors.ListAsync()).ToList();
        boot.Should().OnlyContain(d => !d.Pending, "a finished operation leaves no pending door. " + because);
        boot.Select(d => (d.Kind, d.SourceId, d.ContentSha256)).OrderBy(d => d.Kind).ThenBy(d => d.SourceId)
            .Should().Equal(await SealedDoorsAsync(), "the boot doors equal the key slots and the auto-unlock agents. " + because);
        (await Boot.GetMetaAsync(BootMetaKeys.UnlockMaterialVersion)).Should().Be((await SealedVersionAsync()).ToString(), "the mirrored version is the sealed one. " + because);
    }

    protected async Task<int> CreateWrappedAgentAsync(int ownerId, string name = "agent")
    {
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var dek = Session.GetMasterDek();
        try
        {
            var (ciphertext, iv, salt) = AgentKeyHelper.EncryptDekV1(apiKey, dek);
            return await AgentRepo.CreateAsync(new BeeMemoryBank.Core.Models.Agent
            {
                Name = name, KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey), KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
                EncryptedDek = ciphertext, DekIV = iv, Salt = salt, KdfVersion = 1, Status = "A", CreatedAt = DateTime.UtcNow, OwnerUserId = ownerId,
            });
        }
        finally
        {
            Array.Clear(dek);
        }
    }
}
