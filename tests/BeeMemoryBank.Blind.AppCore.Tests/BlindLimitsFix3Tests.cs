using System.Net;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Hardening of the limits, part 3, the tests that need nothing but what part 2 had: the counting stream owns the release of what an answer set aside; after the admission is closed it is closed; a storage total that is
/// fresh after every batch and counts every listed account; usage state that stays unavailable until a repair or a save is really on disk; and the evidence that repair keeps.
/// (A partial of the round-2 class: it uses that class's pipeline rig.)
/// </summary>
public sealed partial class BlindLimitsFix2Tests
{
    /// <summary>A body that never answers a read until it is cancelled, as a socket does when the network goes quiet.</summary>
    private sealed class QuietStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that answers zero once (the end) and then, if asked again, has more: an unusual inner stream that must not be able to move a closed request's counters.</summary>
    private sealed class AwakeningStream(int first) : Stream
    {
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            _reads++;
            var n = _reads switch { 1 => Math.Min(first, buffer.Length), 2 => 0, _ => Math.Min(5000, buffer.Length) };
            buffer[..n].Clear();
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- 7. the stream owns the release ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingOnlyTheStream_GivesBackWhatTheAnswerSetAside_WhileTheResponseIsKept(bool async)
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(300 * 1024, 300 * 1024)));
        rig.Policy.Current = Limited(stored: 1000 * 1024, traffic: 8 << 20);
        using var client = rig.Client;
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        (await stream.ReadAsync(new byte[10 * 1024])).Should().Be(10 * 1024);
        rig.Stored.Peek().Bytes.Should().Be(300 * 1024, "all of it is set aside while the answer is open");

        if (async) await stream.DisposeAsync(); else stream.Dispose();

        rig.Stored.Peek().Bytes.Should().Be(10 * 1024, "the stream was closed: only what was read is held, although the response is still in the caller's hands");
        rig.Ledger.ReservedTraffic.Should().Be(0);
    }

    [Fact]
    public async Task AContentKeptAfterItsStreamWasClosed_HoldsNothing_AndDisposingItLaterChangesNothing()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(300 * 1024, 300 * 1024)));
        rig.Policy.Current = Limited(stored: 1000 * 1024);
        using var client = rig.Client;
        var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
        var content = response.Content;
        await using (var stream = await content.ReadAsStreamAsync())
        {
            var got = await stream.ReadAsync(new byte[1000]);
            got.Should().BeGreaterThan(0, "the stream was read from before it was closed");
        }
        var heldAfterClose = rig.Stored.Peek().Bytes;
        var trafficAfterClose = rig.Ledger.UsageToday().Traffic;

        content.Dispose();
        response.Dispose();
        response.Dispose();

        heldAfterClose.Should().Be(1000);
        rig.Stored.Peek().Bytes.Should().Be(heldAfterClose, "a second release gives nothing back that was already given back");
        rig.Ledger.UsageToday().Traffic.Should().Be(trafficAfterClose);
        rig.Ledger.ReservedTraffic.Should().Be(0);
    }

    [Fact]
    public async Task ARepeatedReadAfterTheEnd_AnswersZero_AndChangesNothing_EvenOverAnInnerStreamThatHasMore()
    {
        using var rig = NewRig((request, ct) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new AwakeningStream(2000)) };
            return Task.FromResult(response);
        });
        rig.Policy.Current = Limited(stored: 1000 * 1024, traffic: 8 << 20);
        using var client = rig.Client;
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[10 * 1024];
        (await stream.ReadAsync(buffer)).Should().Be(2000);
        (await stream.ReadAsync(buffer)).Should().Be(0, "the end");
        var held = rig.Stored.Peek().Bytes;
        var traffic = rig.Ledger.UsageToday().Traffic;

        for (var i = 0; i < 3; i++) (await stream.ReadAsync(buffer)).Should().Be(0, "after the end the stream says so and does not go back to the inner stream");

        rig.Stored.Peek().Bytes.Should().Be(held);
        rig.Ledger.UsageToday().Traffic.Should().Be(traffic);
        rig.Ledger.ReservedTraffic.Should().Be(0);
    }

    [Fact]
    public async Task AReadAfterTheStreamWasDisposed_IsRefused()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(300 * 1024, 300 * 1024)));
        using var client = rig.Client;
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        await stream.DisposeAsync();

        var act = async () => await stream.ReadAsync(new byte[10]);

        await act.Should().ThrowAsync<ObjectDisposedException>();
        rig.Ledger.ReservedTraffic.Should().Be(0);
    }

    [Fact]
    public async Task AReadThatIsCancelled_GivesItsOwnReservationBack_AndTheStreamsDisposalGivesTheRest()
    {
        using var rig = NewRig((request, ct) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new QuietStream(300 * 1024)) };
            response.Content.Headers.ContentLength = 300 * 1024;
            return Task.FromResult(response);
        });
        rig.Policy.Current = Limited(stored: 1000 * 1024, traffic: 8 << 20);
        using var client = rig.Client;
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        using var cancel = new CancellationTokenSource();

        var read = stream.ReadAsync(new byte[64 * 1024], cancel.Token).AsTask();
        rig.Ledger.ReservedTraffic.Should().BeGreaterThan(64 * 1023, "the read set its room aside");
        await cancel.CancelAsync();
        var act = () => read;

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Ledger.ReservedTraffic.Should().Be(0, "no read is under way and the answer's headers were spent: nothing is held for the read");
        rig.Stored.Peek().Bytes.Should().Be(300 * 1024, "the announced length is still set aside, because the answer is still open");

        await stream.DisposeAsync();

        rig.Stored.Peek().Bytes.Should().Be(0, "nothing was read: closing the stream gave all of it back");
    }

    [Fact]
    public void WhenTheAdmissionIsClosed_EverythingThatWouldSetSomethingAsideRefuses_AndTheRestDoesNothing()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(stored: 1000 * 1024, traffic: 8 << 20);
        rig.Stored.Refresh();
        var admission = rig.Guard.AdmitRequest(1000);
        admission.ReserveResponseStorage(50 * 1024);
        admission.Dispose();
        var traffic = rig.Ledger.UsageToday().Traffic;

        admission.EndRead(500);
        admission.AbortRead();
        admission.CompleteWrite(500);
        admission.CompleteResponseHeaders();
        admission.Dispose();

        rig.Ledger.UsageToday().Traffic.Should().Be(traffic, "a late call moves no counter");
        rig.Ledger.ReservedTraffic.Should().Be(0);
        rig.Stored.Peek().Bytes.Should().Be(0);
        ((Action)(() => admission.BeginRead(10))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => admission.BeginWrite(10))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => admission.ReserveResponseStorage(10))).Should().Throw<ObjectDisposedException>();
        rig.Ledger.ReservedTraffic.Should().Be(0, "the refusals set nothing aside either");
    }

    // ---- 4. usage that is unavailable stays unavailable until a repair or a save is really on disk ---------------------------------------------

    /// <summary>The temporary name a save writes to is taken by a folder: every write of the file fails, the way a full disk or a refused permission does, with no seam.</summary>
    private static string BlockWrites(string file)
    {
        var blocker = file + ".tmp";
        Directory.CreateDirectory(blocker);
        return blocker;
    }

    [Fact]
    public void ARepairWhoseNewFileCannotBeWritten_SaysSo_LeavesTheDamagedFileInPlace_AndTheLedgerUnavailable()
    {
        var clock = new FakeClock(Noon);
        Directory.CreateDirectory(Path.Combine(_dir, "a"));
        File.WriteAllText(Path.Combine(_dir, "a", "usage.json"), "{ damaged");
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), clock: clock);
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxRequestsPerDay: 200));
        var blocker = BlockWrites(rig.UsageFile);

        var problem = rig.Ledger.StartNewLedger();

        problem.Should().NotBeNullOrWhiteSpace().And.Contain("could not be");
        rig.Ledger.IsAvailable.Should().BeFalse("a repair that did not complete does not open the account");
        rig.Guard.Check()!.Reason.Should().Be("usage-unavailable");
        File.ReadAllText(rig.UsageFile).Should().Be("{ damaged", "the damaged file is still THE file: a restart cannot read a missing name as a fresh install");

        Directory.Delete(blocker);
        rig.Ledger.StartNewLedger().Should().BeNull("the disk allows it now");
        rig.Ledger.IsAvailable.Should().BeTrue();
        rig.Guard.Check().Should().BeNull();
        new BlindUsageLedger(rig.UsageFile, clock).IsAvailable.Should().BeTrue("the file on disk is a valid ledger");
    }

    [Fact]
    public void ASaveThatFails_MakesTheLedgerUnavailable_UntilASaveWorks_AndNothingCountedIsLost()
    {
        var clock = new FakeClock(Noon);
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), clock: clock);
        rig.Policy.Current = new BlindAccountPolicy(new BlindAccountLimits(MaxRequestsPerDay: 200));
        var blocker = BlockWrites(rig.UsageFile);
        rig.Ledger.AddRequest();

        rig.Ledger.Flush(); // a write that fails does not throw into the round that asked for it

        rig.Ledger.IsAvailable.Should().BeFalse("counts that nothing records are not counts a limit can be checked against");
        rig.Ledger.UnavailableReason.Should().Contain("saved");
        rig.Guard.Check()!.Reason.Should().Be("usage-unavailable");

        Directory.Delete(blocker);
        clock.Advance(BlindUsageLedger.RetryEvery + TimeSpan.FromSeconds(1));
        rig.Ledger.IsAvailable.Should().BeTrue("the next save worked");
        new BlindUsageLedger(rig.UsageFile, clock).UsageToday().Requests.Should().Be(1, "the count stayed in memory and was written when a save could be");
    }

    [Fact]
    public void AnAccountWithNoUsageLimit_IsNotStoppedByASaveThatFails()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        BlockWrites(rig.UsageFile);
        rig.Ledger.AddRequest();
        rig.Ledger.Flush();

        rig.Guard.Check().Should().BeNull("nothing is checked against the counts, so nothing is wrong with not saving them");
    }

    [Fact]
    public void ANewDeviceDayThatCannotBeSaved_MakesTheDeviceUnavailable_UntilASaveWorks()
    {
        var clock = new FakeClock(Noon);
        var stateFile = Path.Combine(_dir, "device-usage.json");
        Directory.CreateDirectory(_dir);
        var blocker = BlockWrites(stateFile);
        var limits = new BlindDeviceLimits(MaxTrafficBytesPerDay: 10 * 1024);
        var device = new BlindDeviceUsage(clock, stateFile, () => limits);
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), device: device, clock: clock);

        device.Today(); // the first day of the device is saved: here it cannot be

        device.IsAvailable.Should().BeFalse("a day nothing records is a day a restart can forget");
        rig.Guard.Check()!.Reason.Should().Be("device-usage-unavailable");

        Directory.Delete(blocker);
        clock.Advance(BlindUsageLedger.RetryEvery + TimeSpan.FromSeconds(1));
        device.IsAvailable.Should().BeTrue();
        rig.Guard.Check().Should().BeNull();
        File.ReadAllText(stateFile).Should().Contain("highWater");
    }

    [Fact]
    public void ADeviceRepairWhoseNewFileCannotBeWritten_SaysSo_AndLeavesTheDeviceUnavailable()
    {
        var clock = new FakeClock(Noon);
        var stateFile = Path.Combine(_dir, "device-usage.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(stateFile, "{ damaged");
        var limits = new BlindDeviceLimits(MaxTrafficBytesPerDay: 10 * 1024);
        var device = new BlindDeviceUsage(clock, stateFile, () => limits);
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), device: device, clock: clock);
        var blocker = BlockWrites(stateFile);

        var problem = device.StartNewHighWater();

        problem.Should().NotBeNullOrWhiteSpace();
        device.IsAvailable.Should().BeFalse();
        rig.Guard.Check()!.Reason.Should().Be("device-usage-unavailable");
        File.ReadAllText(stateFile).Should().Be("{ damaged");

        Directory.Delete(blocker);
        device.StartNewHighWater().Should().BeNull();
        device.IsAvailable.Should().BeTrue();
        rig.Guard.Check().Should().BeNull();
    }

    [Fact]
    public void OnlyTheNewestFiveKeptAsideCopies_AreKept_AndTheOnlyOneIsNeverRetired()
    {
        var clock = new FakeClock(Noon);
        var file = Path.Combine(_dir, "a", "usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        for (var cycle = 1; cycle <= 7; cycle++)
        {
            File.WriteAllText(file, "damage " + cycle);
            var ledger = new BlindUsageLedger(file, clock);
            ledger.IsAvailable.Should().BeFalse();
            ledger.StartNewLedger().Should().BeNull();
            if (cycle == 1) Evidence(file).Should().ContainSingle("the only damaged copy is kept");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Evidence(file).Select(File.ReadAllText).Should().BeEquivalentTo(["damage 3", "damage 4", "damage 5", "damage 6", "damage 7"], "the five newest stay, the two oldest were retired");
    }

    [Fact]
    public void TheRetentionOrdersNumberedNamesByNumber_NotByText()
    {
        var clock = new FakeClock(Noon); // the clock does not move: twelve repairs within one second take twelve names
        var file = Path.Combine(_dir, "a", "usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        for (var cycle = 1; cycle <= 12; cycle++)
        {
            File.WriteAllText(file, "damage " + cycle);
            new BlindUsageLedger(file, clock).StartNewLedger().Should().BeNull();
        }

        Evidence(file).Select(File.ReadAllText).Should().BeEquivalentTo(["damage 8", "damage 9", "damage 10", "damage 11", "damage 12"], "-10 is newer than -9");
    }

    // ---- 3. the folders are walked again after every persisted batch --------------------------------------------------------------------------

    private static async Task<List<string?>> TwoAdmissionsAsync(BlindLimitGuard guard, long announced, List<BlindRequestAdmission> kept)
    {
        using var go = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            go.Wait();
            try
            {
                var admission = guard.AdmitRequest(0);
                admission.ReserveResponseStorage(announced);
                lock (kept) kept.Add(admission);
                return (string?)null;
            }
            catch (BlindPausedException ex)
            {
                return ex.Pause.Reason;
            }
        })).ToArray();
        go.Set();
        return [.. await Task.WhenAll(tasks)];
    }

    [Fact]
    public async Task TwoAdmissionsRacingAfterRealFilesWereWritten_BothDecideOnTheFreshSize()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(stored: 1000 * 1024);
        rig.Stored.Refresh(); // the walk that is cached: nothing held
        File.WriteAllBytes(Path.Combine(rig.Folder, "grown.db"), new byte[600 * 1024]); // a persisted batch grew the folder by more than anything that came in
        rig.Stored.Invalidate(); // what every place that persists a batch does
        var kept = new List<BlindRequestAdmission>();

        var reasons = await TwoAdmissionsAsync(rig.Guard, 300 * 1024, kept);

        reasons.Count(r => r is null).Should().Be(1, "600 KB are held now and 300 KB fit, 600 KB do not");
        reasons.Count(r => r == BlindPauseReasons.Disk).Should().Be(1);
        rig.Stored.Peek().Bytes.Should().Be(900 * 1024);
        foreach (var admission in kept) admission.Dispose();
    }

    [Fact]
    public void TheNextAdmissionWalksAgain_AfterEachBatch_NotOnlyAfterAMinute()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(stored: 1000 * 1024);
        rig.Guard.Check(0).Should().BeNull(); // the first walk
        File.WriteAllBytes(Path.Combine(rig.Folder, "one.db"), new byte[400 * 1024]);
        rig.Stored.Invalidate();

        rig.Guard.Check(500 * 1024).Should().BeNull("400 + 500 fit in 1000");
        rig.Stored.Peek().Bytes.Should().Be(400 * 1024, "the batch was seen: the decision walked the folder again, not after a minute");
        rig.Stored.IsStale.Should().BeFalse("the walk happened");

        File.WriteAllBytes(Path.Combine(rig.Folder, "two.db"), new byte[300 * 1024]);
        rig.Guard.Check(500 * 1024).Should().BeNull("with no batch announced the minute's cache holds: 400 + 500 fit");
        rig.Stored.Invalidate();
        var afterTwo = rig.Guard.Check(500 * 1024);
        afterTwo.Should().NotBeNull("700 KB are there now: another batch was persisted, and the next decision saw it");
        afterTwo!.Reason.Should().Be(BlindPauseReasons.Disk);
    }

    [Fact]
    public void AScreenKeepsItsMinute_WhileADecisionGetsTheFreshNumber()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(stored: 1000 * 1024);
        rig.Stored.Reading().Bytes.Should().Be(0);
        File.WriteAllBytes(Path.Combine(rig.Folder, "grown.db"), new byte[600 * 1024]);
        rig.Stored.Invalidate();

        rig.Stored.Reading().Bytes.Should().Be(0, "a screen shows the minute's number: it is not walked at every look");
        rig.Stored.IsStale.Should().BeTrue();
        rig.Guard.Check(500 * 1024).Should().NotBeNull("a decision does not use that number");
        rig.Stored.Reading().Bytes.Should().Be(600 * 1024, "and the walk it made is what the screen shows from then on");
    }

    [Fact]
    public void ADeviceDecision_WalksEveryAccountThatSawABatch()
    {
        var clock = new FakeClock(Noon);
        var limits = new BlindDeviceLimits(MaxStoredBytes: 1000 * 1024);
        var device = new BlindDeviceUsage(clock, null, () => limits);
        using var first = NewRig((request, ct) => Task.FromResult(Ok(1)), "a", device, clock: clock);
        using var second = NewRig((request, ct) => Task.FromResult(Ok(1)), "b", device, clock: clock);
        second.Guard.Check(0).Should().BeNull(); // everything is measured once
        File.WriteAllBytes(Path.Combine(first.Folder, "grown.db"), new byte[700 * 1024]); // the OTHER account persisted a batch
        first.Stored.Invalidate();

        var pause = second.Guard.Check(400 * 1024);

        pause.Should().NotBeNull("the device holds 700 KB between its accounts: the second account's decision walked the first one's folder");
        pause!.Reason.Should().Be(BlindPauseReasons.DeviceDisk);
    }
}

/// <summary>The device's storage is the sum over every LISTED account, not over the runtimes that are up (fix round 3).</summary>
public sealed class BlindLimitsFix3AccountTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly AccountRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private string FolderOf(string id) => Path.Combine(_rig.Root, "accounts", id);

    /// <summary>A file in the account's media folder, which is one of the places the account owns (a file anywhere else in the folder is not counted).</summary>
    private string Media(string id)
    {
        var folder = Path.Combine(FolderOf(id), "media");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "grown.bin");
    }

    private static BlindPause? DeviceDecision(BlindAccountHost host, string id, long planned) =>
        host.ServicesFor(id).GetRequiredService<BlindLimitGuard>().Check(planned);

    [Fact]
    public async Task AnAccountInTheMiddleOfItsRemoval_StillCountsTowardsTheDevice_WhileAnotherIsAdmitted()
    {
        await using var host = _rig.Host(crash: new CrashAtNamed("remove:closed"), time: new FakeClock(Noon));
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;
        File.WriteAllBytes(Media(a), new byte[6 << 20]);
        host.ServicesFor(a).GetRequiredService<BlindStoredBytes>().Invalidate();
        host.SetDeviceLimits(new BlindDeviceLimits(MaxStoredBytes: 8L << 20)).Should().BeNull();

        var removal = await Record.ExceptionAsync(() => host.RemoveAsync(a)); // the process "dies" after the runtime was closed and before a file was deleted

        removal.Should().BeOfType<SimulatedCrash>();
        File.Exists(Media(a)).Should().BeTrue("the removal did not get to the files");
        host.GetSnapshot().Accounts.Single(c => c.Entry.Id == a).Entry.Phase.Should().Be(BlindAccountPhases.Deleting);
        var pause = DeviceDecision(host, b, 3 << 20);
        pause.Should().NotBeNull("A's files are still on the device, and a removal that is not finished does not give their room away");
        pause!.Reason.Should().Be(BlindPauseReasons.DeviceDisk);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task OnceTheFilesAreGoneAndTheEntryIsOut_TheRoomIsGivenBack()
    {
        await using var host = _rig.Host(time: new FakeClock(Noon));
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;
        File.WriteAllBytes(Media(a), new byte[6 << 20]);
        host.ServicesFor(a).GetRequiredService<BlindStoredBytes>().Invalidate();
        host.SetDeviceLimits(new BlindDeviceLimits(MaxStoredBytes: 8L << 20)).Should().BeNull();
        DeviceDecision(host, b, 3 << 20).Should().NotBeNull();

        await host.RemoveAsync(a);

        host.DeviceUsage.AccountCount.Should().Be(1, "only B is listed now");
        DeviceDecision(host, b, 3 << 20).Should().BeNull("A is gone: its files, and its entry");
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task AListedAccountWithNoRuntimeAndAFolderThatCannotBeMeasured_BlocksTheDevicesStorageDecision()
    {
        string a, b;
        await using (var first = _rig.Host(time: new FakeClock(Noon)))
        {
            await first.StartAsync();
            a = (await first.AddAsync("A")).AccountId!;
            b = (await first.AddAsync("B")).AccountId!;
            first.SetDeviceLimits(new BlindDeviceLimits(MaxStoredBytes: 8L << 20)).Should().BeNull();
        }
        SqliteConnection.ClearAllPools();
        var folderA = FolderOf(a);
        var elsewhere = Path.Combine(_rig.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.Delete(folderA, recursive: true);
        if (!TestLinks.TryCreateDirectoryLink(folderA, elsewhere)) return; // links cannot be made here

        await using var host = _rig.Host(time: new FakeClock(Noon));
        await host.StartAsync();

        host.GetSnapshot().Accounts.Single(c => c.Entry.Id == a).Status.Should().BeNull("A has no runtime: its folder is a link");
        var pause = DeviceDecision(host, b, 1);
        pause.Should().NotBeNull("what A holds cannot be said, so the device cannot say what it holds");
        pause!.Reason.Should().Be("storage-unknown");
        SqliteConnection.ClearAllPools();
    }
}
