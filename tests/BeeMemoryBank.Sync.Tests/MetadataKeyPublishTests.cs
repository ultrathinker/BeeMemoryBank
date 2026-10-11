using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.DekRotation;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using SealedEnvelope = BeeMemoryBank.Sync.Metadata.SealedEnvelope;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// SA-1 (docs/design/step-a-spec.md 4.4; owner decision 5): the metadata key of a DEK rotation is created in the rotation's own
/// transaction with a durable marker, its <c>metadata_key_set</c> event is appended and the marker deleted in one transaction,
/// the step is retried until it holds (every 60 s and at every unlock, also after a crash), and until then nothing is sealed.
/// </summary>
public class MetadataKeyPublishTests : IAsyncLifetime
{
    private const string Password = "metadataKeyPublish1";
    private const string MarkerKey = "mek_publish_pending";

    private readonly StepALogCapture _log = new();
    private readonly Node _a = new();
    private readonly List<IDisposable> _disposables = [];
    private byte[] _oldDek = [];
    private byte[] _newDek = [];

    public async Task InitializeAsync()
    {
        await _a.InitializeAsync();
        await _a.InitService.InitializeAsync("admin", "NodeA", Password);
        await _a.Session.UnlockAsync(Password);
        _oldDek = _a.Session.GetMasterDek();

        // The network is switched on: generation 1 under the DEK, then the activation (what MetadataActivationService writes).
        var publisher = new MetadataEventPublisher(_a.NodeRepo, _a.EventLogRepo, _a.Clock, _a.Session, _a.EventApplier,
            new SyncTrigger(), _a.ProtocolPolicy);
        var key = await publisher.PublishNewKeyAsync(1);
        await publisher.PublishActivationAsync(Guid.NewGuid(), new MetadataKeyInfo(key.KeyId, key.Generation));
        _a.ProtocolPolicy.SealsMetadata.Should().BeTrue();
    }

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables) d.Dispose();
        CryptographicOperations.ZeroMemory(_oldDek);
        CryptographicOperations.ZeroMemory(_newDek);
        await _a.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheRotationTransaction_CreatesTheNextGenerationAndTheMarker_ForTheInitiatorOnly(bool initiator)
    {
        await RotateAsync(initiator, createNextMetadataKey: true);

        var keys = await KeyRowsAsync(_a);
        if (!initiator)
        {
            keys.Select(k => k.Generation).Should().Equal([1], "a peer creates nothing: it receives the key from the initiator's event");
            (await MarkerAsync(_a)).Should().BeNull();
            return;
        }
        keys.Select(k => k.Generation).Should().Equal(1, 2);
        MetadataKeyCrypto.TryUnwrap(keys[1], [_newDek]).Should().NotBeNull("wrapped under the NEW DEK");
        MetadataKeyCrypto.TryUnwrap(keys[1], [_oldDek]).Should().BeNull("a node that kept only the old DEK cannot open it");
        (await MarkerAsync(_a)).Should().Be(keys[1].KeyId);
        (await EventCountAsync(_a, EventTypes.MetadataKeySet)).Should().Be(1, "only generation 1's event: generation 2 is not logged yet");
    }

    [Fact]
    public async Task ARotationThatCannotCreateItsKey_RollsBack_AndKeepsTheOldDek()
    {
        using (var conn = _a.Factory.CreateConnection())
            await conn.ExecuteAsync("CREATE TRIGGER fail_mek BEFORE INSERT ON tbl_metadata_key WHEN NEW.generation = 2 BEGIN SELECT RAISE(ABORT, 'injected'); END");

        var act = () => RotateAsync(true, createNextMetadataKey: true);

        await act.Should().ThrowAsync<Exception>();
        _a.Session.GetMasterDek().Should().Equal(_oldDek, "no committed state with a new DEK and no new key");
        (await KeyRowsAsync(_a)).Select(k => k.Generation).Should().Equal(1);
        (await MarkerAsync(_a)).Should().BeNull();
    }

    [Fact]
    public async Task ASecondRotation_WhileTheMarkerStands_FailsAndChangesNothing()
    {
        await RotateAsync(true, createNextMetadataKey: true);
        var dekAfterFirst = _a.Session.GetMasterDek();
        var marker = await MarkerAsync(_a);

        var act = () => RotateAsync(true, createNextMetadataKey: true, newEpoch: 3);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not published yet*");
        _a.Session.GetMasterDek().Should().Equal(dekAfterFirst, "the second rotation rolled back");
        (await KeyRowsAsync(_a)).Select(k => k.Generation).Should().Equal(1, 2);
        (await MarkerAsync(_a)).Should().Be(marker, "the first publication is still the one outstanding");
    }

    [Fact]
    public async Task Publishing_AppendsTheEvent_ClearsTheMarker_AsksForTheEarlyBaseline_AndAPeerOpensTheKey()
    {
        await RotateAsync(true, createNextMetadataKey: true);
        var key = (await KeyRowsAsync(_a))[1];
        var service = ServiceFor(_a);
        var published = new List<string>();
        service.Published += id => { published.Add(id); return Task.CompletedTask; };

        (await service.TryPublishAsync()).Should().BeTrue();

        (await MarkerAsync(_a)).Should().BeNull();
        using var conn = _a.Factory.CreateConnection();
        var events = (await conn.QueryAsync<(string EventId, string Payload, long Lamport)>(
            "SELECT event_id, payload, lamport_ts FROM tbl_event WHERE event_type = 'metadata_key_set' ORDER BY sequence_num")).ToList();
        events.Should().HaveCount(2);
        var payload = JsonSerializer.Deserialize<MetadataKeySetPayload>(events[1].Payload)!;
        payload.KeyId.Should().Be(key.KeyId);
        payload.Generation.Should().Be(2);
        payload.DekFingerprint.Should().Be(key.DekFingerprint);
        Convert.FromBase64String(payload.Wrapped).Should().Equal(key.Wrapped);
        var row = await conn.QuerySingleAsync<(string EventId, long Lamport)>(
            "SELECT event_id, lamport_ts FROM tbl_metadata_key WHERE key_id = @id", new { id = key.KeyId });
        row.EventId.Should().BeEquivalentTo(events[1].EventId, "the row carries the event that published it");
        row.Lamport.Should().Be(events[1].Lamport);
        _a.BaselineScheduler.Reasons.Should().Contain(BaselineReasons.KeyRotation);
        published.Should().Equal(key.KeyId);

        // The peer that accepted the rotation: it receives the event, stores the row, and opens it with the new DEK.
        var peer = new Node();
        await peer.InitializeAsync();
        try
        {
            var (pub, seed) = Ed25519Signer.GenerateKeyPair();
            await peer.NodeRepo.CreateAsync(new NodeIdentity
            {
                NodeId = Guid.NewGuid(), DisplayName = "Peer", Ed25519PublicKey = pub, Ed25519PrivateKey = seed,
                Ed25519PrivateKeyV = 0, CreatedAt = DateTime.UtcNow
            });
            var me = (await _a.NodeRepo.GetAsync())!;
            await peer.WhitelistRepo.CreateAsync(new WhitelistEntry
            {
                NodeId = me.NodeId, DisplayName = me.DisplayName, Ed25519PublicKey = me.Ed25519PublicKey, Status = "A",
                IsSuperadmin = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            var served = (await _a.EventLogRepo.GetAfterSequenceAsync(0, 100)).Where(e => e.EventType == EventTypes.MetadataKeySet).ToList();
            foreach (var evt in served) (await peer.ApplyFromAsync(_a, evt)).Should().Be(EventApplyResult.Applied);
            var peerKeys = await KeyRowsAsync(peer);
            peerKeys.Select(k => k.Generation).Should().Equal(1, 2);
            MetadataKeyCrypto.TryUnwrap(peerKeys[1], [_newDek]).Should().NotBeNull();
        }
        finally
        {
            await peer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Authoring_IsRefused_WhileTheMarkerStands_AndResumesSealedWithTheNewKey_AfterThePublication()
    {
        await RotateAsync(true, createNextMetadataKey: true);
        var service = ServiceFor(_a);
        var logger = LoggerFor(_a, service);
        var newKey = (await KeyRowsAsync(_a))[1];

        var refused = () => logger.LogDeleteAsync(Guid.NewGuid());

        var thrown = await refused.Should().ThrowAsync<MetadataKeyPublishPendingException>();
        thrown.Which.Message.Should().NotContain(newKey.KeyId, "no key material or id in the message");
        (await EventCountAsync(_a, EventTypes.Sealed)).Should().Be(0, "nothing was sealed with the previous generation");
        await logger.LogWhitelistRevokeAsync(Guid.NewGuid()); // control events are not sealed and are not held back

        (await service.TryPublishAsync()).Should().BeTrue();
        await logger.LogDeleteAsync(Guid.NewGuid());

        using var conn = _a.Factory.CreateConnection();
        var sealedRows = (await conn.QueryAsync<string>("SELECT payload FROM tbl_event WHERE event_type = 'sealed' ORDER BY sequence_num")).ToList();
        sealedRows.Should().ContainSingle();
        SealedEnvelope.Parse(sealedRows[0]).KeyId.Should().Be(newKey.KeyId, "authoring resumes with the new generation");
    }

    [Fact]
    public async Task ACrash_BetweenTheCommitAndThePublication_ResumesAfterUnlock_BeforeAnyOtherSealedEvent()
    {
        await RotateAsync(true, createNextMetadataKey: true);
        var newKey = (await KeyRowsAsync(_a))[1];

        // The process dies here. A restart: a new session (locked), key ring and service over the same database.
        var restarted = new SharedNode(_a.Factory);
        await restarted.InitializeAsync();
        try
        {
            (await MarkerAsync(restarted)).Should().Be(newKey.KeyId, "the marker survived the restart");
            var service = ServiceFor(restarted);
            var logger = LoggerFor(restarted, service);

            (await service.TryPublishAsync()).Should().BeFalse("locked: nothing can be signed yet");
            (await MarkerAsync(restarted)).Should().Be(newKey.KeyId);

            restarted.Session.UnlockWithDek((byte[])_newDek.Clone()); // the unlock: the new DEK is in the session again
            await ((Func<Task>)(() => logger.LogDeleteAsync(Guid.NewGuid()))).Should().ThrowAsync<MetadataKeyPublishPendingException>(
                "even unlocked, nothing is sealed before the publication");

            (await service.TryPublishAsync()).Should().BeTrue();
            await logger.LogDeleteAsync(Guid.NewGuid());

            using var conn = restarted.Factory.CreateConnection();
            var keySetAt = await conn.ExecuteScalarAsync<long>(
                "SELECT sequence_num FROM tbl_event WHERE event_type = 'metadata_key_set' AND payload LIKE '%' || @id || '%'", new { id = newKey.KeyId });
            var firstSealedAt = await conn.ExecuteScalarAsync<long>("SELECT MIN(sequence_num) FROM tbl_event WHERE event_type = 'sealed'");
            keySetAt.Should().BeLessThan(firstSealedAt, "the event is appended before any other sealed event");
        }
        finally
        {
            await restarted.DisposeAsync();
        }
    }

    [Fact]
    public async Task AFailedPublication_ChangesNothing_AndTheTimerRepeatsTheStepEverySixtySeconds_OnlyWhileUnlocked()
    {
        await RotateAsync(true, createNextMetadataKey: true);
        var newKey = (await KeyRowsAsync(_a))[1];
        var time = new ManualTime();
        var service = ServiceFor(_a, time);
        time.Period.Should().Be(TimeSpan.FromSeconds(60));

        using (var conn = _a.Factory.CreateConnection())
            await conn.ExecuteAsync("CREATE TRIGGER fail_key_event BEFORE INSERT ON tbl_event WHEN NEW.event_type = 'metadata_key_set' BEGIN SELECT RAISE(ABORT, 'injected'); END");
        (await service.TryPublishAsync()).Should().BeFalse();
        (await MarkerAsync(_a)).Should().Be(newKey.KeyId, "the marker stays");
        (await EventCountAsync(_a, EventTypes.MetadataKeySet)).Should().Be(1, "and no half-written event exists");
        using (var conn = _a.Factory.CreateConnection())
            await conn.ExecuteAsync("DROP TRIGGER fail_key_event");

        _a.Session.Lock();
        time.Fire();
        await Task.Delay(300);
        (await MarkerAsync(_a)).Should().Be(newKey.KeyId, "a locked node does not try");

        _a.Session.UnlockWithDek((byte[])_newDek.Clone());
        time.Fire();
        await WaitUntilAsync(async () => await MarkerAsync(_a) is null);
        (await EventCountAsync(_a, EventTypes.MetadataKeySet)).Should().Be(2);
    }

    [Fact]
    public async Task ANotificationThatFailsAfterTheCommit_DoesNotLoseThePublication()
    {
        await RotateAsync(true, createNextMetadataKey: true);
        var key = (await KeyRowsAsync(_a))[1];
        // Past the commit the marker is gone, so nothing would ever retry: the waking of the sync loop and the early baseline are
        // best effort, and the publication still counts as done and is announced to whoever waits for it.
        var service = ServiceFor(_a, trigger: new ThrowingTrigger(), scheduler: new ThrowingScheduler());
        var published = new List<string>();
        service.Published += id => { published.Add(id); return Task.CompletedTask; };

        (await service.TryPublishAsync()).Should().BeTrue();

        (await MarkerAsync(_a)).Should().BeNull();
        (await EventCountAsync(_a, EventTypes.MetadataKeySet)).Should().Be(2);
        published.Should().Equal([key.KeyId], "the rotation that waited for the publication is told");
    }

    [Fact]
    public async Task TheRotationTransaction_WritesTheCompletionRecord_AndTheRollbackTakesItAway()
    {
        var record = new DekRotationCompletionRecord("commit-1", 2, 7, "Admin", "pre-rotation.tar.gz", 0);
        await RotateAsync(true, createNextMetadataKey: true, completionRecord: record);

        DekRotationCompletionRecord.Read(_a.Factory).Should().Be(record, "what is left to finish is committed together with the new DEK");

        // A rotation whose transaction fails after the record was written: no new DEK and no record. The state row of the commit
        // is the last thing the transaction updates; a trigger makes that fail.
        using (var conn = _a.Factory.CreateConnection())
            await conn.ExecuteAsync(@"DELETE FROM tbl_migration_marker WHERE key IN (@Key, @MarkerKey)",
                new { Key = DekRotationCompletionRecord.Key, MarkerKey });
        using (var conn = _a.Factory.CreateConnection())
        {
            await conn.ExecuteAsync("INSERT INTO tbl_dek_rotation_state (event_id, state, rotation_ts, created_at, updated_at) VALUES ('commit-2', 'COMMITTING', 'now', 'now', 'now')");
            await conn.ExecuteAsync("CREATE TRIGGER fail_state BEFORE UPDATE ON tbl_dek_rotation_state BEGIN SELECT RAISE(ABORT, 'injected'); END");
        }
        var dekBefore = _a.Session.GetMasterDek();

        var act = () => RotateAsync(true, createNextMetadataKey: false, newEpoch: 3, completionRecord: record with { CommitEventId = "commit-2" }, commitEventId: "commit-2");

        await act.Should().ThrowAsync<Exception>();
        _a.Session.GetMasterDek().Should().Equal(dekBefore);
        DekRotationCompletionRecord.Read(_a.Factory).Should().BeNull("the rotation rolled back and took its record with it");
    }

    [Fact]
    public async Task ASecondRotation_WhileACompletionRecordStands_FailsAndChangesNothing()
    {
        var record = new DekRotationCompletionRecord("commit-1", 2, 7, "Admin", "pre-rotation.tar.gz", 0);
        await RotateAsync(true, createNextMetadataKey: false, completionRecord: record);
        var dekAfterFirst = _a.Session.GetMasterDek();

        var act = () => RotateAsync(true, createNextMetadataKey: false, newEpoch: 3, completionRecord: record with { CommitEventId = "commit-2" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not finished yet*");
        _a.Session.GetMasterDek().Should().Equal(dekAfterFirst, "the second rotation rolled back: the first one has its unfinished work on record, and keeps it");
        DekRotationCompletionRecord.Read(_a.Factory).Should().Be(record);
    }

    [Fact]
    public async Task BeginAuthoring_IsRefusedWhileTheMarkerStands_AndHoldsNothingBack()
    {
        await RotateAsync(true, createNextMetadataKey: true);
        var barrier = new AuthoringBarrier();
        var logger = LoggerFor(_a, ServiceFor(_a), barrier);

        var refused = async () => (await logger.BeginAuthoringAsync()).Dispose();

        await refused.Should().ThrowAsync<MetadataKeyPublishPendingException>("before the caller changed anything");
        (await barrier.EnterExclusiveAsync(TimeSpan.FromSeconds(2))).Dispose(); // the refusal left no scope behind
    }

    [Fact]
    public async Task TheDekSwap_WaitsForAnAuthorInFlight_AndWhatItSealedIsLoggedUnderTheKeyItWasSealedWith()
    {
        var barrier = new AuthoringBarrier();
        var service = ServiceFor(_a);
        var logger = LoggerFor(_a, service, barrier);
        var generation1 = _a.KeyRing.Current!.KeyId;
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // An authoring request that passed the check and is suspended before its event is appended.
        var author = Task.Run(async () =>
        {
            using var authoring = await logger.BeginAuthoringAsync();
            inside.SetResult();
            await resume.Task;
            await logger.LogDeleteAsync(Guid.NewGuid());
        });
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The rotation: the barrier first, then the DEK transaction, as DekRotationService runs it.
        var rotation = Task.Run(async () =>
        {
            using var exclusive = await barrier.EnterExclusiveAsync();
            await RotateAsync(true, createNextMetadataKey: true);
        });
        await Task.Delay(400);
        rotation.IsCompleted.Should().BeFalse("the swap waits for the author in flight");
        _a.Session.GetMasterDek().Should().Equal(_oldDek);
        (await EventCountAsync(_a, EventTypes.Sealed)).Should().Be(0);

        resume.SetResult();
        await author.WaitAsync(TimeSpan.FromSeconds(10));
        await rotation.WaitAsync(TimeSpan.FromSeconds(30));

        using var conn = _a.Factory.CreateConnection();
        var sealedRows = (await conn.QueryAsync<string>("SELECT payload FROM tbl_event WHERE event_type = 'sealed'")).ToList();
        sealedRows.Should().ContainSingle();
        SealedEnvelope.Parse(sealedRows[0]).KeyId.Should().Be(generation1, "appended before the swap, under the key it was sealed with");
        _a.Session.GetMasterDek().Should().Equal(_newDek);
        await ((Func<Task>)(async () => (await logger.BeginAuthoringAsync()).Dispose())).Should().ThrowAsync<MetadataKeyPublishPendingException>(
            "after the swap nothing is written until the new key is published");
    }

    [Fact]
    public async Task AMarkerWhoseKeyIsGone_IsForgotten_AndBlocksNothing()
    {
        using (var conn = _a.Factory.CreateConnection())
            await conn.ExecuteAsync("INSERT INTO tbl_migration_marker (key, value, set_at) VALUES (@MarkerKey, 'ffffffffffffffffffffffffffffffff', 'now')", new { MarkerKey });
        var service = ServiceFor(_a);
        service.IsPending.Should().BeTrue();

        (await service.TryPublishAsync()).Should().BeTrue();

        service.IsPending.Should().BeFalse();
        (await EventCountAsync(_a, EventTypes.MetadataKeySet)).Should().Be(1, "nothing was published for a key that is not there");
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    /// <summary>The DEK transaction of a rotation, as the server runs it (the initiator's slot rewrap is the test's own business).</summary>
    private async Task RotateAsync(bool initiator, bool createNextMetadataKey, int newEpoch = 2, DekRotationCompletionRecord? completionRecord = null, string? commitEventId = null)
    {
        var oldDek = _a.Session.GetMasterDek();
        if (_newDek.Length == 0) _newDek = RandomNumberGenerator.GetBytes(32);
        var newDek = (byte[])_newDek.Clone();
        if (newEpoch != 2) newDek = RandomNumberGenerator.GetBytes(32);

        int? slotId = null;
        byte[]? slotDek = null, slotIv = null;
        if (initiator)
        {
            using var conn = _a.Factory.CreateConnection();
            slotId = await conn.ExecuteScalarAsync<int>("SELECT slot_id FROM tbl_key_slot LIMIT 1");
            slotDek = await conn.ExecuteScalarAsync<byte[]>("SELECT encrypted_master_dek FROM tbl_key_slot WHERE slot_id = @slotId", new { slotId });
            slotIv = await conn.ExecuteScalarAsync<byte[]>("SELECT iv FROM tbl_key_slot WHERE slot_id = @slotId", new { slotId });
        }

        await DekRewrapper.RewrapAllAsync(
            _a.Factory, _a.Session, oldDek, newDek, newEpoch, commitEventId ?? Guid.NewGuid().ToString(), initiator, slotId, slotDek, slotIv,
            createNextMetadataKey: createNextMetadataKey, completionRecord: completionRecord);
    }

    private MetadataKeyPublishService ServiceFor(SyncTestFixture node, TimeProvider? time = null, ISyncTrigger? trigger = null, IBaselineScheduler? scheduler = null)
    {
        var provider = new ServiceCollection()
            .AddSingleton(node.NodeRepo)
            .AddSingleton<ILamportClock>(node.Clock)
            .AddSingleton<ISyncProtocolPolicy>(node.ProtocolPolicy)
            .AddSingleton(node.EventLogRepo)
            .AddSingleton(trigger ?? new SyncTrigger())
            .AddSingleton(scheduler ?? node.BaselineScheduler)
            .BuildServiceProvider();
        _disposables.Add(provider);
        var service = new MetadataKeyPublishService(provider.GetRequiredService<IServiceScopeFactory>(), node.Session, node.Factory,
            _log.For<MetadataKeyPublishService>(), time);
        _disposables.Add(service);
        return service;
    }

    private static EventLogger LoggerFor(SyncTestFixture node, IMetadataKeyPublishGate gate, AuthoringBarrier? barrier = null) =>
        new(node.NodeRepo, node.EventLogRepo, node.Clock, new NullActorProvider(), new SyncTrigger(), node.Session,
            new BlobRepository(node.Factory), node.ProtocolPolicy, node.KeyRing, gate, barrier);

    private static async Task<List<MetadataKeyRow>> KeyRowsAsync(SyncTestFixture node)
    {
        using var conn = node.Factory.CreateConnection();
        return (await conn.QueryAsync<(string KeyId, long Generation, string Fp, byte[] Wrapped, byte[] Iv)>(
                "SELECT key_id, generation, dek_fingerprint, wrapped, iv FROM tbl_metadata_key ORDER BY generation"))
            .Select(r => new MetadataKeyRow(r.KeyId, (int)r.Generation, r.Fp, r.Wrapped, r.Iv)).ToList();
    }

    private static async Task<string?> MarkerAsync(SyncTestFixture node)
    {
        using var conn = node.Factory.CreateConnection();
        return await conn.ExecuteScalarAsync<string?>("SELECT value FROM tbl_migration_marker WHERE key = @MarkerKey", new { MarkerKey });
    }

    private static async Task<long> EventCountAsync(SyncTestFixture node, string type)
    {
        using var conn = node.Factory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event WHERE event_type = @type", new { type });
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
    }

    private sealed class ThrowingTrigger : ISyncTrigger
    {
        public void Signal() => throw new InvalidOperationException("injected: the sync trigger is gone");
        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class ThrowingScheduler : IBaselineScheduler
    {
        public void RequestEarly(string reasonCode) => throw new InvalidOperationException("injected: the baseline scheduler is gone");
    }

    private sealed class Node : SyncTestFixture;

    private sealed class SharedNode(DbConnectionFactory shared) : SyncTestFixture
    {
        protected override DbConnectionFactory CreateFactory() => shared;
        protected override bool OwnsFactory => false;
    }

    /// <summary>A clock that never ticks by itself: the test fires the one timer a service creates.</summary>
    private sealed class ManualTime : TimeProvider
    {
        public TimeSpan? Period { get; private set; }
        private TimerCallback? _callback;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            Period = period;
            return new NoTimer();
        }

        public void Fire() => _callback!(null);

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
