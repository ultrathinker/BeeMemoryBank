using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Fix round 3 of the limits, the tests that use what only round 3 has (the file system seam of the usage files, the device total over every listed account, the abandoned owner of the
/// device gate ...). Not run against the code before the fix: it does not compile there. (A partial of the round-2 class: it uses that class's pipeline rig.)
/// </summary>
public sealed partial class BlindLimitsFix2Tests
{
    /// <summary>The real file system, with the faults a test asks for.</summary>
    private sealed class ScriptedFiles : IBlindUsageFiles
    {
        private readonly IBlindUsageFiles _inner = BlindUsageFiles.Default;

        public Func<string, bool?>? ExistsAnswer { get; set; }
        public Action<string, byte[]>? OnCreateNew { get; set; }
        public Action<string, byte[]>? OnWrite { get; set; }
        public Func<string, byte[]?, byte[]?>? ReadAnswer { get; set; }

        public byte[]? Read(string path)
        {
            var real = _inner.Read(path);
            return ReadAnswer is null ? real : ReadAnswer(path, real);
        }

        public bool Exists(string path) => ExistsAnswer?.Invoke(path) ?? _inner.Exists(path);

        public void Write(string path, byte[] bytes)
        {
            OnWrite?.Invoke(path, bytes);
            _inner.Write(path, bytes);
        }

        public void CreateNew(string path, byte[] bytes)
        {
            OnCreateNew?.Invoke(path, bytes);
            _inner.CreateNew(path, bytes);
        }

        public IReadOnlyList<string> List(string directory, string searchPattern) => _inner.List(directory, searchPattern);

        public void Delete(string path) => _inner.Delete(path);
    }

    private string DamagedUsageFile(string content = "{ damaged")
    {
        var file = Path.Combine(_dir, "seam", "usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return file;
    }

    // ---- 4. the file system seam ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnExistingNameThatCannotBeOpened_CountsAsTaken_ForTheKeptAsideCopy()
    {
        var clock = new FakeClock(Noon);
        var file = DamagedUsageFile();
        var first = file + ".damaged-20261021T120000Z";
        var files = new ScriptedFiles
        {
            ExistsAnswer = path => path == first ? true : null, // there, and denied to us
            OnCreateNew = (path, _) => { if (path == first) throw new IOException("exists"); },
        };
        var ledger = new BlindUsageLedger(file, clock, files);

        ledger.StartNewLedger().Should().BeNull();

        File.Exists(first).Should().BeFalse("the name that could not be opened was not used, and not written over");
        File.ReadAllText(first + "-1").Should().Be("{ damaged");
    }

    [Fact]
    public void ANameThatIsTakenBetweenTheProbeAndTheCreate_GoesToTheNextName()
    {
        var clock = new FakeClock(Noon);
        var file = DamagedUsageFile();
        var first = file + ".damaged-20261021T120000Z";
        var taken = false;
        var files = new ScriptedFiles
        {
            ExistsAnswer = path => path == first ? taken : null,
            OnCreateNew = (path, _) =>
            {
                if (path != first) return;
                taken = true; // somebody else created it a moment ago
                throw new IOException("exists");
            },
        };

        new BlindUsageLedger(file, clock, files).StartNewLedger().Should().BeNull();

        File.ReadAllText(first + "-1").Should().Be("{ damaged");
    }

    [Fact]
    public void TheProbeFindsAFileByOpeningIt_SoOneThatIsHeldOrIsAFolderIsThere()
    {
        var folder = Path.Combine(_dir, "probe");
        Directory.CreateDirectory(Path.Combine(folder, "a-folder"));
        var held = Path.Combine(folder, "held");
        File.WriteAllText(held, "x");
        var plain = Path.Combine(folder, "plain");
        File.WriteAllText(plain, "x");

        using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            BlindUsageFiles.Default.Exists(held).Should().BeTrue("held by someone: it cannot be opened, and it is there");
        }
        BlindUsageFiles.Default.Exists(Path.Combine(folder, "a-folder")).Should().BeTrue("a folder takes the name");
        BlindUsageFiles.Default.Exists(plain).Should().BeTrue();
        BlindUsageFiles.Default.Exists(Path.Combine(folder, "nothing")).Should().BeFalse();
    }

    [Fact]
    public void ADiskThatIsFullWhileTheEvidenceIsCopied_RefusesTheRepair_AndChangesNothing()
    {
        var clock = new FakeClock(Noon);
        var file = DamagedUsageFile();
        var files = new ScriptedFiles { OnCreateNew = (_, _) => throw new IOException("There is not enough space on the disk.") };
        var ledger = new BlindUsageLedger(file, clock, files);

        var problem = ledger.StartNewLedger();

        problem.Should().Contain("kept aside").And.Contain("nothing was changed");
        ledger.IsAvailable.Should().BeFalse();
        File.ReadAllText(file).Should().Be("{ damaged");
        Evidence(file).Should().BeEmpty();
    }

    [Fact]
    public void ANewFileThatDoesNotReadBackAsWritten_IsNoAvailableLedger()
    {
        var clock = new FakeClock(Noon);
        var file = DamagedUsageFile();
        var written = false;
        var files = new ScriptedFiles
        {
            OnWrite = (_, _) => written = true,
            ReadAnswer = (path, real) => written && path == file ? [1, 2, 3] : real, // what is on the disk is not what was written
        };
        var ledger = new BlindUsageLedger(file, clock, files);

        var problem = ledger.StartNewLedger();

        problem.Should().Contain("could not be written");
        ledger.IsAvailable.Should().BeFalse("a commit that was not verified is no commit");
    }

    [Fact]
    public void ADeviceDiskThatIsFullWhileTheEvidenceIsCopied_RefusesTheRepair()
    {
        var clock = new FakeClock(Noon);
        var stateFile = Path.Combine(_dir, "seam-device", "device-usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        File.WriteAllText(stateFile, "{ damaged");
        var files = new ScriptedFiles { OnCreateNew = (_, _) => throw new IOException("There is not enough space on the disk.") };
        var device = new BlindDeviceUsage(clock, stateFile, null, files);

        var problem = device.StartNewHighWater();

        problem.Should().Contain("kept aside");
        device.IsAvailable.Should().BeFalse();
        File.ReadAllText(stateFile).Should().Be("{ damaged");
    }

    [Fact]
    public void ASaveThatFailsOnTheSeam_IsUnavailableAndNotSavedTwiceInAHurry()
    {
        var clock = new FakeClock(Noon);
        var file = Path.Combine(_dir, "seam", "usage.json");
        var attempts = 0;
        var files = new ScriptedFiles { OnWrite = (_, _) => { attempts++; throw new IOException("There is not enough space on the disk."); } };
        var ledger = new BlindUsageLedger(file, clock, files);
        ledger.AddRequest();

        ledger.Flush();
        var afterFirst = attempts;
        for (var i = 0; i < 20; i++) ledger.AddRequest();
        _ = ledger.IsAvailable;

        ledger.IsAvailable.Should().BeFalse();
        attempts.Should().Be(afterFirst, "a save that failed is tried again after its wait, not at every count");
        clock.Advance(BlindUsageLedger.RetryEvery + TimeSpan.FromSeconds(1));
        _ = ledger.IsAvailable;
        attempts.Should().Be(afterFirst + 1);
    }
}

/// <summary>The device gate records who holds it, and whether that job was let go of (fix round 3).</summary>
public sealed class BlindDeviceGateFix3Tests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheGateRecordsItsHolder_AndSince_AndGivesItUpWithTheLease()
    {
        var clock = new FakeClock(Noon);
        var gate = new BlindDeviceGate();
        gate.UseClock(clock);
        var name = "Friend";

        using (var lease = gate.TryEnter("abc", () => name))
        {
            lease.Should().NotBeNull();
            gate.IsBusy.Should().BeTrue();
            gate.TryEnter("other", null).Should().BeNull("one long job at a time");
            var holder = gate.CurrentHolder!;
            holder.OwnerId.Should().Be("abc");
            holder.OwnerName.Should().Be("Friend");
            holder.Since.Should().Be(Noon);
            holder.Abandoned.Should().BeFalse();
            name = "Renamed";
            gate.CurrentHolder!.OwnerName.Should().Be("Renamed", "the name is read when it is asked for");
        }

        gate.IsBusy.Should().BeFalse();
        gate.CurrentHolder.Should().BeNull();
    }

    [Fact]
    public void OnlyTheHolderCanBeLetGoOf_AndTheMarkEndsWithTheJob()
    {
        var clock = new FakeClock(Noon);
        var gate = new BlindDeviceGate();
        gate.UseClock(clock);
        var lease = gate.TryEnter("abc", null)!;

        gate.Abandon("somebody-else").Should().BeFalse("that account does not hold the gate");
        clock.Advance(TimeSpan.FromMinutes(3));
        gate.Abandon("abc").Should().BeTrue();
        clock.Advance(TimeSpan.FromMinutes(3));
        gate.Abandon("abc").Should().BeTrue("again is the same mark, not a new one");

        var holder = gate.CurrentHolder!;
        holder.Abandoned.Should().BeTrue();
        holder.AbandonedSince.Should().Be(Noon.AddMinutes(3), "the first time is the time");
        gate.IsBusy.Should().BeTrue("a thread cannot be killed: the gate is still held");

        lease.Dispose();
        lease.Dispose();

        gate.IsBusy.Should().BeFalse();
        gate.CurrentHolder.Should().BeNull();
        using var next = gate.TryEnter("abc", null);
        next.Should().NotBeNull();
        gate.CurrentHolder!.Abandoned.Should().BeFalse("the next job starts clean");
    }

    [Fact]
    public void ALateDisposeOfAnOldLease_DoesNotClearANewHolder()
    {
        var gate = new BlindDeviceGate();
        var first = gate.TryEnter("one", null)!;
        first.Dispose();
        using var second = gate.TryEnter("two", null);

        first.Dispose();

        gate.CurrentHolder!.OwnerId.Should().Be("two");
        gate.IsBusy.Should().BeTrue();
    }
}


/// <summary>The hooks that tell the stored-bytes meter about a persisted batch, in the composition the account host makes.</summary>
public sealed class BlindLimitsFix3HostTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly AccountRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private sealed class PhaseRecorder : IBlindReplicaSource
    {
        public Func<bool>? StaleProbe { get; set; }
        public List<string> Seen { get; } = [];

        public Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            Seen.Add("load, stale at the start: " + StaleProbe!());
            return Task.CompletedTask;
        }

        public Task<bool> UpdateBaselineAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            Seen.Add("update, stale at the start: " + StaleProbe!());
            return Task.FromResult(false);
        }
    }

    [Fact]
    public async Task EveryPlaceThatPersistsABatch_IsWiredToTheMeter()
    {
        await using var host = _rig.Host(time: new FakeClock(Noon));
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var services = host.ServicesFor(a);
        var stored = services.GetRequiredService<BlindStoredBytes>();
        stored.Refresh();

        foreach (var (name, invoke) in new (string, Action?)[]
        {
            ("the pull client (a blob, a hundred events, a page)", services.GetRequiredService<BlindPhonePullClient>().AfterBatch),
            ("the replica client (the archive, the extraction, the install, a blob)", services.GetRequiredService<BlindPhoneReplicaClient>().AfterBatch),
            ("the backup runner (the package, the encrypted file)", services.GetRequiredService<BlindPhoneBackupRunner>().AfterStep),
        })
        {
            invoke.Should().NotBeNull(name);
            stored.Refresh();
            stored.IsStale.Should().BeFalse();

            invoke!();

            stored.IsStale.Should().BeTrue(name + " tells the meter, and the next admission walks again");
        }
    }

    [Fact]
    public async Task ALongJob_MarksTheFoldersStale_AfterEachPhase_SoThatTheNextPhaseAndTheNextAccountSeeWhatItWrote()
    {
        var hub = new FakeBlindHub("hub-a.test");
        var recorder = new PhaseRecorder();
        await using var host = _rig.Host(time: new FakeClock(Noon), overrides: (services, _) => services.AddSingleton<IBlindReplicaSource>(recorder));
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        await host.PairAsync(_rig, a, hub, loaded: false);
        recorder.StaleProbe = () => host.ServicesFor(a).GetRequiredService<BlindStoredBytes>().IsStale;
        host.For(a).SetSchedule(BlindBackupSchedule.Off);

        await host.For(a).RunHeavyAsync(false);

        recorder.Seen.Should().Equal("load, stale at the start: False", "update, stale at the start: True");
    }
}
