using System.Net;
using System.Runtime.CompilerServices;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Fix round 2, the parts that look at the new surface itself: what an admission sets aside and gives back, the peak footprint of a long job, the repair of a usage file that cannot be
/// trusted, the stored-size meter's unknown state and its walk, and the words and buttons the screens get from them. (The outcomes these serve are in <see cref="BlindLimitsFix2Tests"/>.)
/// </summary>
public sealed partial class BlindLimitsFix2Tests
{
    private static BlindAccountPolicy Limited(long? stored = null, long? traffic = null, int? requests = null) =>
        new(new BlindAccountLimits(stored, traffic, requests));

    // ---- what an admission sets aside, spends and gives back ----------------------------------------------------------------------------

    [Fact]
    public void AnAdmittedRequest_SetsItsCostAside_AndGivesBackWhatWasNotUsed()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(traffic: 1 << 20);

        var admission = rig.Guard.AdmitRequest(10 * 1024);

        rig.Ledger.UsageToday().Requests.Should().Be(1);
        rig.Ledger.UsageToday().BytesOut.Should().Be(BlindMeteringTap.OverheadBytes, "the headers went out at once");
        rig.Ledger.ReservedTraffic.Should().Be(BlindMeteringTap.OverheadBytes + 10 * 1024, "the answer's headers and the announced body are set aside");

        admission.Dispose();

        rig.Ledger.ReservedTraffic.Should().Be(0, "a request that ended before it sent its body gives the body back");
        rig.Ledger.UsageToday().Traffic.Should().Be(BlindMeteringTap.OverheadBytes);
    }

    [Fact]
    public void AWholeRequest_SpendsWhatWasSetAside_AndNothingStaysReserved()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(traffic: 1 << 20);
        rig.Stored.Refresh();

        using (var admission = rig.Guard.AdmitRequest(2000))
        {
            admission.BeginWrite(2000);
            admission.CompleteWrite(2000);
            admission.CompleteResponseHeaders();
            var allowed = admission.BeginRead(1 << 20);
            allowed.Should().Be(BlindMeteringTap.CheckEveryBytes, "a read never carries the answer past the next 256 KB check");
            rig.Ledger.ReservedTraffic.Should().Be(allowed, "what the read may take is set aside before it");
            admission.EndRead(1000);
            rig.Ledger.ReservedTraffic.Should().Be(0, "a read that returned less than it was allowed to takes back the difference");
        }

        var today = rig.Ledger.UsageToday();
        today.BytesOut.Should().Be(BlindMeteringTap.OverheadBytes + 2000);
        today.BytesIn.Should().Be(BlindMeteringTap.OverheadBytes + 1000);
        rig.Stored.Peek().Bytes.Should().Be(1000, "what arrived is what the account holds now, with no walk");
    }

    [Fact]
    public void AReadThatFails_GivesItsReservationBack()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(traffic: 1 << 20, stored: 1 << 20);
        rig.Stored.Refresh();
        using var admission = rig.Guard.AdmitRequest(0);

        admission.BeginRead(100_000);
        rig.Ledger.ReservedTraffic.Should().BeGreaterThan(0);
        rig.Stored.Peek().Bytes.Should().Be(100_000, "the room for the read is held while it is under way");
        admission.AbortRead();

        rig.Ledger.ReservedTraffic.Should().Be(BlindMeteringTap.OverheadBytes, "only the answer's headers are still set aside");
        rig.Stored.Peek().Bytes.Should().Be(0);
    }

    [Fact]
    public void AnAnnouncedResponse_IsSetAsideBeforeItsFirstByte_AndBecomesWhatIsHeldAsItIsRead()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(stored: 1000 * 1024);
        rig.Stored.Refresh();
        var admission = rig.Guard.AdmitRequest(0);

        admission.ReserveResponseStorage(300 * 1024);
        rig.Stored.Peek().Bytes.Should().Be(300 * 1024, "set aside before the first byte");

        admission.BeginRead(100 * 1024).Should().Be(100 * 1024);
        admission.EndRead(100 * 1024);
        rig.Stored.Peek().Bytes.Should().Be(300 * 1024, "100 KB arrived (held), 200 KB are still set aside");

        admission.Dispose();
        rig.Stored.Peek().Bytes.Should().Be(100 * 1024, "the 200 KB that never came are given back");
    }

    [Fact]
    public void AnAnnouncedResponseThatDoesNotFit_IsRefusedAndSetsNothingAside()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(stored: 100 * 1024);
        rig.Stored.Refresh();
        using var admission = rig.Guard.AdmitRequest(0);

        var act = () => admission.ReserveResponseStorage(300 * 1024);

        act.Should().Throw<BlindPausedException>().Which.Pause.Reason.Should().Be(BlindPauseReasons.Disk);
        rig.Stored.Peek().Bytes.Should().Be(0);
    }

    [Fact]
    public void AnUnannouncedResponse_MayOnlyGoAsFarAsTheRoom_AndThenTheStreamStopsWithAPause()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(stored: 100 * 1024);
        rig.Stored.Refresh();
        using var admission = rig.Guard.AdmitRequest(0);

        var allowed = admission.BeginRead(1 << 20);
        allowed.Should().Be(100 * 1024, "no length was announced: the room that is left is the most it may take");
        admission.EndRead(allowed);
        var next = () => admission.BeginRead(10);

        next.Should().Throw<BlindPausedException>().Which.Pause.Reason.Should().Be(BlindPauseReasons.Disk);
        rig.Stored.Peek().Bytes.Should().Be(100 * 1024);
    }

    [Fact]
    public async Task ARequestThatFails_LeavesNothingSetAside()
    {
        using var rig = NewRig((request, ct) => throw new HttpRequestException("no route to host"));
        rig.Policy.Current = Limited(traffic: 1 << 20, stored: 1 << 20);
        using var client = rig.Client;

        var act = () => client.PostAsync(Url, new ByteArrayContent(new byte[10 * 1024]));

        await act.Should().ThrowAsync<HttpRequestException>();
        rig.Ledger.ReservedTraffic.Should().Be(0);
        rig.Stored.Peek().Bytes.Should().Be(0);
        rig.Ledger.UsageToday().Requests.Should().Be(1);
    }

    [Fact]
    public async Task ARequestThatIsCancelledWhileItsBodyGoesOut_LeavesNothingSetAside()
    {
        var wrote = new TaskCompletionSource();
        using var rig = NewRig(async (request, ct) =>
        {
            await request.Content!.ReadAsByteArrayAsync(ct);
            return Ok(1);
        });
        rig.Policy.Current = Limited(traffic: 8 << 20);
        using var client = rig.Client;
        using var cancel = new CancellationTokenSource();
        var body = new ScriptedContent(1 << 20, async (stream, ct) =>
        {
            await stream.WriteAsync(new byte[2048], ct);
            wrote.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });

        var send = client.PostAsync(Url, body, cancel.Token);
        await wrote.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Ledger.ReservedTraffic.Should().BeGreaterThan(1 << 19, "most of the announced megabyte is still set aside while it is going out");
        await cancel.CancelAsync();
        var act = () => send;

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Ledger.ReservedTraffic.Should().Be(0, "the megabyte that never went out is given back");
    }

    [Fact]
    public async Task AResponseThatIsDisposedBeforeItsEnd_HoldsOnlyWhatWasRead()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(300 * 1024, 300 * 1024)));
        rig.Policy.Current = Limited(stored: 1000 * 1024, traffic: 8 << 20);
        using var client = rig.Client;

        using (var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead))
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[10 * 1024];
            (await stream.ReadAsync(buffer)).Should().Be(10 * 1024);
            rig.Stored.Peek().Bytes.Should().Be(300 * 1024, "all of it is set aside while the answer is open");
        }

        rig.Stored.Peek().Bytes.Should().Be(10 * 1024, "the rest was given back when the response was disposed");
        rig.Ledger.ReservedTraffic.Should().Be(0);
    }

    [Fact]
    public async Task AReadOfZeroBytes_IsNotTheEndOfTheBody_AndKeepsWhatIsSetAside()
    {
        // Some stacks read zero bytes to wait for data. That returns 0 without being the end of the answer, and must not give back what the answer has set aside.
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(300 * 1024, 300 * 1024)));
        rig.Policy.Current = Limited(stored: 1000 * 1024);
        using var client = rig.Client;
        using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();

        (await stream.ReadAsync(Memory<byte>.Empty)).Should().Be(0);

        rig.Stored.Peek().Bytes.Should().Be(300 * 1024, "the answer has not ended: its room is still set aside");
        (await stream.ReadAsync(new byte[100 * 1024])).Should().Be(100 * 1024);
        rig.Stored.Peek().Bytes.Should().Be(300 * 1024);
    }

    [Fact]
    public void AnAdmissionThatIsDroppedWithoutBeingDisposed_IsGivenBackByTheFinalizer()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(traffic: 1 << 20);

        DropAnAdmission(rig.Guard);
        rig.Ledger.ReservedTraffic.Should().BeGreaterThan(0);
        for (var i = 0; i < 3 && rig.Ledger.ReservedTraffic != 0; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        rig.Ledger.ReservedTraffic.Should().Be(0, "a reservation can never outlive its request");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropAnAdmission(BlindLimitGuard guard) => _ = guard.AdmitRequest(5000);

    // ---- the peak footprint of a long job -------------------------------------------------------------------------------------------------

    [Fact]
    public void APackage_SetsItsPeakFootprintAside_ForTheWholeJob_AndTheJobGivesItBack()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(stored: 500 * 1024);
        rig.Stored.Refresh();

        rig.Guard.AdmitPackage(100 * 1024, BlindPackageFootprint.Install);

        rig.Stored.Peek().Bytes.Should().Be(200 * 1024, "the archive is the HTTP layer's; its extraction and the installed copy are two more archives' worth");
        rig.Stored.ReleaseJob();
        rig.Stored.Peek().Bytes.Should().Be(0);
    }

    [Fact]
    public void ABackup_NeedsMoreRoomThanAnInstall()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(stored: 250 * 1024);
        rig.Stored.Refresh();

        rig.Guard.AdmitPackage(100 * 1024, BlindPackageFootprint.Install);
        rig.Stored.ReleaseJob();
        var backup = () => rig.Guard.AdmitPackage(100 * 1024, BlindPackageFootprint.Backup);

        var thrown = backup.Should().Throw<BlindPausedException>().Which;
        thrown.Pause.Reason.Should().Be(BlindPauseReasons.Disk);
        thrown.Pause.Needed.Should().Be(300 * 1024);
        rig.Stored.Peek().Bytes.Should().Be(0, "a refused job sets nothing aside");
    }

    [Fact]
    public async Task TheArchiveItselfMustFitBesideTheFootprintOfTheJob()
    {
        // 200 KB are set aside for the job's extraction and installed copy; the archive's own 100 KB is then announced by the HTTP answer: 300 KB do not fit in 250 KB.
        var served = 0L;
        using var rig = NewRig((request, ct) => Task.FromResult(Streamed(100 * 1024, 100 * 1024, n => Interlocked.Add(ref served, n))));
        rig.Policy.Current = Limited(stored: 250 * 1024);
        rig.Guard.AdmitPackage(100 * 1024, BlindPackageFootprint.Install);
        using var client = rig.Client;

        var act = () => client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);

        (await act.Should().ThrowAsync<BlindPausedException>()).Which.Pause.Reason.Should().Be(BlindPauseReasons.Disk);
        Interlocked.Read(ref served).Should().Be(0);
        rig.Stored.ReleaseJob();
        rig.Stored.Peek().Bytes.Should().Be(0);
    }

    [Fact]
    public void AnAbsurdArchiveLength_IsRefused_AndNeededSaturates()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(stored: 500 * 1024);

        var act = () => rig.Guard.AdmitPackage(long.MaxValue, BlindPackageFootprint.Backup);

        act.Should().Throw<BlindPausedException>().Which.Pause.Needed.Should().Be(long.MaxValue, "three times an absurd length is still an absurd length, not a wrapped one");
    }

    [Fact]
    public void ADeviceStorageLimit_IsCheckedAgainstTheJobsFootprintToo()
    {
        var clock = new FakeClock(Noon);
        var limits = new BlindDeviceLimits(MaxStoredBytes: 250 * 1024);
        var device = new BlindDeviceUsage(clock, null, () => limits);
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), device: device, clock: clock);

        var act = () => rig.Guard.AdmitPackage(100 * 1024, BlindPackageFootprint.Backup);

        act.Should().Throw<BlindPausedException>().Which.Pause.Reason.Should().Be(BlindPauseReasons.DeviceDisk);
    }

    [Fact]
    public void WithoutAStorageLimit_APackageSetsNothingAside()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Stored.Refresh();

        rig.Guard.AdmitPackage(100 * 1024, BlindPackageFootprint.Backup);

        rig.Stored.Peek().Bytes.Should().Be(0);
    }

    [Fact]
    public void TheJobEndsAndGivesBackOnlyItsOwnReservation_NotTheRequestsThatAreStillOpen()
    {
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)));
        rig.Policy.Current = Limited(stored: 1000 * 1024);
        rig.Stored.Refresh();
        rig.Guard.AdmitPackage(100 * 1024, BlindPackageFootprint.Install);
        using var admission = rig.Guard.AdmitRequest(0);
        admission.ReserveResponseStorage(100 * 1024);

        rig.Stored.ReleaseJob();

        rig.Stored.Peek().Bytes.Should().Be(100 * 1024, "the answer that is still coming keeps its room");
    }

    // ---- usage that cannot be trusted: evidence, and the repair that is asked for ------------------------------------------------------

    private static string[] Evidence(string file) =>
        Directory.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file) + ".damaged-*");

    [Fact]
    public void StartingANewLedger_KeepsTheDamagedFileAside_AndWritesAValidOne()
    {
        var clock = new FakeClock(Noon);
        var file = Path.Combine(_dir, "a", "usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ damaged");
        var ledger = new BlindUsageLedger(file, clock);
        ledger.IsAvailable.Should().BeFalse();
        ledger.UnavailableReason.Should().NotBeNullOrWhiteSpace().And.NotContain(_dir, "no path in a sentence for the screen");
        ledger.AddRequest();

        var problem = ledger.StartNewLedger();

        problem.Should().BeNull();
        var evidence = Evidence(file).Should().ContainSingle().Subject;
        Path.GetFileName(evidence).Should().Be("usage.json.damaged-20261021T120000Z");
        File.ReadAllText(evidence).Should().Be("{ damaged", "the evidence is the file as it was");
        ledger.IsAvailable.Should().BeTrue();
        ledger.UsageToday().Requests.Should().Be(1, "what was counted since the start is kept");
        new BlindUsageLedger(file, clock).UsageToday().Requests.Should().Be(1, "the new file is valid and holds it");
    }

    [Fact]
    public void TheEvidenceOfAnEarlierDamage_IsNeverOverwritten()
    {
        var clock = new FakeClock(Noon);
        var file = Path.Combine(_dir, "a", "usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "first damage");
        new BlindUsageLedger(file, clock).StartNewLedger().Should().BeNull();
        File.WriteAllText(file, "second damage");

        new BlindUsageLedger(file, clock).StartNewLedger().Should().BeNull("the same second, the same name: the second one is numbered, not written over the first");

        var evidence = Evidence(file);
        evidence.Should().HaveCount(2);
        evidence.Select(File.ReadAllText).Should().BeEquivalentTo("first damage", "second damage");
    }

    [Fact]
    public void StartingANewLedgerOfAHealthyOne_ChangesNothing()
    {
        var clock = new FakeClock(Noon);
        var file = Path.Combine(_dir, "a", "usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var before = ValidUsage(clock, file);
        var ledger = new BlindUsageLedger(file, clock);

        var problem = ledger.StartNewLedger();

        problem.Should().NotBeNullOrWhiteSpace("there is nothing to start again");
        Evidence(file).Should().BeEmpty();
        File.ReadAllText(file).Should().Be(before);
    }

    [Fact]
    public void CountsMadeWhileTheFileCouldNotBeOpened_AreAddedToTheOnesInTheFileWhenItCan()
    {
        var clock = new FakeClock(Noon);
        var file = Path.Combine(_dir, "a", "usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        ValidUsage(clock, file);
        var ledger = new BlindUsageLedger(file, clock);

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            ledger.IsAvailable.Should().BeFalse();
            for (var i = 0; i < 3; i++) ledger.AddRequest();
            ledger.Flush();
        }
        clock.Advance(BlindUsageLedger.RetryEvery + TimeSpan.FromSeconds(1));

        ledger.IsAvailable.Should().BeTrue();
        ledger.UsageToday().Requests.Should().Be(153, "the 150 that were in the file and the 3 counted meanwhile");
    }

    [Fact]
    public void AnAccountThatWasPausedForItsUsage_GoesOnWhenANewLedgerIsStarted()
    {
        var clock = new FakeClock(Noon);
        var file = Path.Combine(_dir, "a", "usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ damaged");
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), clock: clock);
        rig.Policy.Current = Limited(requests: 200);
        rig.Guard.Check()!.Reason.Should().Be("usage-unavailable");

        rig.Ledger.StartNewLedger().Should().BeNull();

        rig.Guard.Check().Should().BeNull();
    }

    [Fact]
    public void StartingNewDeviceCounts_KeepsTheDamagedFileAside_AndTheDeviceGoesOn()
    {
        var clock = new FakeClock(Noon);
        var stateFile = Path.Combine(_dir, "device-usage.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(stateFile, "{ damaged");
        var limits = new BlindDeviceLimits(MaxTrafficBytesPerDay: 10 * 1024);
        var device = new BlindDeviceUsage(clock, stateFile, () => limits);
        using var rig = NewRig((request, ct) => Task.FromResult(Ok(1)), device: device, clock: clock);
        device.IsAvailable.Should().BeFalse();
        device.UnavailableReason.Should().NotBeNullOrWhiteSpace().And.NotContain(_dir);
        rig.Guard.Check()!.Reason.Should().Be("device-usage-unavailable");

        device.StartNewHighWater().Should().BeNull();

        var evidence = Directory.GetFiles(_dir, "device-usage.json.damaged-*").Should().ContainSingle().Subject;
        File.ReadAllText(evidence).Should().Be("{ damaged");
        device.IsAvailable.Should().BeTrue();
        rig.Guard.Check().Should().BeNull();
        File.ReadAllText(stateFile).Should().Contain("highWater");
        device.StartNewHighWater().Should().NotBeNullOrWhiteSpace("nothing to repair the second time");
    }

    // ---- the stored-size meter: unknown, and its walk -----------------------------------------------------------------------------------------

    [Fact]
    public void AnInaccessibleFolder_IsAnUnknownSize_NotZero_AndItIsMeasuredAgainWhenItCanBeRead()
    {
        var clock = new FakeClock(Noon);
        var folder = Path.Combine(_dir, "a", "data");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "held.bin"), new byte[4096]);
        var meter = new BlindStoredBytes(() => [folder], clock);
        var denied = TestAccess.DenyListing(folder);
        if (denied is null) return; // this machine allows this process to list it anyway

        BlindStorageReading reading;
        try
        {
            reading = meter.Reading();
            meter.Get().Should().Be(0, "a screen that wants a number gets 0; Reading says it is not one");
        }
        finally
        {
            denied.Dispose();
        }

        reading.Known.Should().BeFalse();
        reading.Problem.Should().NotBeNullOrWhiteSpace().And.NotContain(folder, "the sentence names no path");

        meter.Reading().Known.Should().BeFalse("an unknown size is tried again after a few seconds, not at every call");
        clock.Advance(TimeSpan.FromSeconds(10));
        var again = meter.Reading();
        again.Known.Should().BeTrue();
        again.Bytes.Should().Be(4096);
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(IOException))]
    public void AFolderThatCannotBeListed_IsAnUnknownSize_WhateverTheReason_AndIsMeasuredAgainLater(Type failure)
    {
        var clock = new FakeClock(Noon);
        var folder = Path.Combine(_dir, "a", "data");
        var inner = Path.Combine(folder, "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllBytes(Path.Combine(folder, "held.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(inner, "more.bin"), new byte[500]);
        var failing = true;
        var meter = new BlindStoredBytes(() => [folder], clock, null, walkHook: visiting =>
        {
            if (failing && visiting == inner) throw (Exception)Activator.CreateInstance(failure)!;
        });

        var reading = meter.Reading();

        reading.Known.Should().BeFalse("a folder that cannot be listed is not an empty folder: 1000 bytes were seen and 500 were not");
        reading.Problem.Should().NotBeNullOrWhiteSpace().And.NotContain(_dir);
        meter.Get().Should().Be(0);

        failing = false;
        clock.Advance(TimeSpan.FromSeconds(10));
        var again = meter.Reading();
        again.Known.Should().BeTrue();
        again.Bytes.Should().Be(1500);
    }

    [Fact]
    public void AFolderThatVanishesDuringTheWalk_HoldsNothingNow_AndTheRestIsStillCounted()
    {
        var clock = new FakeClock(Noon);
        var folder = Path.Combine(_dir, "a", "data");
        var inner = Path.Combine(folder, "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllBytes(Path.Combine(folder, "held.bin"), new byte[1000]);
        var meter = new BlindStoredBytes(() => [folder], clock, null, walkHook: visiting =>
        {
            if (visiting == inner) throw new DirectoryNotFoundException();
        });

        var reading = meter.Reading();

        reading.Known.Should().BeTrue("a folder that is gone is not a folder that cannot be read");
        reading.Bytes.Should().Be(1000);
    }

    [Fact]
    public void ALinkThatAppearsWhileTheWalkIsUnderWay_MakesTheSizeUnknown()
    {
        var clock = new FakeClock(Noon);
        var folder = Path.Combine(_dir, "a", "data");
        var inner = Path.Combine(folder, "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllBytes(Path.Combine(inner, "held.bin"), new byte[4096]);
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "big.bin"), new byte[1 << 20]);
        var swapped = false;
        var supported = true;
        var meter = new BlindStoredBytes(() => [folder], clock, Path.Combine(_dir, "a"), walkHook: visiting =>
        {
            if (visiting != inner || swapped) return;
            swapped = true;
            Directory.Delete(inner, recursive: true);
            supported = TestLinks.TryCreateDirectoryLink(inner, elsewhere);
        });

        var reading = meter.Reading();

        if (!supported) return; // links cannot be made here
        reading.Known.Should().BeFalse("a link that took the place of a folder under the walk is not counted and not followed");
        reading.Problem.Should().Contain("link");
    }

    [Fact]
    public void ALinkAmongTheFoldersAboveTheRoot_IsRefusedBeforeAnythingIsListed()
    {
        var clock = new FakeClock(Noon);
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "data"));
        File.WriteAllBytes(Path.Combine(elsewhere, "data", "big.bin"), new byte[1 << 20]);
        var account = Path.Combine(_dir, "a");
        Directory.CreateDirectory(account);
        if (!TestLinks.TryCreateDirectoryLink(Path.Combine(account, "mid"), elsewhere)) return;
        var listed = new List<string>();
        var meter = new BlindStoredBytes(() => [Path.Combine(account, "mid", "data")], clock, account, walkHook: listed.Add);

        var reading = meter.Reading();

        reading.Known.Should().BeFalse();
        listed.Should().BeEmpty("nothing was listed through the link");
    }

    [Fact]
    public void TheWalk_KeepsWhatWasChargedWhileItRan_AndDoesNotCountTwiceWhatWasChargedBefore()
    {
        var clock = new FakeClock(Noon);
        var folder = Path.Combine(_dir, "a", "data");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "held.bin"), new byte[1000]);
        BlindStoredBytes? meter = null;
        var meterWithHook = new BlindStoredBytes(() => [folder], clock, null, walkHook: _ => meter!.Charge(500));
        meter = meterWithHook;
        meter.Charge(1000); // arrived before the walk, and is in the file the walk sees

        meter.Resync();

        meter.Peek().Bytes.Should().Be(1000 + 500, "the 1000 that were charged before the walk are the file it counted; the 500 that arrived during it are kept on top");
    }

    // ---- the words and the buttons ----------------------------------------------------------------------------------------------------------

    private static BlindAccountEntry Entry(BlindAccountLimits? limits = null) =>
        new("0123456789abcdef0123456789abcdef", BlindAccountLayouts.Dir, "Friend", BlindAccountPhases.Active, Limits: limits);

    private static BlindAppStatus Status() =>
        new(Guid.NewGuid(), "device", true, true, Noon.AddMinutes(-4), null, BlindBackupSchedule.Off, false, null, null, [], [], Endpoint: "https://mem.example.org:5300");

    [Fact]
    public void AnUnknownStoredSize_IsShownAsUnknown_AndTheRemovalDoesNotClaimASize()
    {
        var account = new BlindAccountSnapshot(Entry(new BlindAccountLimits(MaxStoredBytes: 2L << 30)), Status(), Usage: new BlindAccountUsageView(null, 5, 100, 100, null));

        var card = BlindAccountCard.Build(account, TimeZoneInfo.Utc, Noon);

        card.Stored.Should().Be("unknown");
        BlindRemoveConfirmation.Build(account).Message.Should().Contain("Its data").And.NotContain(" 0 B");
    }

    [Fact]
    public void AccountWhoseUsageCannotBeTrusted_ShowsAProblem_OffersTheRepair_AndSaysSo()
    {
        var pause = new BlindPause("usage-unavailable");
        var account = new BlindAccountSnapshot(Entry(new BlindAccountLimits(MaxRequestsPerDay: 200)), Status(), Pause: pause,
            Usage: new BlindAccountUsageView(1000, 0, 0, 0, null, Problem: "The usage file is empty or damaged."));

        var card = BlindAccountCard.Build(account, TimeZoneInfo.Utc, Noon);

        card.CanRepairUsage.Should().BeTrue();
        card.Kind.Should().Be(BlindCardKind.Problem, "not a wait and not a limit: a fault that needs the person");
        card.State.Should().Contain("usage counts").And.Contain("Start new counts");
        card.Today.Should().Be("usage counts unavailable");
        card.IsPaused.Should().BeTrue();
        card.CanSyncNow.Should().BeFalse();
    }

    [Fact]
    public void AHealthyCard_OffersNoRepair()
    {
        var card = BlindAccountCard.Build(new BlindAccountSnapshot(Entry(), Status(), Usage: new BlindAccountUsageView(1000, 3, 10, 10, null)), TimeZoneInfo.Utc, Noon);

        card.CanRepairUsage.Should().BeFalse();
    }

    [Fact]
    public void TheDeviceLine_SaysUnknown_AndOffersTheDeviceRepair()
    {
        var account = new BlindAccountSnapshot(Entry(), Status(), Usage: new BlindAccountUsageView(null, 0, 0, 0, null));
        var snapshot = new BlindAccountsSnapshot([account], BlindDeviceLimits.None, null, true, 8, new BlindDeviceUsageView(null, 0, 0, 1, "The device's usage file is damaged."));

        var view = BlindAccountsView.Build(snapshot, TimeZoneInfo.Utc, Noon);

        view.DeviceLine.Should().Contain("stored size unknown");
        view.CanRepairDeviceUsage.Should().BeTrue();
        view.DeviceUsageProblem.Should().Be("The device's usage file is damaged.");
    }

    [Theory]
    [InlineData("usage-unavailable")]
    [InlineData("device-usage-unavailable")]
    [InlineData("storage-unknown")]
    public void TheFaultReasons_AreWordsOfTheContract_FaultsOnTheCard_AndNeverToldToTheSource(string reason)
    {
        BlindPauseReasons.All.Should().Contain(reason);
        BlindPauseReasons.IsReportable(reason).Should().BeFalse("they are about this device's own files: the source is not told");
        BlindPauseText.IsFault(new BlindPause(reason)).Should().BeTrue();
        BlindPauseText.IsWaiting(new BlindPause(reason)).Should().BeFalse();
        BlindPauseText.Say(new BlindPause(reason)).Should().NotBe("paused.").And.NotContain(_dir);
    }

    [Fact]
    public void TheLimitReasons_AreNotFaults()
    {
        foreach (var reason in new[] { BlindPauseReasons.Traffic, BlindPauseReasons.Requests, BlindPauseReasons.Disk, BlindPauseReasons.DeviceTraffic, BlindPauseReasons.DeviceDisk, BlindPauseReasons.PausedByYou })
            BlindPauseText.IsFault(new BlindPause(reason)).Should().BeFalse(reason);
    }

    [Theory]
    [InlineData(long.MaxValue, 5L)]
    [InlineData(5L, long.MaxValue)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void TheArithmetic_Saturates_AndNeverWraps(long held, long planned)
    {
        BlindMath.AddSat(held, planned).Should().Be(long.MaxValue);
        BlindMath.Fits(1000, held, planned).Should().BeFalse();
        BlindMath.Needed(held, planned).Should().Be(long.MaxValue);
        BlindMath.MulSat(planned, 3).Should().BeGreaterThanOrEqualTo(planned);
    }

    [Fact]
    public void TheArithmetic_AgreesWithPlainMathWhereNothingOverflows()
    {
        BlindMath.Fits(1000, 400, 600).Should().BeTrue();
        BlindMath.Fits(1000, 400, 601).Should().BeFalse();
        BlindMath.Fits(1000, 1001, 0).Should().BeFalse("what is held is already over the limit");
        BlindMath.Fits(1000, 0, -1).Should().BeFalse("a negative size is not a size");
        BlindMath.SubFloor(5, 10).Should().Be(0);
        BlindMath.SubFloor(10, 5).Should().Be(5);
        BlindMath.MulSat(1 << 20, 3).Should().Be(3 << 20);
        BlindMath.MulSat(long.MaxValue / 2 + 1, 2).Should().Be(long.MaxValue);
        BlindMath.Needed(600, 400).Should().Be(1000);
    }
}

/// <summary>The repair of usage counts through the real account host: the card shows it, the guard pauses on it, and the repair keeps the evidence.</summary>
public sealed class BlindLimitsFix2HostTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly AccountRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task ADamagedUsageFile_ShowsOnTheCard_PausesTheAccount_AndTheRepairKeepsTheEvidence()
    {
        var clock = new FakeClock(Noon);
        string a;
        await using (var first = _rig.Host(time: clock))
        {
            await first.StartAsync();
            a = (await first.AddAsync("A")).AccountId!;
            first.SetLimits(a, new BlindAccountLimits(MaxRequestsPerDay: 200)).Should().BeNull();
        }
        SqliteConnection.ClearAllPools();
        var folder = Path.Combine(_rig.Root, "accounts", a);
        var file = Path.Combine(folder, "usage.json");
        File.WriteAllText(file, "{ damaged");

        await using var host = _rig.Host(time: clock);
        await host.StartAsync();
        var account = host.GetSnapshot().Accounts.Single();

        account.Usage!.Problem.Should().NotBeNullOrWhiteSpace();
        account.Pause!.Reason.Should().Be("usage-unavailable");
        var card = BlindAccountCard.Build(account, clock.LocalTimeZone, clock.GetUtcNow());
        card.CanRepairUsage.Should().BeTrue();
        card.Kind.Should().Be(BlindCardKind.Problem);
        File.ReadAllText(file).Should().Be("{ damaged", "starting the host and reading the card did not touch the file");

        host.StartNewUsageLedger(a).Should().BeNull();

        var evidence = Directory.GetFiles(folder, "usage.json.damaged-*").Should().ContainSingle().Subject;
        File.ReadAllText(evidence).Should().Be("{ damaged");
        host.GetSnapshot().Accounts.Single().Usage!.Problem.Should().BeNull();
        host.PauseOf(a).Should().BeNull();
        File.ReadAllText(file).Should().Contain("version");
        host.StartNewUsageLedger(a).Should().NotBeNullOrWhiteSpace("a second repair has nothing to repair");
        Directory.GetFiles(folder, "usage.json.damaged-*").Should().HaveCount(1);
    }

    [Fact]
    public async Task ADamagedDeviceFile_ShowsOnTheDeviceLine_PausesTheAccounts_AndTheRepairKeepsTheEvidence()
    {
        var clock = new FakeClock(Noon);
        string a;
        await using (var first = _rig.Host(time: clock))
        {
            await first.StartAsync();
            a = (await first.AddAsync("A")).AccountId!;
            first.SetDeviceLimits(new BlindDeviceLimits(MaxTrafficBytesPerDay: 1L << 30)).Should().BeNull();
        }
        SqliteConnection.ClearAllPools();
        var file = Path.Combine(_rig.Root, "device-usage.json");
        File.WriteAllText(file, "{ damaged");

        await using var host = _rig.Host(time: clock);
        await host.StartAsync();
        var snapshot = host.GetSnapshot();

        snapshot.DeviceUsage!.Problem.Should().NotBeNullOrWhiteSpace();
        host.PauseOf(a)!.Reason.Should().Be("device-usage-unavailable");
        BlindAccountsView.Build(snapshot, clock.LocalTimeZone, clock.GetUtcNow()).CanRepairDeviceUsage.Should().BeTrue();
        File.ReadAllText(file).Should().Be("{ damaged");

        host.StartNewDeviceUsageLedger().Should().BeNull();

        var evidence = Directory.GetFiles(_rig.Root, "device-usage.json.damaged-*").Should().ContainSingle().Subject;
        File.ReadAllText(evidence).Should().Be("{ damaged");
        host.GetSnapshot().DeviceUsage!.Problem.Should().BeNull();
        host.PauseOf(a).Should().BeNull();
    }
}
