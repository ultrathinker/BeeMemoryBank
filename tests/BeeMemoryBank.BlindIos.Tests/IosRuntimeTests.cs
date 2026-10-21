using System.Security.Cryptography;
using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>
/// The iPhone app's composition with fakes for what is iOS's own (Keychain, BGTaskScheduler, NWPath, file attributes): an account of its own per source with its own Keychain items, folder and
/// state; the 2.6 install adopted as account 1 with nothing moved; removal that really deletes keys, state and files; one app that is never rebuilt when accounts come and go.
/// </summary>
public sealed class IosRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-ios-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly IosKeychainSecretStoreTests.FakeKeychain _keychain = new();
    private readonly FakeBackground _background = new();
    private readonly FakeFileProtection _files = new();
    private readonly FakeConditions _conditions = new();
    private readonly RecordingHandler _network = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private IosBlindPaths Paths => new(_root);

    private IosBlindRuntimeOptions Options() => new(
        Paths, new IosKeychainSecretStoreFactory(_keychain), _background, _conditions, _files, DisplayName: () => "iPhone")
    {
        // No real network, and no courtesy call to a source on removal.
        PrimaryHandlerFactory = _ => () => new Forward(_network),
        LeaveHook = () => null,
    };

    private async Task<IosBlindRuntime> StartedAsync()
    {
        var runtime = IosBlindRuntime.Create(Options());
        await runtime.StartAsync();
        return runtime;
    }

    private static string FolderOf(IosBlindPaths paths, string id) => Path.Combine(paths.DataDirectory, "accounts", id);

    [Fact]
    public async Task AFirstStart_HoldsNoAccount_AndMakesNoKeyAndNoDatabase()
    {
        await using var runtime = await StartedAsync();

        var snapshot = runtime.Accounts.GetSnapshot();
        snapshot.Accounts.Should().BeEmpty();
        snapshot.RegistryProblem.Should().BeNull();
        snapshot.CanAdd.Should().BeTrue();
        _keychain.Items.Should().BeEmpty("a key is made when an account is, never before");
        File.Exists(Path.Combine(Paths.DataDirectory, "accounts.json")).Should().BeTrue();
        File.Exists(Paths.DatabasePath).Should().BeFalse();
    }

    [Fact]
    public async Task AnAccountAddedTheOldWay_MakesItsIdentity_KeepsItsSecretsInTheKeychainUnderItsId_AndItsStateInItsFolder()
    {
        await using var runtime = await StartedAsync();

        var added = await runtime.Accounts.AddAsync("Anna");

        added.Ok.Should().BeTrue();
        var id = added.AccountId!;
        var app = runtime.Accounts.For(id);
        var status = app.GetStatus();
        status.NodeId.Should().NotBeNull();
        BlindNodeId.IsBlind(status.NodeId!.Value).Should().BeTrue();
        status.DisplayName.Should().Be("iPhone");
        status.IsPaired.Should().BeFalse();
        app.PairingCode().Should().StartWith("bmb-blind-phone:");

        _keychain.Items.Keys.Should().BeEquivalentTo(id + "/identity-seed-v1", id + "/backup-key-v1", id + "/pairing-secret-v1");
        var folder = FolderOf(Paths, id);
        File.Exists(Path.Combine(folder, "beememorybank.db")).Should().BeTrue();
        File.Exists(Path.Combine(folder, MacOsBlindStateStore.FileName)).Should().BeTrue();
        File.Exists(Paths.DatabasePath).Should().BeFalse("account 1's files are not made for a new account");
        // Nothing secret in the state file.
        var seed = _keychain.Items[id + "/identity-seed-v1"][33..];
        File.ReadAllText(Path.Combine(folder, MacOsBlindStateStore.FileName))
            .Should().NotContain(Convert.ToBase64String(seed)).And.NotContain(System.Buffers.Text.Base64Url.EncodeToString(seed));
    }

    [Fact]
    public async Task TheComputersAnswer_PairsTheAccountItWasMadeFor_ThroughTheHost_AndTheStateSurvivesARestartOfTheApp()
    {
        string a, b;
        Guid nodeOfA;
        await using (var runtime = await StartedAsync())
        {
            a = (await runtime.Accounts.AddAsync("Anna")).AccountId!;
            b = (await runtime.Accounts.AddAsync("Ben")).AccountId!;
            nodeOfA = runtime.Accounts.For(a).GetStatus().NodeId!.Value;

            runtime.Accounts.AcceptCallCode(AnswerFor(runtime.Accounts.For(a).PairingCode()!)).Should().BeNull();

            runtime.Accounts.For(a).GetStatus().IsPaired.Should().BeTrue();
            runtime.Accounts.For(b).GetStatus().IsPaired.Should().BeFalse("the answer landed in the account whose pairing secret made it");
            runtime.Accounts.For(a).PairingCode().Should().BeNull("the one-time secret is spent");
            runtime.Accounts.For(b).PairingCode().Should().StartWith("bmb-blind-phone:");
        }
        SqliteConnection.ClearAllPools();

        await using var again = await StartedAsync();
        var status = again.Accounts.For(a).GetStatus();
        status.NodeId.Should().Be(nodeOfA);
        status.IsPaired.Should().BeTrue();
        status.Endpoint.Should().Be("https://192.0.2.10:5610");
        status.StartError.Should().BeNull();
        again.Accounts.For(b).GetStatus().IsPaired.Should().BeFalse();
    }

    [Fact]
    public async Task AnAnswerMadeForAnotherPhone_IsRefused_AndNoAccountIsTouched()
    {
        await using var runtime = await StartedAsync();
        var id = (await runtime.Accounts.AddAsync("Anna")).AccountId!;
        var other = BlindCallCode.Create("https://192.0.2.10:5610", BlindNodeId.NewId(), SpkiPinText(), Ed25519Signer.GenerateKeyPair().publicKey, BlindPairingSecret.New());

        runtime.Accounts.AcceptCallCode(other.ToString()).Should().NotBeNull();

        runtime.Accounts.For(id).GetStatus().IsPaired.Should().BeFalse();
    }

    [Fact]
    public async Task TwoAccounts_ShareNoFolder_NoKeychainItem_AndNoIdentity()
    {
        await using var runtime = await StartedAsync();
        var a = (await runtime.Accounts.AddAsync("Anna")).AccountId!;
        var b = (await runtime.Accounts.AddAsync("Ben")).AccountId!;

        var nodeA = runtime.Accounts.For(a).GetStatus().NodeId!.Value;
        var nodeB = runtime.Accounts.For(b).GetStatus().NodeId!.Value;
        nodeA.Should().NotBe(nodeB, "every source mesh sees an ordinary new blind copy");
        _keychain.Items[a + "/identity-seed-v1"].Should().NotEqual(_keychain.Items[b + "/identity-seed-v1"]);
        _keychain.Items.Keys.Should().HaveCount(6);

        // The canary: the node id of one account is nowhere in the other's folder.
        foreach (var (folder, foreign) in new[] { (FolderOf(Paths, a), nodeB), (FolderOf(Paths, b), nodeA) })
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".db", StringComparison.Ordinal) && !f.Contains("-wal") && !f.Contains("-shm")))
                File.ReadAllText(file).Should().NotContain(foreign.ToString("D"), file);
    }

    [Fact]
    public async Task RemovingAnAccount_DeletesItsKeychainItemsItsFolderAndItsState_AndLeavesTheOthersAlone()
    {
        await using var runtime = await StartedAsync();
        var a = (await runtime.Accounts.AddAsync("Anna")).AccountId!;
        var b = (await runtime.Accounts.AddAsync("Ben")).AccountId!;
        var controllerOfB = runtime.Accounts.For(b);

        await runtime.Accounts.RemoveAsync(a);

        _keychain.Items.Keys.Should().BeEquivalentTo(b + "/identity-seed-v1", b + "/backup-key-v1", b + "/pairing-secret-v1");
        Directory.Exists(FolderOf(Paths, a)).Should().BeFalse();
        Directory.Exists(FolderOf(Paths, b)).Should().BeTrue();
        runtime.Accounts.GetSnapshot().Accounts.Select(x => x.Entry.Id).Should().Equal(b);
        var gone = () => runtime.Accounts.For(a);
        gone.Should().Throw<KeyNotFoundException>();
        runtime.Accounts.For(b).Should().BeSameAs(controllerOfB, "removing one account does not rebuild another");
        controllerOfB.GetStatus().NodeId.Should().NotBeNull();
    }

    [Fact]
    public async Task AddingAndRemovingAccounts_NeverRebuildsTheApp_TheSchedulerOrTheOtherAccounts()
    {
        await using var runtime = await StartedAsync();
        var scheduler = runtime.Scheduler;
        var host = runtime.Host;
        var a = (await runtime.Accounts.AddAsync("Anna")).AccountId!;
        var controllerOfA = runtime.Accounts.For(a);
        var servicesOfA = runtime.Accounts.ServicesFor(a);

        var b = (await runtime.Accounts.AddAsync("Ben")).AccountId!;
        await runtime.Accounts.RemoveAsync(b);
        var c = (await runtime.Accounts.AddAsync("Cleo")).AccountId!;

        runtime.Scheduler.Should().BeSameAs(scheduler);
        runtime.Host.Should().BeSameAs(host);
        runtime.Accounts.For(a).Should().BeSameAs(controllerOfA);
        runtime.Accounts.ServicesFor(a).Should().BeSameAs(servicesOfA);
        runtime.Accounts.GetSnapshot().Accounts.Select(x => x.Entry.Id).Should().Equal(a, c);
    }

    [Fact]
    public async Task DisconnectAndWipeOfOneAccount_MakesItAFreshAccountInPlace_AndTheOthersStayAsTheyWere()
    {
        await using var runtime = await StartedAsync();
        var a = (await runtime.Accounts.AddAsync("Anna")).AccountId!;
        var b = (await runtime.Accounts.AddAsync("Ben")).AccountId!;
        runtime.Accounts.AcceptCallCode(AnswerFor(runtime.Accounts.For(a).PairingCode()!)).Should().BeNull();
        runtime.Accounts.AcceptCallCode(AnswerFor(runtime.Accounts.For(b).PairingCode()!)).Should().BeNull();
        var oldNode = runtime.Accounts.For(a).GetStatus().NodeId!.Value;
        var controllerOfB = runtime.Accounts.For(b);
        var nodeOfB = controllerOfB.GetStatus().NodeId!.Value;

        await runtime.Accounts.For(a).DisconnectAndWipeAsync();

        await EventuallyAsync(() =>
        {
            try { return runtime.Accounts.For(a).GetStatus() is { NodeId: { } fresh } && fresh != oldNode; }
            catch (KeyNotFoundException) { return false; }
        });
        var status = runtime.Accounts.For(a).GetStatus();
        status.IsPaired.Should().BeFalse();
        runtime.Accounts.For(a).PairingCode().Should().StartWith("bmb-blind-phone:");
        _keychain.Items.Keys.Should().Contain(a + "/identity-seed-v1", "the fresh account has an identity of its own again");
        runtime.Accounts.For(b).Should().BeSameAs(controllerOfB);
        controllerOfB.GetStatus().NodeId.Should().Be(nodeOfB);
        controllerOfB.GetStatus().IsPaired.Should().BeTrue();
    }

    [Fact]
    public async Task EveryAccountFolder_GetsTheFileProtectionAndTheNoBackupMarkOfTheRoot_AlsoAfterAWipe()
    {
        await using var runtime = await StartedAsync();
        var ids = new List<string>();
        for (var i = 0; i < 3; i++) ids.Add((await runtime.Accounts.AddAsync("Account " + i)).AccountId!);

        foreach (var folder in new[] { Paths.DataDirectory, Path.Combine(Paths.DataDirectory, "accounts") }.Concat(ids.Select(id => FolderOf(Paths, id))))
        {
            _files.Protected.Should().Contain(Path.GetFullPath(folder), folder);
            _files.Excluded.Should().Contain(Path.GetFullPath(folder), folder);
        }

        _files.Protected.Clear();
        _files.Excluded.Clear();
        var wiped = runtime.Accounts.For(ids[1]);
        await wiped.DisconnectAndWipeAsync();
        await EventuallyAsync(() => _files.Excluded.Contains(Path.GetFullPath(FolderOf(Paths, ids[1]))));
        _files.Protected.Should().Contain(Path.GetFullPath(FolderOf(Paths, ids[1])), "the account that was set up again after a wipe is marked like the first time");
    }

    [Fact]
    public async Task TheBackgroundRequests_AreAskedForOnlyWhileAnAccountNeedsThem()
    {
        await using var runtime = await StartedAsync();

        runtime.RefreshBackgroundRequests();
        runtime.BackgroundRequestsWanted.Should().BeFalse();
        _background.Cancelled.Should().Be(1, "a phone with no account asks iOS for nothing");
        _background.Submitted.Should().Be(0);

        var id = (await runtime.Accounts.AddAsync("Anna")).AccountId!;
        runtime.RefreshBackgroundRequests();
        runtime.BackgroundRequestsWanted.Should().BeTrue();
        _background.Submitted.Should().Be(1);

        await runtime.Accounts.RemoveAsync(id);
        runtime.RefreshBackgroundRequests();
        runtime.BackgroundRequestsWanted.Should().BeFalse();
        _background.Cancelled.Should().Be(2);
    }

    [Fact]
    public async Task TheMeteredAndBatterySwitches_AskThePhone_AndWaitWhenItCannotTell()
    {
        await using var runtime = await StartedAsync();
        var id = (await runtime.Accounts.AddAsync("Anna")).AccountId!;
        runtime.Accounts.SetSwitches(id, unmeteredOnly: true, pauseOnPowerSaving: true).Should().BeNull();

        _conditions.IsMetered = null;
        _conditions.IsPowerSaving = false;
        runtime.Accounts.PauseOf(id).Should().Match<BlindPause>(p => p.Reason == BlindPauseReasons.MeteredNetwork && p.ConditionUnknown, "fail closed toward the data plan");

        _conditions.IsMetered = true;
        runtime.Accounts.PauseOf(id).Should().Match<BlindPause>(p => p.Reason == BlindPauseReasons.MeteredNetwork && !p.ConditionUnknown);

        _conditions.IsMetered = false;
        runtime.Accounts.PauseOf(id).Should().BeNull();

        _conditions.IsPowerSaving = true;
        runtime.Accounts.PauseOf(id).Should().Match<BlindPause>(p => p.Reason == BlindPauseReasons.PowerSaving);

        runtime.Accounts.SetSwitches(id, unmeteredOnly: false, pauseOnPowerSaving: false).Should().BeNull();
        _conditions.IsMetered = true;
        runtime.Accounts.PauseOf(id).Should().BeNull("with the switches off the phone is never asked");
    }

    // ---- the upgrade from 2.6.4 ----------------------------------------------------------------------------------------------------------

    /// <summary>Makes the install a 2.6.4 phone had: ONE copy composed the old way (the root is the copy, the legacy Keychain names, <c>blind-state.json</c>), paired and loaded.</summary>
    private async Task<(Guid NodeId, string Endpoint)> Make264InstallAsync()
    {
        var paths = Paths;
        Directory.CreateDirectory(paths.DataDirectory);
        var services = new ServiceCollection();
        services.AddSingleton<IBlindPaths>(paths);
        services.AddSingleton<IBlindSecretStore>(new IosKeychainSecretStore(_keychain));
        services.AddSingleton<IBlindStateStore>(new MacOsBlindStateStore(Path.Combine(paths.DataDirectory, MacOsBlindStateStore.FileName)));
        services.AddSingleton<IBlindLifecycle>(new QuietLifecycle());
        BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(paths.DataDirectory, paths.DatabasePath, null, () => "iPhone")
        {
            PrimaryHandlerFactory = () => new Forward(_network),
        });
        await using var provider = services.BuildServiceProvider();
        var app = provider.GetRequiredService<IBlindAppController>();
        await app.InitializeAsync();
        app.AcceptCallCode(AnswerFor(app.PairingCode()!)).Should().BeNull();
        var state = new BlindPhoneState(provider.GetRequiredService<IBlindStateStore>()) { InitialLoadDone = true, LastSyncAt = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero) };
        var status = app.GetStatus();
        state.CallCode.Should().NotBeNull();
        return (status.NodeId!.Value, status.Endpoint!);
    }

    private sealed class QuietLifecycle : IBlindLifecycle
    {
        public void StopBackgroundWork() { }
        public void StopBackupService() { }
        public void RestartAfterWipe() { }
    }

    [Fact]
    public async Task A264Install_BecomesExactlyOneAccount1_WithNothingMoved_NoKeyRenamed_AndKeepsSyncingWithItsSource()
    {
        var (nodeId, endpoint) = await Make264InstallAsync();
        SqliteConnection.ClearAllPools();
        var root = Paths.DataDirectory;
        var before = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).Order(StringComparer.Ordinal).ToList();
        before.Should().Contain(["beememorybank.db", "blind-state.json"]);
        var keychainBefore = _keychain.Items.ToDictionary(p => p.Key, p => p.Value.ToArray());
        keychainBefore.Keys.Should().BeEquivalentTo(["identity-seed-v1", "backup-key-v1"], "the pairing secret was spent when the computer's answer was accepted");
        var stateBefore = File.ReadAllBytes(Path.Combine(root, "blind-state.json"));

        await using var runtime = await StartedAsync();

        // exactly one account: the 2.6 install, listed where it is
        var cards = runtime.Accounts.GetSnapshot().Accounts;
        cards.Should().ContainSingle();
        var card = cards[0];
        card.Entry.Id.Should().Be("legacy");
        card.Entry.Layout.Should().Be(BlindAccountLayouts.Root);
        card.Entry.Limits.Should().BeNull("account 1 keeps 2.6 behaviour: no limits");
        card.Entry.UnmeteredOnly.Should().BeFalse("any network, as before");
        card.Entry.PauseOnPowerSaving.Should().BeFalse();
        card.Status!.NodeId.Should().Be(nodeId);
        card.Status.IsPaired.Should().BeTrue();
        card.Status.InitialLoadDone.Should().BeTrue();
        card.Status.Endpoint.Should().Be(endpoint);
        card.Status.StartError.Should().BeNull();

        // nothing renamed or re-keyed
        _keychain.Items.Keys.Should().BeEquivalentTo(keychainBefore.Keys);
        foreach (var (name, bytes) in keychainBefore) _keychain.Items[name].Should().Equal(bytes, name);

        // nothing moved: every file is where it was, no account folder was made, the state file is the one the old app wrote
        var after = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).ToList();
        after.Should().Contain(before.Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal)));
        Directory.Exists(Path.Combine(root, "accounts")).Should().BeFalse("account 1 lives in the root; no folder is made for it");
        File.ReadAllBytes(Path.Combine(root, "blind-state.json")).Should().Equal(stateBefore, "starting the app rewrites nothing of account 1's state");
        File.Exists(Paths.DatabasePath).Should().BeTrue();

        // and it keeps syncing: the round goes to the source it was paired with, with the identity it had
        var result = await runtime.Accounts.For("legacy").RequestSyncAsync();

        result.Should().StartWith("Sync failed", "the fake source answers 503; what matters is that the round was made");
        _network.Requests.Should().NotBeEmpty();
        _network.Requests.Should().OnlyContain(uri => uri.Host == "192.0.2.10" && uri.Port == 5610);
        runtime.Accounts.For("legacy").GetStatus().NodeId.Should().Be(nodeId);
    }

    [Fact]
    public async Task A264Install_ThenANewAccount_LeavesAccount1Untouched()
    {
        var (nodeId, _) = await Make264InstallAsync();
        SqliteConnection.ClearAllPools();
        await using var runtime = await StartedAsync();
        var legacy = runtime.Accounts.For("legacy");

        var added = await runtime.Accounts.AddAsync("Anna");

        added.Ok.Should().BeTrue();
        runtime.Accounts.For("legacy").Should().BeSameAs(legacy);
        legacy.GetStatus().NodeId.Should().Be(nodeId);
        legacy.GetStatus().IsPaired.Should().BeTrue();
        _keychain.Items.Keys.Should().Contain(["identity-seed-v1", "backup-key-v1", added.AccountId + "/identity-seed-v1"]);
        runtime.Accounts.GetSnapshot().Accounts.Select(a => a.Entry.Id).Should().Equal("legacy", added.AccountId);

        // removing the new account leaves account 1's keys and files where they are
        await runtime.Accounts.RemoveAsync(added.AccountId!);
        _keychain.Items.Keys.Should().BeEquivalentTo("identity-seed-v1", "backup-key-v1");
        File.Exists(Paths.DatabasePath).Should().BeTrue();
        legacy.GetStatus().IsPaired.Should().BeTrue();
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------------

    private static async Task EventuallyAsync(Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the condition did not come true");
            await Task.Delay(50);
        }
    }

    /// <summary>The computer's "where to call" answer to a phone code, as BlindPhoneEnrollment makes it (the test plays the computer).</summary>
    private static string AnswerFor(string phoneCode)
    {
        BlindPhoneCode.TryParse(phoneCode, out var phone).Should().BeTrue();
        return BlindCallCode.Create("https://192.0.2.10:5610", BlindNodeId.NewId(), SpkiPinText(), Ed25519Signer.GenerateKeyPair().publicKey,
            phone!.Secret.ToArray()).ToString();
    }

    private static string SpkiPinText() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private sealed class FakeBackground : IIosBackgroundRequests
    {
        public int Submitted { get; private set; }
        public int Cancelled { get; private set; }
        public void Submit() => Submitted++;
        public void CancelAll() => Cancelled++;
    }

    private sealed class FakeConditions : IBlindDeviceConditions
    {
        public bool? IsMetered { get; set; }
        public bool? IsPowerSaving { get; set; }
    }

    /// <summary>The source: answers every request with 503 and remembers where it was asked.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<Uri> _requests = [];

        public IReadOnlyList<Uri> Requests
        {
            get { lock (_gate) return _requests.ToList(); }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_gate) _requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }

    /// <summary>The handler an account's pipeline sits on; disposing it (a pin change, a wipe) leaves the shared recorder alone.</summary>
    private sealed class Forward(HttpMessageHandler target) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(target, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _invoker.SendAsync(request, ct);
    }
}
