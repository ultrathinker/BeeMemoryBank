using System.Globalization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>An applier the test scripts: by default it stores the event in the log (what a real apply ends with) and says Applied.</summary>
internal sealed class ScriptedApplier(IEventLogRepository log) : IRelayEventApplier
{
    public List<(Guid EventId, ApplyContext Context)> Calls { get; } = [];

    /// <summary>Runs instead of the default; may store the event itself, throw, or return any result.</summary>
    public Func<SyncEvent, ApplyContext, Task<EventApplyResult>>? Script { get; set; }

    public async Task<EventApplyResult> ApplyAsync(SyncEvent evt, ApplyContext context, CancellationToken ct = default)
    {
        Calls.Add((evt.EventId, context));
        if (Script is not null) return await Script(evt, context);
        await Store(evt);
        return EventApplyResult.Applied;
    }

    public Task Store(SyncEvent evt) => log.AppendIfNotExistsAsync(evt);
}

internal sealed class RecordingMirror : IUnlockMaterialMirror
{
    public int Rewrites { get; private set; }

    /// <summary>Thrown by the next rewrites, when set.</summary>
    public Exception? FailWith { get; set; }

    public Task RewriteAsync(CancellationToken ct = default)
    {
        Rewrites++;
        return FailWith is { } ex ? Task.FromException(ex) : Task.CompletedTask;
    }
    public Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default) => Task.CompletedTask;
    public Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default) => Task.CompletedTask;
    public Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey, int dbkGeneration,
        IReadOnlyList<byte[]> masterDeks, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// A plain vault database standing for the open sealed one, a boot store with a relay inbox, a gate in a state the test chooses, and the
/// importer over them with an applier the test scripts (docs/design/step-b-spec.md 6.6, 15.2.9-15.2.11).
/// </summary>
internal sealed class RelayImportFixture : IAsyncDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "bmb_relay_import_" + Guid.NewGuid().ToString("N"));
    public DbConnectionFactory Vault { get; }
    public BootStore Boot { get; }
    public RelayInbox Inbox { get; }
    public FakeGate Gate { get; }
    public RelayWriteLock Lock { get; } = new();
    public RecordingMirror Mirror { get; } = new();
    public EventLogRepository EventLog { get; }
    public BlobRepository Blobs { get; }
    public SyncQuarantineRepository Quarantine { get; }
    public SyncPushPositionRepository Positions { get; }
    public ScriptedApplier Applier { get; }
    public ServiceProvider Services { get; }
    public RelayInboxImporter Importer { get; private set; }

    private RelayImportFixture(VaultState state)
    {
        Directory.CreateDirectory(Dir);
        Vault = new DbConnectionFactory(Path.Combine(Dir, "beememorybank.db"));
        Boot = new BootStore(Path.Combine(Dir, "beememorybank.boot.db"));
        Inbox = new RelayInbox(Boot.Connections);
        Gate = new FakeGate(state);
        EventLog = new EventLogRepository(Vault);
        Blobs = new BlobRepository(Vault);
        Quarantine = new SyncQuarantineRepository(Vault);
        Positions = new SyncPushPositionRepository(Vault);
        Applier = new ScriptedApplier(EventLog);

        var services = new ServiceCollection();
        services.AddSingleton<IEventLogRepository>(EventLog);
        services.AddSingleton<IBlobRepository>(Blobs);
        services.AddSingleton<ISyncQuarantineRepository>(Quarantine);
        services.AddSingleton<ISyncPushPositionRepository>(Positions);
        services.AddSingleton<IRelayEventApplier>(Applier);
        Services = services.BuildServiceProvider();
        Importer = NewImporter();
    }

    /// <summary>A second importer over the same files, as a restarted process has.</summary>
    public RelayInboxImporter NewImporter() => new(Boot, Inbox, Lock, Vault, Services.GetRequiredService<IServiceScopeFactory>(), Mirror, Gate);

    public static async Task<RelayImportFixture> CreateAsync(VaultState state = VaultState.Importing)
    {
        var f = new RelayImportFixture(state);
        await new MigrationRunner(f.Vault).RunMigrationsAsync();
        await f.Boot.EnsureCreatedAsync();
        await f.Boot.SetMetaAsync(BootMetaKeys.NodeId, Guid.NewGuid().ToString("D"));
        await f.Boot.SetMetaAsync(BootMetaKeys.SeqFloor, "1");
        await f.Boot.SetMetaAsync(BootMetaKeys.CleanClose, "1");
        return f;
    }

    public static SyncEvent NewEvent(string type = EventTypes.WhitelistAdd, Guid? node = null, string payload = "{}", int protocol = 4)
    {
        var id = Guid.NewGuid();
        return new SyncEvent
        {
            EventId = id, NodeId = node ?? Guid.NewGuid(), LamportTs = 7, EventType = type, Payload = payload,
            Signature = new byte[64], ProtocolVersion = protocol, CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>Puts events in the inbox the way the relay does, from <paramref name="peer"/>; returns them with their relay sequence.</summary>
    public async Task<List<(SyncEvent Event, long Relay)>> ReceiveAsync(Guid peer, params SyncEvent[] events)
    {
        var stored = new List<(SyncEvent, long)>();
        foreach (var evt in events)
            stored.Add((evt, await Inbox.AppendAsync(evt, peer, evt.SequenceNum == 0 ? stored.Count + 1000 : evt.SequenceNum)));
        return stored;
    }

    public async Task<long> LogCountAsync()
    {
        using var c = Vault.CreateConnection();
        return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event");
    }

    public async Task<List<Guid>> LogIdsAsync()
    {
        using var c = Vault.CreateConnection();
        return (await c.QueryAsync<string>("SELECT event_id FROM tbl_event ORDER BY sequence_num")).Select(Guid.Parse).ToList();
    }

    public async Task<long> MetaAsync(string key) =>
        long.Parse((await Boot.GetMetaAsync(key))!, NumberStyles.Integer, CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Vault.Dispose();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}
