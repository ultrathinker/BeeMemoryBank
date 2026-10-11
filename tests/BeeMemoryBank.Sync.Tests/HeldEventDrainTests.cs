using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The drain of held events (docs/design/step-a-spec.md 6.7, 16.2 item 6): nothing while locked; at unlock every held row
/// in order, exactly once; the held rows survive a restart; a network restore still in progress stops the drain at its
/// sequence; a key hold whose key no metadata_key_set named within 30 days is settled as unknown, one whose key row exists
/// but does not open yet keeps waiting. The whole cycle logs no canary (item 9).
/// </summary>
public class HeldEventDrainTests : IAsyncLifetime
{
    private readonly StepALogCapture _log = new();
    private readonly ConcreteFixture _node;
    private readonly StepAAuthor _author = new();
    private readonly byte[] _dek = RandomNumberGenerator.GetBytes(32);
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<HeldEventDrainService> _drains = [];
    private string _blob = "";

    public HeldEventDrainTests() => _node = new ConcreteFixture(_log);

    public async Task InitializeAsync()
    {
        await _node.InitializeAsync();
        var (pub, seed) = Ed25519Signer.GenerateKeyPair();
        await _node.NodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = Guid.NewGuid(), DisplayName = "Receiver", Ed25519PublicKey = pub, Ed25519PrivateKey = seed,
            Ed25519PrivateKeyV = 0, CreatedAt = DateTime.UtcNow
        });
        await _node.WhitelistRepo.CreateAsync(_author.Entry("Author", superadmin: true));
        _blob = await new BlobRepository(_node.Factory).StoreAsync(RandomNumberGenerator.GetBytes(80));
        (await _node.EventApplier.ApplyAsync(_author.NewKey(_dek))).Should().Be(EventApplyResult.Applied, "a control event applies locked");
    }

    public async Task DisposeAsync()
    {
        foreach (var drain in _drains) drain.Dispose();
        foreach (var provider in _providers) await provider.DisposeAsync();
        CryptographicOperations.ZeroMemory(_dek);
        await _node.DisposeAsync();
    }

    [Fact]
    public async Task NothingDrainsWhileLocked_AtUnlockEveryHeldRowIsApplied_InOrder_ExactlyOnce()
    {
        var (article, events) = await HoldAnArticleAsync("First title", "Second title");
        var drain = DrainFor(_node);

        (await drain.DrainAsync()).Should().Be(0, "a locked node cannot open anything");
        (await new HeldEventStatus(_node.Factory).GetSummaryAsync()).Held.Should().Be(events.Count);

        _node.Session.UnlockWithDek((byte[])_dek.Clone());
        (await drain.DrainAsync()).Should().Be(events.Count);
        (await _node.ArticleRepo.GetByIdAsync(article))!.Title.Should().Be("Second title", "applied in order: the update last");
        var status = await new HeldEventStatus(_node.Factory).GetSummaryAsync();
        status.Held.Should().Be(0);
        status.AppliedThroughSequence.Should().Be(status.HeadSequence);

        (await drain.DrainAsync()).Should().Be(0, "exactly once");
        using var conn = _node.Factory.CreateConnection();
        (await conn.QueryAsync<long>("SELECT apply_state FROM tbl_event WHERE event_type = 'sealed'"))
            .Should().HaveCount(events.Count).And.OnlyContain(s => s == EventApplyStates.Applied);
    }

    [Fact]
    public async Task HeldRows_SurviveARestart_AndDrainAfterIt()
    {
        var (article, events) = await HoldAnArticleAsync("Before the restart", "Still before the restart");

        // The process restarts: new session (locked), key ring, applier and drain over the same database.
        var restarted = new SharedFixture(_node.Factory);
        await restarted.InitializeAsync();
        try
        {
            (await new HeldEventStatus(restarted.Factory).GetSummaryAsync()).Held.Should().Be(events.Count);
            restarted.Session.UnlockWithDek((byte[])_dek.Clone());
            (await DrainFor(restarted).DrainAsync()).Should().Be(events.Count);
            (await restarted.ArticleRepo.GetByIdAsync(article))!.Title.Should().Be("Still before the restart");
        }
        finally
        {
            await restarted.DisposeAsync();
        }
    }

    [Fact]
    public async Task ANetworkRestoreInProgress_StopsTheDrainAtItsSequence_UntilItFinishes()
    {
        var restore = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = _author.NodeId, LamportTs = 1, EventType = EventTypes.RestoreNetwork,
            Payload = "{}", Signature = new byte[64], ProtocolVersion = 1, CreatedAt = DateTime.UtcNow,
        };
        await _node.EventLogRepo.AppendAsync(restore);
        var restoreStates = new RestoreEventStateRepository(_node.Factory);
        var now = DateTime.UtcNow.ToString("O");
        await restoreStates.UpsertAsync(new RestoreEventStateRow(restore.EventId.ToString(), RestoreEventState.Downloading,
            null, false, null, null, now, now));
        var (article, events) = await HoldAnArticleAsync("After the restore", "After the restore, edited");
        _node.Session.UnlockWithDek((byte[])_dek.Clone());
        var drain = DrainFor(_node);

        (await drain.DrainAsync()).Should().Be(0, "content received after a restore waits for it");
        (await _node.ArticleRepo.GetByIdAsync(article)).Should().BeNull();

        await restoreStates.UpdateStateAsync(restore.EventId.ToString(), RestoreEventState.Applied);
        (await drain.DrainAsync()).Should().Be(events.Count);
        (await _node.ArticleRepo.GetByIdAsync(article)).Should().NotBeNull();
    }

    [Fact]
    public async Task AKeyNobodyAnnounced_IsSettledAfter30Days_AKeyThatExistsButDoesNotOpenKeepsWaiting()
    {
        _node.Session.UnlockWithDek((byte[])_dek.Clone());
        // A writer whose key record never reached this node.
        var stranger = new StepAAuthor();
        await _node.WhitelistRepo.CreateAsync(stranger.Entry("Stranger", superadmin: false));
        stranger.NewKey(RandomNumberGenerator.GetBytes(32));
        var unknown = stranger.Sealed(EventTypes.FolderCreate, null, StepAAuthor.FolderCreate(Guid.NewGuid(), "/Unknown", "Unknown"));
        (await _node.EventApplier.ApplyAsync(unknown)).Should().Be(EventApplyResult.Held);
        // A writer whose key record arrived, wrapped under a DEK this node does not hold (a rotation it has not accepted).
        var rotated = new StepAAuthor();
        await _node.WhitelistRepo.CreateAsync(rotated.Entry("Rotated", superadmin: true));
        (await _node.EventApplier.ApplyAsync(rotated.NewKey(RandomNumberGenerator.GetBytes(32)))).Should().Be(EventApplyResult.Applied);
        var waiting = rotated.Sealed(EventTypes.FolderCreate, null, StepAAuthor.FolderCreate(Guid.NewGuid(), "/Waiting", "Waiting"));
        (await _node.EventApplier.ApplyAsync(waiting)).Should().Be(EventApplyResult.Held);

        (await DrainFor(_node, new ShiftedTime(TimeSpan.FromDays(29))).DrainAsync()).Should().Be(0, "29 days: still waiting");

        (await DrainFor(_node, new ShiftedTime(TimeSpan.FromDays(31))).DrainAsync()).Should().Be(1);
        var status = await new HeldEventStatus(_node.Factory).GetSummaryAsync();
        status.QuarantinedAfterReceipt.Should().Be(1, "the unknown key is settled");
        status.Held.Should().Be(1, "a key this node has a record of may still open after the rotation is accepted");
        status.HeldKey.Should().Be(1);
    }

    [Fact]
    public async Task TheLockedNodeCycle_LogsNoCanary()
    {
        var canaries = new[] { MetadataCanaries.Ascii("Title"), MetadataCanaries.NonAscii("Edited"), MetadataCanaries.Ascii("Folder") };
        var article = Guid.NewGuid();
        var events = new List<SyncEvent>
        {
            _author.Sealed(EventTypes.FolderCreate, null, StepAAuthor.FolderCreate(Guid.NewGuid(), "/" + canaries[2], canaries[2])),
            _author.Sealed(EventTypes.ArticleCreate, article, StepAAuthor.ArticleCreate(canaries[0], "/" + canaries[2], _blob)),
            _author.Sealed(EventTypes.ArticleUpdate, article, StepAAuthor.ArticleCreate(canaries[1], "/" + canaries[2], _blob)),
        };
        foreach (var e in events) (await _node.EventApplier.ApplyAsync(e)).Should().Be(EventApplyResult.Held);
        // One that will not open: its ciphertext was changed after sealing (quarantined by the drain, with a log line).
        var broken = _author.Sealed(EventTypes.FolderCreate, null, StepAAuthor.FolderCreate(Guid.NewGuid(), "/" + canaries[0], canaries[0]),
            outer =>
            {
                var at = outer.Payload.IndexOf("\"c\":\"", StringComparison.Ordinal) + 6;
                outer.Payload = outer.Payload[..at] + (outer.Payload[at] == 'A' ? 'B' : 'A') + outer.Payload[(at + 1)..];
            });
        (await _node.EventApplier.ApplyAsync(broken)).Should().Be(EventApplyResult.Held);

        _node.Session.UnlockWithDek((byte[])_dek.Clone());
        (await DrainFor(_node).DrainAsync()).Should().Be(events.Count + 1);
        (await _node.ArticleRepo.GetByIdAsync(article))!.Title.Should().Be(canaries[1]);

        _log.Lines.Should().Contain(l => l.Contains("quarantined"), "the scan reads real log lines");
        _log.Scan(canaries).Should().BeEmpty();
    }

    /// <summary>Folder, article create and update, sealed, delivered to the locked node: all held.</summary>
    private async Task<(Guid Article, List<SyncEvent> Events)> HoldAnArticleAsync(string title, string newTitle)
    {
        var article = Guid.NewGuid();
        var events = new List<SyncEvent>
        {
            _author.Sealed(EventTypes.FolderCreate, null, StepAAuthor.FolderCreate(Guid.NewGuid(), "/Held", "Held")),
            _author.Sealed(EventTypes.ArticleCreate, article, StepAAuthor.ArticleCreate(title, "/Held", _blob)),
            _author.Sealed(EventTypes.ArticleUpdate, article, StepAAuthor.ArticleCreate(newTitle, "/Held", _blob)),
        };
        foreach (var e in events)
            (await _node.EventApplier.ApplyAsync(e, new ApplyContext(ReceivedFrom: _author.NodeId, ReceivedSeq: 1)))
                .Should().Be(EventApplyResult.Held);
        return (article, events);
    }

    private HeldEventDrainService DrainFor(SyncTestFixture node, TimeProvider? time = null)
    {
        var provider = new ServiceCollection()
            .AddSingleton(node.EventLogRepo)
            .AddSingleton(node.EventApplier)
            .BuildServiceProvider();
        _providers.Add(provider);
        var drain = new HeldEventDrainService(provider.GetRequiredService<IServiceScopeFactory>(), node.Session, node.Factory,
            new HeldEventStatus(node.Factory), _log.For<HeldEventDrainService>(), time);
        _drains.Add(drain);
        return drain;
    }

    private sealed class ShiftedTime(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }

    private sealed class ConcreteFixture(StepALogCapture log) : SyncTestFixture
    {
        protected override Microsoft.Extensions.Logging.ILogger<EventApplier> CreateApplierLogger() => log.For<EventApplier>();
    }

    private sealed class SharedFixture(BeeMemoryBank.Storage.Sqlite.DbConnectionFactory shared) : SyncTestFixture
    {
        protected override BeeMemoryBank.Storage.Sqlite.DbConnectionFactory CreateFactory() => shared;
        protected override bool OwnsFactory => false;
    }
}
