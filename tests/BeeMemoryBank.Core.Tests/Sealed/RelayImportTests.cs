using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// The import of what a closed node received (docs/design/step-b-spec.md 6.6, red tests 15.2.9 and 15.2.10) and the sequence floor of 6.3: every
/// row is applied once and in relay order, a permanent failure ends a row and a deferrable one stops the import with the order kept, blobs come
/// first, reports are merged by the rule of report-position, and a crash at any point leaves nothing applied twice.
/// </summary>
public class RelayImportTests
{
    private sealed class DeferrableFailure : Exception, IDeferrableSyncFailure;

    private static readonly Guid Peer = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ---- order and exactly once ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryRowIsAppliedOnce_InRelayOrder_WithItsPusherAndSequence_AndTheInboxIsEmptyAfterwards()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        var events = Enumerable.Range(0, 3).Select(i => { var e = RelayImportFixture.NewEvent(); e.SequenceNum = 40 + i; return e; }).ToArray();
        await f.ReceiveAsync(Peer, events);

        var result = await f.Importer.ImportAsync();

        result.Should().Be(new RelayImportResult(Applied: 3, Held: 0, AlreadyPresent: 0, Refused: 0, Waiting: 0));
        f.Applier.Calls.Select(c => c.EventId).Should().Equal(events.Select(e => e.EventId), "the order of the inbox is the order of the apply");
        f.Applier.Calls.Select(c => c.Context.ReceivedFrom).Should().OnlyContain(p => p == Peer);
        f.Applier.Calls.Select(c => c.Context.ReceivedSeq).Should().Equal(40L, 41L, 42L);
        f.Applier.Calls.Should().OnlyContain(c => c.Context.Originators == null, "the whitelist lookup runs on the sealed whitelist as it stands (6.6 step 2)");
        (await f.LogIdsAsync()).Should().Equal(events.Select(e => e.EventId));
        (await f.Inbox.GetSummaryAsync()).Events.Should().Be(0);
        f.Mirror.Rewrites.Should().Be(1, "the whitelist mirror becomes the sealed whitelist when the import is done (6.6 step 4)");
    }

    [Fact]
    public async Task ASecondImportOfAnEmptyInbox_AppliesNothing_AndStillRewritesTheMirror()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        await f.ReceiveAsync(Peer, RelayImportFixture.NewEvent());
        await f.Importer.ImportAsync();

        var again = await f.Importer.ImportAsync();

        again.Should().Be(new RelayImportResult(0, 0, 0, 0, 0));
        f.Applier.Calls.Should().HaveCount(1);
        f.Mirror.Rewrites.Should().Be(2);
    }

    [Fact]
    public async Task OutcomesAreCounted_HeldAndDroppedRowsLeaveTheInbox_AndARowTheLogAlreadyHoldsIsNotAppliedAgain()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        var held = RelayImportFixture.NewEvent();
        var dropped = RelayImportFixture.NewEvent();
        var already = RelayImportFixture.NewEvent();
        var normal = RelayImportFixture.NewEvent();
        await f.ReceiveAsync(Peer, held, dropped, already, normal);
        await f.Applier.Store(already);
        f.Applier.Script = async (e, _) =>
        {
            if (e.EventId == held.EventId) { await f.Applier.Store(e); return EventApplyResult.Held; }
            if (e.EventId == dropped.EventId) return EventApplyResult.SilentlyDropped;
            await f.Applier.Store(e);
            return EventApplyResult.Applied;
        };

        var result = await f.Importer.ImportAsync();

        result.Should().Be(new RelayImportResult(Applied: 1, Held: 1, AlreadyPresent: 1, Refused: 1, Waiting: 0));
        f.Applier.Calls.Select(c => c.EventId).Should().NotContain(already.EventId);
        (await f.Inbox.GetSummaryAsync()).Events.Should().Be(0);
    }

    // ---- failures ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APermanentFailure_IsRecordedInTheQuarantine_AndEndsTheRow_TheLaterRowsAreStillApplied()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        var a = RelayImportFixture.NewEvent();
        var bad = RelayImportFixture.NewEvent();
        var c = RelayImportFixture.NewEvent();
        await f.ReceiveAsync(Peer, a, bad, c);
        f.Applier.Script = async (e, _) =>
        {
            if (e.EventId == bad.EventId) throw new InvalidDataException("Invalid Ed25519 signature");
            await f.Applier.Store(e);
            return EventApplyResult.Applied;
        };

        var result = await f.Importer.ImportAsync();

        result.Should().Be(new RelayImportResult(Applied: 2, Held: 0, AlreadyPresent: 0, Refused: 1, Waiting: 0));
        (await f.Quarantine.GetAllAsync()).Should().ContainSingle(q => q.EventId == bad.EventId && q.LastFailureKind == SyncFailureKind.Permanent);
        (await f.LogIdsAsync()).Should().Equal(a.EventId, c.EventId);
        (await f.Inbox.GetSummaryAsync()).Events.Should().Be(0);
    }

    [Fact]
    public async Task ADeferrableFailure_StopsAtTheRow_KeepsTheOrder_AndTheRetryImportsItBeforeAnyLaterRow()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        var first = RelayImportFixture.NewEvent();
        var waiting = RelayImportFixture.NewEvent();
        var after = RelayImportFixture.NewEvent();
        await f.ReceiveAsync(Peer, first, waiting, after);
        var blocked = true;
        f.Applier.Script = async (e, _) =>
        {
            if (e.EventId == waiting.EventId && blocked) throw new DeferrableFailure();
            await f.Applier.Store(e);
            return EventApplyResult.Applied;
        };

        var result = await f.Importer.ImportAsync();

        result.Should().Be(new RelayImportResult(Applied: 1, Held: 0, AlreadyPresent: 0, Refused: 0, Waiting: 2));
        f.Applier.Calls.Select(c => c.EventId).Should().Equal(first.EventId, waiting.EventId).And.NotContain(after.EventId, "the order is kept: nothing after the stopped row is applied");

        // A row that arrives meanwhile goes behind the stopped ones.
        var later = RelayImportFixture.NewEvent();
        await f.ReceiveAsync(Peer, later);
        blocked = false;
        var retry = await f.Importer.ImportAsync();

        retry.Should().Be(new RelayImportResult(Applied: 3, Held: 0, AlreadyPresent: 0, Refused: 0, Waiting: 0));
        (await f.LogIdsAsync()).Should().Equal(first.EventId, waiting.EventId, after.EventId, later.EventId);
    }

    [Fact]
    public async Task AFailureOfTheStorageUnderTheApply_IsNotTheEventsFault_TheRowStays()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        var evt = RelayImportFixture.NewEvent();
        await f.ReceiveAsync(Peer, evt);
        f.Applier.Script = (_, _) => throw new SqliteException("database is locked", 5);

        var result = await f.Importer.ImportAsync();

        result.Should().Be(new RelayImportResult(0, 0, 0, 0, Waiting: 1));
        (await f.Inbox.ReadAfterAsync(0, 10)).Should().ContainSingle(r => r.Event.EventId == evt.EventId);
    }

    [Fact]
    public async Task ADatabaseThatClosesMeanwhile_StopsTheImport_AndEveryRowStaysInTheInbox()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        var events = Enumerable.Range(0, 3).Select(_ => RelayImportFixture.NewEvent()).ToArray();
        await f.ReceiveAsync(Peer, events);
        f.Applier.Script = async (e, _) =>
        {
            await f.Applier.Store(e);
            f.Gate.State = VaultState.Closed;
            return EventApplyResult.Applied;
        };

        var result = await f.Importer.ImportAsync();

        result.Applied.Should().Be(1);
        result.Waiting.Should().Be(2);
        f.Applier.Calls.Should().HaveCount(1, "a closed database is not read or written to");
    }

    [Fact]
    public async Task AVaultSealedException_FromTheApply_KeepsTheRow_WithoutAQuarantineRecord()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        var evt = RelayImportFixture.NewEvent();
        await f.ReceiveAsync(Peer, evt);
        f.Applier.Script = (_, _) => throw new VaultSealedException();

        var result = await f.Importer.ImportAsync();

        result.Waiting.Should().Be(1);
        (await f.Quarantine.GetAllAsync()).Should().BeEmpty();
    }

    // ---- blobs and reports -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BlobsGoFirst_SoAnEventFindsItsBlob_AndLeaveTheInbox()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var hash = BlobHash.Compute(data);
        await f.Inbox.StoreBlobAsync(hash, data);
        await f.ReceiveAsync(Peer, RelayImportFixture.NewEvent());
        bool? blobWasThere = null;
        f.Applier.Script = async (e, _) =>
        {
            blobWasThere = await f.Blobs.GetAsync(hash) is not null;
            await f.Applier.Store(e);
            return EventApplyResult.Applied;
        };

        await f.Importer.ImportAsync();

        blobWasThere.Should().BeTrue();
        (await f.Inbox.GetSummaryAsync()).Blobs.Should().Be(0);
        (await f.Blobs.GetAsync(hash)).Should().Equal(data);
    }

    [Fact]
    public async Task ReportsAreMerged_ByTheRuleOfReportPosition_AndCleared()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        var inRange = Guid.NewGuid();
        var aboveHead = Guid.NewGuid();
        var belowRecorded = Guid.NewGuid();
        await f.ReceiveAsync(Peer, RelayImportFixture.NewEvent(), RelayImportFixture.NewEvent());
        await f.Positions.RecordReportedPositionAsync(belowRecorded, 2);
        await f.Inbox.RecordReportAsync(inRange, 1);
        await f.Inbox.RecordReportAsync(aboveHead, 99);
        await f.Inbox.RecordReportAsync(belowRecorded, 1);

        await f.Importer.ImportAsync();

        (await f.Positions.GetAsync(inRange))!.ReportedSeq.Should().Be(1);
        (await f.Positions.GetAsync(aboveHead)).Should().BeNull("a position above the head is not one the peer can have read");
        (await f.Positions.GetAsync(belowRecorded))!.ReportedSeq.Should().Be(2, "a report never moves a recorded position backwards");
        (await f.Inbox.ListReportsAsync()).Should().BeEmpty();
    }

    // ---- 15.2.10 crash during import --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACrashDuringTheImport_RestartApplyingEveryEventExactlyOnce()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        var events = Enumerable.Range(0, 5).Select(_ => RelayImportFixture.NewEvent()).ToArray();
        await f.ReceiveAsync(Peer, events);
        // The process dies after the third event is in the log and before its row left the inbox: the worst place.
        f.Applier.Script = async (e, _) =>
        {
            await f.Applier.Store(e);
            if (e.EventId == events[2].EventId) throw new OperationCanceledException("the process was killed");
            return EventApplyResult.Applied;
        };
        await FluentActions.Awaiting(() => f.Importer.ImportAsync()).Should().ThrowAsync<OperationCanceledException>();
        (await f.LogIdsAsync()).Should().Equal(events.Take(3).Select(e => e.EventId));
        (await f.Inbox.ReadAfterAsync(0, 10)).Select(r => r.Event.EventId).Should().Equal(events.Skip(2).Select(e => e.EventId));

        // Restart: a new importer over the same files, the node unlocks, the gate opens and imports.
        f.Applier.Script = null;
        f.Applier.Calls.Clear();
        var restarted = f.NewImporter();
        await restarted.OnOpenedAsync();
        var result = await restarted.ImportAsync();

        result.Should().Be(new RelayImportResult(Applied: 2, Held: 0, AlreadyPresent: 1, Refused: 0, Waiting: 0));
        (await f.LogIdsAsync()).Should().Equal(events.Select(e => e.EventId), "every event once, in order");
        f.Applier.Calls.Select(c => c.EventId).Should().Equal(events.Skip(3).Select(e => e.EventId), "the one already in the log is not applied again");
        (await f.Inbox.GetSummaryAsync()).Events.Should().Be(0);
    }

    [Fact]
    public async Task ACrashInTheLastPass_DoesNotLeaveTheRelayLocked()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        await f.ReceiveAsync(Peer, RelayImportFixture.NewEvent());
        f.Mirror.FailWith = new OperationCanceledException("killed in the last pass, under the relay's lock");

        await FluentActions.Awaiting(() => f.Importer.ImportAsync()).Should().ThrowAsync<OperationCanceledException>();

        using var receipt = await f.Lock.TryEnterReceiptAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        receipt.Should().NotBeNull("the exclusive hold of the last pass is released when the pass fails");
    }

    [Fact]
    public async Task AMirrorThatCannotBeRewritten_DoesNotFailTheImport()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        await f.ReceiveAsync(Peer, RelayImportFixture.NewEvent());
        f.Mirror.FailWith = new IOException("disk full");

        var result = await f.Importer.ImportAsync();

        result.Applied.Should().Be(1, "the rewrite is best effort (4.3 rule 3); the watcher and the close do it again");
    }

    // ---- 6.3 sequence numbers ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AtOpen_TheSequenceIsRaisedAboveEveryRelaySequenceServed_AndTheFloorIsSet()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        await f.Boot.SetMetaAsync(BootMetaKeys.SeqFloor, "5000");
        await f.Boot.SetMetaAsync(BootMetaKeys.CleanClose, "0");
        var served = await f.ReceiveAsync(Peer, RelayImportFixture.NewEvent(), RelayImportFixture.NewEvent(), RelayImportFixture.NewEvent());
        var highest = served.Max(s => s.Relay);
        highest.Should().Be(5002);

        await f.Importer.OnOpenedAsync();

        using (var c = f.Vault.CreateConnection())
            c.ExecuteScalar<long>("SELECT seq FROM sqlite_sequence WHERE name = 'tbl_event'").Should().Be(highest);
        (await f.MetaAsync(BootMetaKeys.SeqFloor)).Should().Be(highest + 1 + 1_048_576);
        (await f.Boot.GetMetaAsync(BootMetaKeys.CleanClose)).Should().Be("0");

        // Whatever is appended from now on, imported or authored, is numbered above every row a peer has seen.
        var result = await f.Importer.ImportAsync();
        result.Applied.Should().Be(3);
        using var check = f.Vault.CreateConnection();
        check.Query<long>("SELECT sequence_num FROM tbl_event ORDER BY sequence_num").Should().OnlyContain(s => s > highest);
    }

    [Fact]
    public async Task AtOpen_AHigherSequenceThatTheLogAlreadyHas_IsNotLowered()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        using (var c = f.Vault.CreateConnection())
            c.Execute("INSERT INTO sqlite_sequence (name, seq) VALUES ('tbl_event', 9000)");
        await f.ReceiveAsync(Peer, RelayImportFixture.NewEvent());

        await f.Importer.OnOpenedAsync();

        using var check = f.Vault.CreateConnection();
        check.ExecuteScalar<long>("SELECT seq FROM sqlite_sequence WHERE name = 'tbl_event'").Should().Be(9000);
        (await f.MetaAsync(BootMetaKeys.SeqFloor)).Should().Be(9000 + 1 + 1_048_576);
    }

    [Fact]
    public async Task AtACleanClose_TheHeadAndTheFloorAreRecorded_AboveEveryNumberEverGiven()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        var a = RelayImportFixture.NewEvent();
        var b = RelayImportFixture.NewEvent();
        await f.Applier.Store(a);
        await f.Applier.Store(b);
        using (var c = f.Vault.CreateConnection())
            c.Execute("DELETE FROM tbl_event WHERE sequence_num = 2"); // compaction took the newest row

        await f.Importer.OnClosingAsync();

        (await f.MetaAsync(BootMetaKeys.SealedHead)).Should().Be(1, "the head is the highest sequence_num the log holds");
        (await f.MetaAsync(BootMetaKeys.SeqFloor)).Should().Be(3, "the floor is above the highest number ever assigned (sqlite_sequence says 2)");
        (await f.Boot.GetMetaAsync(BootMetaKeys.CleanClose)).Should().Be("1");
    }

    [Fact]
    public async Task TheFloorTimer_RaisesTheFloorOnlyWhenTheLogComesWithin524288OfIt()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        await f.Importer.OnOpenedAsync();
        var floor = await f.MetaAsync(BootMetaKeys.SeqFloor);
        (await f.Importer.RaiseFloorIfNeededAsync()).Should().BeFalse("nothing has moved");

        using (var c = f.Vault.CreateConnection())
            c.Execute(@"INSERT INTO tbl_event (sequence_num, event_id, node_id, lamport_ts, event_type, payload, signature, protocol_version, created_at)
                        VALUES (@seq, @id, @node, 1, 'whitelist_add', '{}', x'00', 4, @now)",
                new { seq = floor - 524_288 - 1, id = Guid.NewGuid().ToString("D"), node = Guid.NewGuid().ToString("D"), now = DateTime.UtcNow.ToString("o") });
        (await f.Importer.RaiseFloorIfNeededAsync()).Should().BeFalse("still a margin of 524,288");

        using (var c = f.Vault.CreateConnection())
            c.Execute("UPDATE tbl_event SET sequence_num = @seq", new { seq = floor - 524_288 + 1 });
        (await f.Importer.RaiseFloorIfNeededAsync()).Should().BeTrue();
        (await f.MetaAsync(BootMetaKeys.SeqFloor)).Should().Be(floor - 524_288 + 1 + 1 + 1_048_576);
        (await f.Boot.GetMetaAsync(BootMetaKeys.CleanClose)).Should().Be("0", "the timer never claims a clean close");
    }

    // ---- the timers ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMaintenanceTick_ImportsWhatIsLeft_OnlyWhileTheDatabaseIsOpen()
    {
        await using var f = await RelayImportFixture.CreateAsync(VaultState.Closed);
        var evt = RelayImportFixture.NewEvent();
        await f.ReceiveAsync(Peer, evt);
        var maintenance = new RelayMaintenance(f.Importer, f.Inbox, f.Gate, importEvery: 1);

        await maintenance.TickAsync();
        f.Applier.Calls.Should().BeEmpty("a closed database is never read");

        f.Gate.State = VaultState.Open;
        await maintenance.TickAsync();
        f.Applier.Calls.Select(c => c.EventId).Should().Equal(evt.EventId);
        (await f.Inbox.GetSummaryAsync()).Events.Should().Be(0);

        var rewrites = f.Mirror.Rewrites;
        await maintenance.TickAsync();
        f.Mirror.Rewrites.Should().Be(rewrites, "an empty inbox is not imported again");
    }

    // ---- 6.8 pause -----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(VaultState.Open)]
    [InlineData(VaultState.Opening)]
    [InlineData(VaultState.Importing)]
    [InlineData(VaultState.Closing)]
    [InlineData(VaultState.Plain)]
    public async Task PauseWrites_IsGrantedOnlyWhileTheGateIsClosed(VaultState state)
    {
        await using var f = await RelayImportFixture.CreateAsync(state);

        await FluentActions.Awaiting(() => f.Importer.PauseWritesAsync()).Should().ThrowAsync<InvalidOperationException>();

        f.Gate.State = VaultState.Closed;
        await using var lease = await f.Importer.PauseWritesAsync();
        f.Lock.IsPaused.Should().BeTrue();
    }

    [Fact]
    public async Task PauseWrites_WaitsForAReceiptInFlight_AndDisposingTheLeaseLiftsIt()
    {
        await using var f = await RelayImportFixture.CreateAsync(VaultState.Closed);
        var receipt = await f.Lock.TryEnterReceiptAsync();
        receipt.Should().NotBeNull();

        var pause = f.Importer.PauseWritesAsync();
        await Task.Delay(200);
        pause.IsCompleted.Should().BeFalse("the pause waits for the receipt in flight");
        (await f.Lock.TryEnterReceiptAsync()).Should().BeNull("a receipt that starts now is told to come back");

        receipt!.Dispose();
        var lease = await pause.WaitAsync(TimeSpan.FromSeconds(5));
        f.Lock.IsPaused.Should().BeTrue();

        await lease.DisposeAsync();
        f.Lock.IsPaused.Should().BeFalse();
        using var again = await f.Lock.TryEnterReceiptAsync();
        again.Should().NotBeNull();
    }

    [Fact]
    public async Task TheLastPassOfTheImport_MakesAReceiptWait_NotRefuse()
    {
        await using var f = await RelayImportFixture.CreateAsync();
        using var exclusive = await f.Lock.EnterExclusiveAsync();

        var receipt = f.Lock.TryEnterReceiptAsync();
        await Task.Delay(200);
        receipt.IsCompleted.Should().BeFalse("a push that arrives during the last pass waits and lands after it (6.6 step 5)");

        exclusive.Dispose();
        using var lease = await receipt.WaitAsync(TimeSpan.FromSeconds(5));
        lease.Should().NotBeNull();
    }
}
