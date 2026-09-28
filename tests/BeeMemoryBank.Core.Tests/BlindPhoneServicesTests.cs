using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The Android blind node's platform-free logic (plan section 10): making the identity, pairing only
/// with a code made for this phone, and backups that wait for Wi-Fi and the charger, resume, and keep a
/// bounded number of files.
/// </summary>
public sealed class BlindPhoneServicesTests : IDisposable
{


    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_bp_" + Guid.NewGuid().ToString("N"));
    private readonly MemoryStore _store = new();
    private readonly MemoryKeys _keys = new();
    private readonly Recorder _recorder = new();
    private readonly Package _package = new();
    private readonly Device _device = new();
    private readonly RecoverySet _recoverySet;
    private readonly SteppingTime _time = new();
    private readonly BlindPhoneState _state;
    private readonly BlindPhoneLog _log;
    private readonly BlindPhonePairing _pairing;
    private readonly BlindPhoneBackupRunner _runner;
    private readonly Replica _replica = new();
    private readonly BlindHeavyWork _heavy;

    public BlindPhoneServicesTests()
    {
        Directory.CreateDirectory(_dir);
        _state = new BlindPhoneState(_store);
        _log = new BlindPhoneLog(Path.Combine(_dir, "log.jsonl"), _time);
        _pairing = new BlindPhonePairing(_state, _keys, _recorder, _log);
        _recoverySet = new RecoverySet(() => _state.NodeId);
        _runner = new BlindPhoneBackupRunner(_state, _keys, _package, _recoverySet, _device, _log,
            Path.Combine(_dir, "backups"), _time);
        _heavy = new BlindHeavyWork(_state, _replica, _runner, _device, _log, Path.Combine(_dir, "replica"), _time);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ─── Identity and pairing ───────────────────────────────────────────────

    [Fact]
    public async Task CreateIdentity_MakesABlindNode_WithKeysOutsideTheState_Once()
    {
        await _pairing.CreateIdentityAsync("Anna's old phone");

        var nodeId = _state.NodeId!.Value;
        BlindNodeId.IsBlind(nodeId).Should().BeTrue("an Android blind copy must carry the blind mark");
        _keys.Seed.Should().HaveCount(32);
        _keys.Secret.Should().HaveCount(BlindPairingSecret.Size);
        _recorder.Recorded.Should().ContainSingle().Which.Should().Be(nodeId);
        _store.Values.Values.Should().NotContain(v => v != null && v.Contains(System.Buffers.Text.Base64Url.EncodeToString(_keys.Secret!)),
            "the pairing secret lives with the keys, not in plain preferences");

        await _pairing.CreateIdentityAsync("another name");
        _state.NodeId.Should().Be(nodeId, "a second call must not replace an identity Windows may already know");
        _recorder.Recorded.Should().HaveCount(1);
    }

    [Fact]
    public async Task PhoneCode_CarriesTheIdentityAndSecret()
    {
        await _pairing.CreateIdentityAsync("Phone");

        BlindPhoneCode.TryParse(_pairing.PhoneCode()!.ToString(), out var code).Should().BeTrue();
        code!.NodeId.Should().Be(_state.NodeId!.Value);
        code.PublicKey.Should().Equal(_state.PublicKey);
        code.Secret.Should().Equal(_keys.Secret);
        code.BackupKey.Should().Equal(_keys.Backup, "Windows seals this key; it must be the one the phone encrypts with");
    }

    [Fact]
    public async Task CallCode_FromTheWindowsThatReadThisPhone_IsAccepted()
    {
        await _pairing.CreateIdentityAsync("Phone");
        var call = BlindCallCode.Create("https://192.0.2.20:5311", Guid.NewGuid(), Pin(), Key(), _keys.Secret!);

        _pairing.AcceptCallCode(call.ToString()).Should().BeNull();
        _pairing.IsPaired.Should().BeTrue();
        _state.CallCode!.Address.Should().Be("https://192.0.2.20:5311");
    }

    [Fact]
    public async Task CallCode_IsAcceptedOnce_TheSecretIsSpent_ReplayAndReplacementAreRefused()
    {
        await _pairing.CreateIdentityAsync("Phone");
        var secret = _keys.Secret!.ToArray();
        var first = BlindCallCode.Create("https://192.0.2.20:5311", Guid.NewGuid(), Pin(), Key(), secret);
        _pairing.AcceptCallCode(first.ToString()).Should().BeNull();

        _keys.Secret.Should().BeNull("the one-time pairing secret is spent by the accepted answer");
        _pairing.PhoneCode().Should().BeNull("a paired phone shows no answerable code");

        _pairing.AcceptCallCode(first.ToString()).Should().Contain("already paired", "a replayed answer is refused");
        // Someone who saw the phone code can still compute valid MACs with the old secret:
        var hijack = BlindCallCode.Create("https://198.51.100.6:5311", Guid.NewGuid(), Pin(), Key(), secret);
        _pairing.AcceptCallCode(hijack.ToString()).Should().NotBeNull("a spent secret must not move the phone elsewhere");
        _state.CallCode!.Address.Should().Be("https://192.0.2.20:5311");
    }

    [Fact]
    public async Task RePair_MakesAFreshSecret_KeepsTheBackupKey_AndOnlyTheNewAnswerMovesThePhone()
    {
        await _pairing.CreateIdentityAsync("Phone");
        var oldSecret = _keys.Secret!.ToArray();
        var backupKey = _keys.Backup!.ToArray();
        _pairing.AcceptCallCode(BlindCallCode.Create("https://192.0.2.20:5311", Guid.NewGuid(), Pin(), Key(), oldSecret).ToString())
            .Should().BeNull();

        _pairing.StartRePair();

        _keys.Secret.Should().NotBeNull().And.NotEqual(oldSecret);
        _keys.Backup.Should().Equal(backupKey, "backups made before re-pairing must stay openable");
        _state.CallCode!.Address.Should().Be("https://192.0.2.20:5311", "the old connection stays until a new answer");
        _pairing.AcceptCallCode(BlindCallCode.Create("https://198.51.100.6:5311", Guid.NewGuid(), Pin(), Key(), oldSecret).ToString())
            .Should().NotBeNull("an answer made with the old secret is not an answer to the new code");

        var fresh = BlindCallCode.Create("https://192.0.2.30:5311", Guid.NewGuid(), Pin(), Key(), _keys.Secret!);
        _pairing.AcceptCallCode(fresh.ToString()).Should().BeNull();
        _state.CallCode!.Address.Should().Be("https://192.0.2.30:5311");
    }

    [Fact]
    public async Task CallCode_MadeWithAnotherSecret_IsRefused_AndTheRefusalChangesNothing()
    {
        await _pairing.CreateIdentityAsync("Phone");
        var forged = BlindCallCode.Create("https://198.51.100.6:5311", Guid.NewGuid(), Pin(), Key(), BlindPairingSecret.New());

        _pairing.AcceptCallCode(forged.ToString()).Should().Contain("not made for this phone");
        _pairing.IsPaired.Should().BeFalse("the phone must never call a server someone else typed in");
        _pairing.AcceptCallCode("hello").Should().NotBeNull();
    }

    // ─── Backups ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Backup_WaitsForWifiAndTheCharger_WithoutTouchingThePackage()
    {
        await _pairing.CreateIdentityAsync("Phone");
        _device.State = _device.State with { Charging = false };

        var outcome = await _runner.RunAsync();

        outcome.Kind.Should().Be(BlindBackupOutcomeKind.Waiting);
        outcome.Message.Should().Contain("charger");
        _package.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Backup_WaitsUntilTheSealedBackupKeyHasArrived_SoEveryFileCanBeRestored()
    {
        await _pairing.CreateIdentityAsync("Phone");
        _recoverySet.HoldsBackupKey = false;

        var outcome = await _runner.RunAsync();

        outcome.Kind.Should().Be(BlindBackupOutcomeKind.Waiting,
            "a file whose recovery set lacks android-backup:<id> could not be opened with the master password");
        outcome.Message.Should().Contain("sealed");
        _package.Calls.Should().Be(0);
        _runner.Backups().Should().BeEmpty();
    }

    [Fact]
    public async Task Backup_OpensWithTheKeyWindowsSealed_AndCarriesTheRecoverySet()
    {
        await _pairing.CreateIdentityAsync("Phone");

        var outcome = await _runner.RunAsync();

        outcome.Kind.Should().Be(BlindBackupOutcomeKind.Done);
        await using var file = File.OpenRead(outcome.FilePath!);
        var header = await AndroidBackupFile.ReadHeaderAsync(file);
        header.NodeId.Should().Be(_state.NodeId!.Value);
        header.BackupKeyName.Should().Be($"android-backup:{_state.NodeId}");
        header.RecoverySet["format"]!.GetValue<string>().Should().Be("bmb-recovery-set-v1");

        file.Position = 0;
        using var plain = new MemoryStream();
        // What Windows seals at pairing: the key derived from the secret the phone code carried.
        await AndroidBackupFile.DecryptAsync(file, _keys.Backup!, plain);
        plain.ToArray().Should().Equal(_package.LastContent);

        _state.LastBackupAt.Should().NotBeNull();
        _state.PendingBackupName.Should().BeNull();
        Directory.GetFiles(_runner.BackupsDirectory).Should().ContainSingle("the package copy and partial files are gone");
    }

    [Fact]
    public async Task AnInterruptedBackup_ContinuesWithTheSamePackage()
    {
        await _pairing.CreateIdentityAsync("Phone");
        _package.Size = AndroidBackupFile.DefaultChunkSize * 3;
        using var cts = new CancellationTokenSource();

        var first = await _runner.RunAsync(new InlineProgress(_ => cts.Cancel()), cts.Token);
        first.Kind.Should().Be(BlindBackupOutcomeKind.Paused);
        _state.PendingBackupName.Should().NotBeNull();

        var second = await _runner.RunAsync();

        second.Kind.Should().Be(BlindBackupOutcomeKind.Done);
        _package.Calls.Should().Be(1, "a resumed backup must back up the package it started with, not a new one");
        await using var file = File.OpenRead(second.FilePath!);
        using var plain = new MemoryStream();
        await AndroidBackupFile.DecryptAsync(file, _keys.Backup!, plain);
        plain.ToArray().Should().Equal(_package.LastContent);
    }

    [Fact]
    public async Task OnlyTheNewestBackupsStay()
    {
        await _pairing.CreateIdentityAsync("Phone");
        for (var i = 0; i < BlindPhoneBackupRunner.KeptBackups + 2; i++)
        {
            _time.Step(TimeSpan.FromHours(1));
            (await _runner.RunAsync()).Kind.Should().Be(BlindBackupOutcomeKind.Done);
        }

        _runner.Backups().Should().HaveCount(BlindPhoneBackupRunner.KeptBackups);
    }

    [Fact]
    public async Task AMissingContractPiece_IsReportedAsNotAvailable_NotAsAFailure()
    {
        await _pairing.CreateIdentityAsync("Phone");
        _package.Pending = true;

        var outcome = await _runner.RunAsync();

        outcome.Kind.Should().Be(BlindBackupOutcomeKind.NotAvailable);
        outcome.Message.Should().Contain("building the blind package");
    }

    // ─── Long jobs: first load, then backups ───────────────────────────────

    [Fact]
    public async Task HeavyWork_BeforePairing_DoesNothing()
    {
        await _pairing.CreateIdentityAsync("Phone");
        (await _heavy.RunAsync(forceBackup: true, CancellationToken.None)).Should().Contain("Not paired");
        _replica.Calls.Should().Be(0);
    }

    [Fact]
    public async Task HeavyWork_LoadsFirst_ThenBacksUp()
    {
        await PairAsync();

        await _heavy.RunAsync(forceBackup: true, CancellationToken.None);

        _replica.Calls.Should().Be(1);
        _state.InitialLoadDone.Should().BeTrue();
        _runner.Backups().Should().ContainSingle();
    }

    [Fact]
    public async Task HeavyWork_FirstLoad_WaitsForWifi()
    {
        await PairAsync();
        _device.State = _device.State with { Unmetered = false };

        (await _heavy.RunAsync(forceBackup: true, CancellationToken.None)).Should().Contain("Wi-Fi");
        _replica.Calls.Should().Be(0, "the first load moves the whole network's data");
        _runner.Backups().Should().BeEmpty();
    }

    [Fact]
    public async Task HeavyWork_UnpluggedMidBackup_Pauses_AndResumesOnTheCharger()
    {
        await PairAsync();
        _state.InitialLoadDone = true;
        _package.Size = AndroidBackupFile.DefaultChunkSize * 4;
        var unplugged = false;
        _heavy.Progress += (_, p) =>
        {
            if (unplugged || p <= 0.3) return;
            unplugged = true;
            _device.State = _device.State with { Charging = false };
        };

        (await _heavy.RunAsync(forceBackup: true, CancellationToken.None)).Should().ContainEquivalentOf("paused");
        _runner.Backups().Should().BeEmpty();
        _log.Latest(5).Should().Contain(e => e.Message.Contains("charger"), "the screen's log says why it stopped");

        _device.State = _device.State with { Charging = true };
        (await _heavy.RunAsync(forceBackup: true, CancellationToken.None)).Should().Be("Backup made.");
        _runner.Backups().Should().ContainSingle();
        _package.Calls.Should().Be(1);
    }

    [Fact]
    public async Task HeavyWork_ReplicaNotAvailableYet_SaysWhatItWaitsFor_AndDoesNotBackUpNothing()
    {
        await PairAsync();
        _replica.Pending = true;

        (await _heavy.RunAsync(forceBackup: true, CancellationToken.None)).Should().Contain("GET /api/blind/replica");
        _state.InitialLoadDone.Should().BeFalse();
        _runner.Backups().Should().BeEmpty();
    }

    [Theory]
    [InlineData(BlindBackupSchedule.Daily, 23, false)]
    [InlineData(BlindBackupSchedule.Daily, 25, true)]
    [InlineData(BlindBackupSchedule.Weekly, 24 * 6, false)]
    [InlineData(BlindBackupSchedule.Weekly, 24 * 7, true)]
    [InlineData(BlindBackupSchedule.Off, 24 * 100, false)]
    public void Schedule_SaysWhenABackupIsDue(BlindBackupSchedule schedule, int hoursSinceLast, bool due)
    {
        var now = DateTimeOffset.UtcNow;
        _state.Schedule = schedule;
        _state.LastBackupAt = now.AddHours(-hoursSinceLast);
        _state.BackupDue(now).Should().Be(due);
    }

    [Fact]
    public void Log_KeepsTheNewestEntries_NewestFirst()
    {
        for (var i = 0; i < BlindPhoneLog.MaxEntries + 20; i++) _log.Add("test", $"entry {i}");

        _log.Latest(1000).Should().HaveCount(BlindPhoneLog.MaxEntries);
        _log.Latest(1).Single().Message.Should().Be($"entry {BlindPhoneLog.MaxEntries + 19}");
    }

    // ─── Fakes ──────────────────────────────────────────────────────────────

    private async Task PairAsync()
    {
        await _pairing.CreateIdentityAsync("Phone");
        _pairing.AcceptCallCode(BlindCallCode.Create("https://192.0.2.20:5311", Guid.NewGuid(), Pin(), Key(), _keys.Secret!).ToString())
            .Should().BeNull();
    }

    private sealed class Replica : IBlindReplicaSource
    {
        public int Calls;
        public bool Pending;
        public Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            if (Pending) throw new BlindFeaturePendingException("the first download of the blind package (GET /api/blind/replica)");
            Calls++;
            progress?.Report(1);
            return Task.CompletedTask;
        }
    }

    private static string Pin() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    private static byte[] Key() => RandomNumberGenerator.GetBytes(32);

    private sealed class MemoryStore : IBlindPhoneStore
    {
        public readonly Dictionary<string, string?> Values = new();
        public string? Get(string key) => Values.GetValueOrDefault(key);
        public void Set(string key, string? value) => Values[key] = value;
    }

    private sealed class MemoryKeys : IBlindPhoneKeys
    {
        public byte[]? Seed, Secret, Backup;
        public void SaveIdentitySeed(byte[] seed) => Seed = seed.ToArray();
        public void SaveBackupKey(byte[] key) => Backup = key.ToArray();
        public byte[]? LoadBackupKey() => Backup?.ToArray();
        public void SavePairingSecret(byte[] secret) => Secret = secret.ToArray();
        public byte[]? LoadPairingSecret() => Secret?.ToArray();
        public void ClearPairingSecret() => Secret = null;
        public void Clear() => Seed = Secret = Backup = null;
    }

    private sealed class Recorder : IBlindIdentityRecorder
    {
        public readonly List<Guid> Recorded = [];
        public Task RecordAsync(Guid nodeId, byte[] publicKey, string displayName, CancellationToken ct)
        {
            Recorded.Add(nodeId);
            return Task.CompletedTask;
        }
    }

    private sealed class Package : IBlindPackageSource
    {
        public int Size = 5000, Calls;
        public bool Pending;
        public byte[] LastContent = [];
        public async Task CreateAsync(string destinationPath, CancellationToken ct)
        {
            if (Pending) throw new BlindFeaturePendingException("building the blind package on the phone");
            Calls++;
            LastContent = RandomNumberGenerator.GetBytes(Size);
            await File.WriteAllBytesAsync(destinationPath, LastContent, ct);
        }
    }

    /// <summary>
    /// The phone's recovery set; by default it already holds the backup key Windows sealed for this
    /// phone (the sealed bytes are irrelevant here — only a restore opens them).
    /// </summary>
    private sealed class RecoverySet(Func<Guid?> nodeId) : IRecoverySetJsonSource
    {
        public bool HoldsBackupKey = true;
        public Task<string> BuildJsonAsync(CancellationToken ct) => Task.FromResult(
            "{\"format\":\"bmb-recovery-set-v1\",\"boxes\":[],\"links\":[],\"anchors\":[],\"sealed_secrets\":["
            + (HoldsBackupKey ? $"{{\"name\":\"android-backup:{nodeId()}\",\"dek_fingerprint\":\"f\",\"wrapped\":\"AA==\",\"iv\":\"AA==\",\"updated_at\":\"x\"}}" : "")
            + "],\"created_at\":\"x\"}");
    }

    private sealed class Device : IDeviceStateProvider
    {
        public BlindPhoneDeviceState State = new(true, true, true, 90);
        public BlindPhoneDeviceState Current() => State;
    }

    private sealed class SteppingTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        public void Step(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
