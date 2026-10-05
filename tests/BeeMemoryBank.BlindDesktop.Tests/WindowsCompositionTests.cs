using System.Buffers.Text;
using System.Text;
using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// The real composition of the Windows app without a window: <c>AddBlindAppCore</c> over the real Windows adapters (DPAPI, the atomic state
/// file, the paths) in a scratch folder, with the host's lifecycle and scheduler. Only autostart is
/// replaced (the real Run key is not touched here).
/// </summary>
public sealed class WindowsCompositionTests
{
    private sealed class TestPlatform(IBlindDesktopPlatform inner) : IBlindDesktopPlatform
    {
        public string Name => inner.Name;
        public IBlindPaths Paths => inner.Paths;
        public IInstanceGuard? TryAcquireInstance() => inner.TryAcquireInstance();
        public bool SignalRunningInstance() => inner.SignalRunningInstance();

        public void AddSeams(IServiceCollection services)
        {
            inner.AddSeams(services);
            services.AddSingleton<IBlindAutostart>(new FakeAutostart());
        }
    }

    private sealed class Session(string root) : IAsyncDisposable
    {
        public string Root { get; } = root;
        public int ReturnedToFirstRun;
        public BlindDesktopRuntime Runtime { get; private set; } = null!;
        public TestPlatform Platform { get; } = new(PlatformSelector.Create(root));

        public async Task<Session> StartAsync()
        {
            Runtime = BlindDesktopRuntime.Create(Platform, () => null, () => Interlocked.Increment(ref ReturnedToFirstRun),
                displayName: () => "Test Desktop");
            await Runtime.StartAsync();
            return this;
        }

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private static async Task<Session> NewSession(string? root = null) => await new Session(root ?? TestFolders.New("compose")).StartAsync();

    private static IEnumerable<string> AllFilesOutsideSecrets(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "secrets" + Path.DirectorySeparatorChar));

    [Fact]
    public async Task TheFirstStart_MakesTheIdentity_AndKeepsTheKeysInDpapiBlobsOnly()
    {
        await using var session = await NewSession();
        var status = session.Runtime.App.GetStatus();

        status.StartError.Should().BeNull();
        status.NodeId.Should().NotBeNull();
        status.DisplayName.Should().Be("Test Desktop");
        status.IsPaired.Should().BeFalse();
        status.AwaitingAnswer.Should().BeTrue();
        session.Runtime.App.PairingCode().Should().StartWith("bmb-blind-phone:?");
        session.Runtime.Scheduler.IsRunning.Should().BeTrue();

        var blobs = Directory.GetFiles(Path.Combine(session.Root, "secrets")).Select(Path.GetFileName).Order().ToArray();
        blobs.Should().Equal("backup-key.dpapi", "identity-seed.dpapi", "pairing-secret.dpapi");

        // None of the three secrets, in any of the usual encodings, is in any other file of the app's folder.
        FindSecretsOutsideTheSecretsFolder(session.Root).Should().BeEmpty("the keys live in the DPAPI blobs only");
        var files = AllFilesOutsideSecrets(session.Root).ToList();
        files.Should().Contain(f => f.EndsWith("beememorybank.db")).And.Contain(f => f.EndsWith("state.json"))
            .And.Contain(f => f.EndsWith("blind-log.jsonl"), "the scan looked at the database, the state and the log");
    }

    [Fact]
    public async Task Control_TheLeakScan_FindsASecretThatIsPlantedInAFile()
    {
        await using var session = await NewSession();
        var seed = new BeeMemoryBank.BlindDesktop.Windows.DpapiSecretStore(Path.Combine(session.Root, "secrets")).LoadIdentitySeed()!;

        File.WriteAllText(Path.Combine(session.Root, "planted.txt"), "key=" + Convert.ToBase64String(seed));

        var leaks = FindSecretsOutsideTheSecretsFolder(session.Root);
        leaks.Should().Contain(l => l.StartsWith("seed base64") && l.EndsWith("planted.txt"));
        leaks.Should().OnlyContain(l => l.StartsWith("seed ") && l.EndsWith("planted.txt"), "base64 and base64url may coincide, nothing else is found");
    }

    private static List<string> FindSecretsOutsideTheSecretsFolder(string root)
    {
        var secrets = new BeeMemoryBank.BlindDesktop.Windows.DpapiSecretStore(Path.Combine(root, "secrets"));
        var needles = new List<(string Name, byte[] Bytes)>();
        foreach (var (name, secret) in new[] { ("seed", secrets.LoadIdentitySeed()), ("backup key", secrets.LoadBackupKey()), ("pairing secret", secrets.LoadPairingSecret()) })
        {
            secret.Should().NotBeNull(name);
            needles.Add((name + " raw", secret!));
            needles.Add((name + " base64", Encoding.ASCII.GetBytes(Convert.ToBase64String(secret!))));
            needles.Add((name + " base64url", Encoding.ASCII.GetBytes(Base64Url.EncodeToString(secret!))));
            needles.Add((name + " hex", Encoding.ASCII.GetBytes(Convert.ToHexString(secret!))));
            needles.Add((name + " hex lower", Encoding.ASCII.GetBytes(Convert.ToHexString(secret!).ToLowerInvariant())));
        }

        var found = new List<string>();
        foreach (var file in AllFilesOutsideSecrets(root))
        {
            // The database may be held open by the app (WAL mode); share for reading.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var content = copy.ToArray();
            foreach (var (name, needle) in needles)
                if (content.AsSpan().IndexOf(needle) >= 0) found.Add($"{name} in {Path.GetFileName(file)}");
        }
        return found;
    }

    [Fact]
    public async Task TheWholeRootBelongsToTheBlindApp_NothingIsWrittenAnywhereElse()
    {
        var root = TestFolders.New("compose");
        await using (await NewSession(root))
        {
        }

        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Should().OnlyContain(f => f.StartsWith(root + Path.DirectorySeparatorChar));
        Path.GetFileName(PlatformSelector.Create().Paths.DataDirectory).Should().Be("BeeMemoryBankBlind", "the default root is the blind app's own folder");
    }

    [Fact]
    public async Task ARestart_KeepsTheIdentity_AndTheSamePairingCode()
    {
        var root = TestFolders.New("compose");
        Guid? nodeId;
        string? code;
        await using (var first = await NewSession(root))
        {
            nodeId = first.Runtime.App.GetStatus().NodeId;
            code = first.Runtime.App.PairingCode();
        }

        await using var second = await NewSession(root);

        second.Runtime.App.GetStatus().NodeId.Should().Be(nodeId);
        second.Runtime.App.GetStatus().StartError.Should().BeNull();
        second.Runtime.App.PairingCode().Should().Be(code);
    }

    [Fact]
    public async Task TheComputersAnswer_IsAcceptedThroughTheRealPairing_AndTheSecretIsSpent()
    {
        await using var session = await NewSession();
        var app = session.Runtime.App;
        BlindPhoneCode.TryParse(app.PairingCode(), out var phone).Should().BeTrue();

        app.AcceptCallCode("bmb-blind-call:?nonsense").Should().Contain("not a connection code");
        var answer = Answer(phone!.Secret);
        app.AcceptCallCode(answer).Should().BeNull();

        var status = app.GetStatus();
        status.IsPaired.Should().BeTrue();
        status.Endpoint.Should().Be("https://127.0.0.1:5610");
        status.AwaitingAnswer.Should().BeFalse("the pairing secret was spent");
        File.Exists(Path.Combine(session.Root, "secrets", "pairing-secret.dpapi")).Should().BeFalse();
        File.ReadAllText(Path.Combine(session.Root, "state.json")).Should().Contain("bmb.blind.call_code");
        app.AcceptCallCode(answer).Should().Contain("already paired");
    }

    [Fact]
    public async Task APairedApp_Wiped_LeavesNoKeyNoStateNoData_StopsItsWork_AndStartsFreshNextTime()
    {
        var root = TestFolders.New("compose");
        Guid? firstNode;
        await using (var session = await NewSession(root))
        {
            var app = session.Runtime.App;
            BlindPhoneCode.TryParse(app.PairingCode(), out var phone);
            app.AcceptCallCode(Answer(phone!.Secret)).Should().BeNull();
            firstNode = app.GetStatus().NodeId;
            session.Runtime.Scheduler.IsRunning.Should().BeTrue();

            await Task.Run(() => app.DisconnectAndWipeAsync());

            session.ReturnedToFirstRun.Should().Be(1, "the host is told to go back to its first-run state");
            session.Runtime.Scheduler.IsRunning.Should().BeFalse("the work was stopped before the files went");
            Directory.Exists(Path.Combine(root, "secrets")).Should().BeFalse("every key blob is removed, and the empty secrets folder with them");
            File.Exists(Path.Combine(root, "state.json")).Should().BeFalse("the state file is removed, not left empty");
            Directory.GetFileSystemEntries(root).Should().BeEmpty("nothing of the blind copy is left in its folder");
            Directory.GetFiles(root, "beememorybank.db*").Should().BeEmpty();
            Directory.GetFiles(root, "blind-log.jsonl").Should().BeEmpty();
            Directory.Exists(Path.Combine(root, "blind-backups")).Should().BeFalse();
            Directory.Exists(Path.Combine(root, "blind-replica")).Should().BeFalse();
        }

        // The first-run state: a new composition over the emptied folder makes a new identity, new keys, and is not paired.
        await using var fresh = await NewSession(root);
        var status = fresh.Runtime.App.GetStatus();
        status.NodeId.Should().NotBeNull();
        status.NodeId!.Value.Should().NotBe(firstNode!.Value, "a wiped copy starts as a new device");
        status.IsPaired.Should().BeFalse();
        status.AwaitingAnswer.Should().BeTrue();
        status.StartError.Should().BeNull();
    }

    [Fact]
    public async Task ALostBackupKey_IsReported_AndNoNewKeyIsMade()
    {
        var root = TestFolders.New("compose");
        await using (await NewSession(root))
        {
        }
        var blob = Path.Combine(root, "secrets", "backup-key.dpapi");
        File.WriteAllBytes(blob, [1, 2, 3, 4]); // a blob that DPAPI cannot open, as after a reset Windows profile
        var before = File.ReadAllBytes(blob);

        await using var session = await NewSession(root);

        session.Runtime.App.GetStatus().BackupKeyLost.Should().BeTrue();
        File.ReadAllBytes(blob).Should().Equal(before, "a missing key is never replaced by a new one over existing data");
    }

    [Fact]
    public async Task LostStateWithIntactKeys_RecoversTheSameIdentity_ButWithoutTheKeysItStopsAndAsksToWipe()
    {
        var root = TestFolders.New("compose");
        Guid? node;
        await using (var first = await NewSession(root))
            node = first.Runtime.App.GetStatus().NodeId;

        // state.json lost (empty), keys intact: the identity is recovered from the database and the keys.
        File.WriteAllText(Path.Combine(root, "state.json"), "{}");
        await using (var recovered = await NewSession(root))
        {
            recovered.Runtime.App.GetStatus().NodeId.Should().Be(node);
            recovered.Runtime.App.GetStatus().StartError.Should().BeNull();
        }

        // state.json lost AND the seed unreadable: stop, keep everything, ask for "Disconnect and wipe".
        File.WriteAllText(Path.Combine(root, "state.json"), "{}");
        var seed = Path.Combine(root, "secrets", "identity-seed.dpapi");
        File.WriteAllBytes(seed, [9, 9, 9]);
        await using var stuck = await NewSession(root);

        stuck.Runtime.App.GetStatus().StartError.Should().Contain("Disconnect and wipe required");
        File.ReadAllBytes(seed).Should().Equal([9, 9, 9], "no replacement seed was written");
        Directory.GetFiles(root, "beememorybank.db").Should().ContainSingle("the database is kept");
    }

    /// <summary>What the computer shows back: a call code authenticated with the phone code's one-time secret.</summary>
    private static string Answer(byte[] secret)
    {
        var key = new byte[32];
        key[0] = 7;
        return BlindCallCode.Create("https://127.0.0.1:5610", Guid.NewGuid(), Base64Url.EncodeToString(new byte[32]), key, secret).ToString();
    }
}
