using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>An <see cref="ISealedRelayLifecycle"/> that writes down what the gate asked of it, in order, and can be slowed down.</summary>
internal sealed class RecordingRelay : ISealedRelayLifecycle
{
    public List<string> Calls { get; } = [];
    public TimeSpan ImportDelay { get; set; }
    public Func<Task>? DuringImport { get; set; }
    public VaultState? StateAtImport { get; set; }
    public Func<VaultState>? ReadState { get; set; }
    public Func<Task>? DuringClosing { get; set; }

    public Task OnOpenedAsync(CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("opened");
        return Task.CompletedTask;
    }

    public async Task<RelayImportResult> ImportAsync(CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("import-start");
        StateAtImport = ReadState?.Invoke();
        if (ImportDelay > TimeSpan.Zero) await Task.Delay(ImportDelay, ct);
        if (DuringImport is not null) await DuringImport();
        lock (Calls) Calls.Add("import-end");
        return new RelayImportResult(0, 0, 0, 0, 0);
    }

    public async Task OnClosingAsync(CancellationToken ct = default)
    {
        lock (Calls) Calls.Add("closing");
        if (DuringClosing is not null) await DuringClosing();
    }

    public Task<IAsyncDisposable> PauseWritesAsync(CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>A logger that keeps what it is told, so a test can read what the log says (and that it never says a key).</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines) Lines.Add($"{logLevel}: {formatter(state, exception)}" + (exception is null ? "" : " | " + exception.GetType().Name));
    }
}

/// <summary>The real mirror behind a counter: what the gate asked of it, and when.</summary>
internal sealed class SpyMirror(IUnlockMaterialMirror inner) : IUnlockMaterialMirror
{
    public int Rewrites;

    public async Task RewriteAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref Rewrites);
        await inner.RewriteAsync(ct);
    }

    public Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default) => inner.AddPendingDoorAsync(door, ct);
    public Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default) => inner.RetireDoorAsync(kind, sourceId, ct);
    public Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default) => inner.AddDbKeyWrapperAsync(masterDek, ct);

    public Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey, int dbkGeneration,
        IReadOnlyList<byte[]> masterDeks, CancellationToken ct = default) =>
        inner.WriteNewBootStoreAsync(bootPath, vault, databaseKey, dbkGeneration, masterDeks, ct);
}

/// <summary>An open step that writes down when it ran and what the gate told it.</summary>
internal sealed class RecordingStep(int order, List<string> log, Func<bool, Task>? action = null) : IVaultOpenStep
{
    public int Order => order;

    public async Task RunAsync(bool firstOpenInProcess, CancellationToken ct)
    {
        lock (log) log.Add($"{order}:{(firstOpenInProcess ? "first" : "again")}");
        if (action is not null) await action(firstOpenInProcess);
    }
}

/// <summary>
/// A real sealed node on disk and the real gate over it (docs/design/step-b-spec.md 15.3): an initialised vault with one superadmin, converted
/// by the page codec exactly as the migration does it, a boot store written by <see cref="UnlockMaterialMirror.WriteNewBootStoreAsync"/>, a
/// <see cref="VaultGate"/>, two keyed factories (vault and chat), the real mirror and a <see cref="SessionService"/> wired to them. Argon2
/// and SQLCipher are real; only the relay inbox is a recorder.
/// </summary>
internal sealed class SealedGateNode : IAsyncDisposable
{
    public const string Password = "AdminPass1";

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "bmb_gate_" + Guid.NewGuid().ToString("N"));
    public string VaultPath => Path.Combine(Dir, "beememorybank.db");
    public string ChatPath => Path.Combine(Dir, "chat.db");
    public string BootPath => Path.Combine(Dir, "beememorybank.boot.db");

    public BootStore Boot { get; private set; } = null!;
    public BootDoorStore Doors { get; private set; } = null!;
    public BootDbKeyStore Wrappers { get; private set; } = null!;
    public DatabaseKeyService KeyService { get; private set; } = null!;
    public VaultGate Gate { get; private set; } = null!;
    public DbConnectionFactory Vault { get; private set; } = null!;
    public DbConnectionFactory Chat { get; private set; } = null!;
    public UnlockMaterialMirror Mirror { get; private set; } = null!;
    public SessionService Session { get; private set; } = null!;
    public ServiceProvider Services { get; private set; } = null!;
    public RecordingRelay Relay { get; } = new();
    public ListLogger<VaultGate> GateLog { get; } = new();
    public ListLogger<SessionService> SessionLog { get; } = new();
    public SpyMirror Spy { get; private set; } = null!;
    public List<string> StepLog { get; } = [];
    public List<IVaultOpenStep> Steps { get; } = [];
    public List<byte[]> Cleared { get; } = [];

    /// <summary>A test seam: the connections the gate is given for the vault file in place of the real factory (a wrapper that fails or records).</summary>
    public Func<ISealableConnections, ISealableConnections>? WrapVaultConnections { get; set; }

    /// <summary>The master DEK of the vault (a copy; the test may clear it).</summary>
    public byte[] MasterDek { get; private set; } = null!;
    public byte[] DatabaseKey { get; private set; } = null!;
    public byte[] VaultPageKey { get; private set; } = null!;
    public byte[] ChatPageKey { get; private set; } = null!;
    public Guid NodeId { get; private set; }

    public static async Task<SealedGateNode> CreateAsync(TimeSpan? drainTimeout = null, bool withChat = true,
        Func<DbConnectionFactory, Task>? seedPlain = null, VaultStoreMode mode = VaultStoreMode.Sealed)
    {
        var node = new SealedGateNode();
        await node.BuildAsync(drainTimeout, withChat, seedPlain, mode);
        return node;
    }

    private async Task BuildAsync(TimeSpan? drainTimeout, bool withChat, Func<DbConnectionFactory, Task>? seedPlain, VaultStoreMode mode)
    {
        DapperConfig.Configure();
        Directory.CreateDirectory(Dir);

        // 1. A plain vault with one superadmin, and a plain chat database.
        var plain = new DbConnectionFactory(VaultPath);
        await new MigrationRunner(plain).RunMigrationsAsync();
        var keySlots = new KeySlotRepository(plain);
        await new InitializationService(new NodeIdentityRepository(plain), keySlots, new UserRepository(plain), plain)
            .InitializeAsync("admin", "GateNode", Password);
        var first = new SessionService(keySlots);
        (await first.UnlockAsync(Password)).Should().BeTrue();
        MasterDek = first.GetMasterDek();
        first.Lock();
        if (seedPlain is not null) await seedPlain(plain);
        using (var c = plain.CreateConnection()) c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        plain.Dispose();
        SqliteConnection.ClearAllPools();
        if (withChat)
        {
            using var chat = new SqliteConnection($"Data Source={ChatPath};Pooling=False");
            chat.Open();
            chat.Execute("CREATE TABLE chat_probe (id INTEGER PRIMARY KEY, title TEXT); INSERT INTO chat_probe (title) VALUES ('gate-probe')");
            chat.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }

        // 2. The sealed node, as the migration makes it: page keys from a fresh database key, the files through the page codec, a boot store.
        Boot = new BootStore(BootPath);
        Doors = new BootDoorStore(Boot.Connections);
        Wrappers = new BootDbKeyStore(Boot.Connections);
        KeyService = new DatabaseKeyService(Boot, Wrappers);
        DatabaseKey = KeyService.Create();
        VaultPageKey = KeyService.DerivePageKey(DatabaseKey, DbPurposes.Vault);
        ChatPageKey = KeyService.DerivePageKey(DatabaseKey, DbPurposes.Chat);
        if (mode == VaultStoreMode.Sealed)
        {
            await SealFileAsync(VaultPath, VaultPageKey);
            if (withChat) await SealFileAsync(ChatPath, ChatPageKey);

            var keys = new InMemoryVaultKeySource(VaultPageKey, ChatPageKey);
            using var readSealed = new DbConnectionFactory(VaultPath, keys);
            var throwaway = new UnlockMaterialMirror(readSealed, Boot, Doors, Wrappers, new BootStoreWriter(Boot.Connections));
            await throwaway.WriteNewBootStoreAsync(BootPath, readSealed, DatabaseKey, 1, [(byte[])MasterDek.Clone()]);
            NodeId = Guid.Parse((await Boot.GetMetaAsync(BootMetaKeys.NodeId))!);
            SqliteConnection.ClearAllPools();
        }

        // 3. The gate, the keyed factories, the mirror and the session.
        Gate = new VaultGate(Boot, KeyService, () => Files(), () => Steps, () => Relay, () => Spy, GateLog, mode, drainTimeout)
        {
            Cleared = buffer => { lock (Cleared) Cleared.Add(buffer); },
        };
        Relay.ReadState = () => Gate.State;
        Vault = new DbConnectionFactory(VaultPath, Gate);
        Chat = new DbConnectionFactory(ChatPath, Gate, DbPurposes.Chat);
        Session = null!;
        Mirror = new UnlockMaterialMirror(Vault, Boot, Doors, Wrappers, new BootStoreWriter(Boot.Connections), Gate, Gate,
            () => Session.GetCandidateDeks());
        Spy = new SpyMirror(Mirror);

        var services = new ServiceCollection();
        services.AddSingleton<IDbConnectionFactory>(Vault);
        services.AddSingleton(Vault);
        services.AddSingleton<IKeySlotRepository>(_ => new KeySlotRepository(Vault));
        services.AddSingleton<INodeIdentityRepository>(_ => new NodeIdentityRepository(Vault));
        services.AddScoped<IUserRepository>(_ => new UserRepository(Vault));
        services.AddScoped<IAgentRepository>(_ => new AgentRepository(Vault));
        services.AddScoped<LegacyPasswordSlotMigrationService>();
        Services = services.BuildServiceProvider();
        Session = new SessionService(Services.GetRequiredService<IKeySlotRepository>(), Services.GetRequiredService<IServiceScopeFactory>(), null, Gate, Doors, Gate, SessionLog);
    }

    private List<SealedFile> Files()
    {
        var files = new List<SealedFile> { new(DbPurposes.Vault, VaultPath, WrapVaultConnections?.Invoke(Vault) ?? Vault, Vault.CreateConnection) };
        files.Add(new SealedFile(DbPurposes.Chat, ChatPath, Chat, Chat.CreateConnection));
        return files;
    }

    private static async Task SealFileAsync(string path, byte[] pageKey)
    {
        var sealedPath = path + ".sealed-tmp";
        await using (var image = File.OpenRead(path))
            await PlainImageImporter.ToSealedFileAsync(image, image.Length, sealedPath, pageKey, memoryCap: 256L << 20);
        foreach (var sidecar in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(path + sidecar)) File.Delete(path + sidecar);
        File.Delete(path);
        File.Move(sealedPath, path);
    }

    /// <summary>The names and counts of what a sealed vault holds, read through a temporary open: a proof that the data survived.</summary>
    public async Task<long> CountUsersAsync()
    {
        using var c = Vault.CreateConnection();
        return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_user");
    }

    public async ValueTask DisposeAsync()
    {
        try { Services?.Dispose(); } catch { }
        try { Gate?.Dispose(); } catch { }
        try { Vault?.Dispose(); } catch { }
        try { Chat?.Dispose(); } catch { }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch { }
        await Task.CompletedTask;
    }
}
