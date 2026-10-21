using System.Buffers.Text;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindDesktop.Windows;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// The real composition of the Windows app without a window: the account host over the real Windows adapters (DPAPI per account, the atomic state file per account, the paths) in a scratch
/// folder, with the device loop. Only autostart (the real Run key) and the network (each account's pipeline ends in an in-memory hub) are replaced.
/// </summary>
public sealed class WindowsCompositionTests
{
    private sealed class TestPlatform(IBlindDesktopPlatform inner, IBlindDeviceConditions? conditions) : IBlindDesktopPlatform
    {
        public string Name => inner.Name;
        public IBlindPaths Paths => inner.Paths;
        public IBlindSecretStoreFactory SecretStores => inner.SecretStores;
        public IBlindStateStoreFactory StateStores => inner.StateStores;
        public IBlindDeviceConditions DeviceConditions => conditions ?? inner.DeviceConditions;
        public IBlindAutostart Autostart { get; } = new FakeAutostart();
        public IInstanceGuard? TryAcquireInstance() => inner.TryAcquireInstance();
        public bool SignalRunningInstance() => inner.SignalRunningInstance();
    }

    /// <summary>A hub that no account reaches: the pipelines of accounts that were given no hub end here.</summary>
    private sealed class OfflineHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    private sealed class Session(string root, FakeConditions? conditions = null) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<DesktopFakeHub> _next = new();
        public string Root { get; } = root;
        public Dictionary<string, DesktopFakeHub> HubByAccount { get; } = [];
        public BlindDesktopRuntime Runtime { get; private set; } = null!;
        public TestPlatform Platform { get; } = new(PlatformSelector.Create(root), conditions);
        public IBlindAccountsController Accounts => Runtime.Accounts;

        public async Task<Session> StartAsync(BlindDeviceSchedulerOptions? scheduler = null)
        {
            Runtime = BlindDesktopRuntime.Create(Platform, () => null, displayName: () => "Test Desktop", scheduler: scheduler,
                primaryHandler: entry =>
                {
                    lock (_gate)
                    {
                        if (!HubByAccount.TryGetValue(entry.Id, out var hub))
                        {
                            if (!_next.TryDequeue(out hub)) return () => new OfflineHub();
                            HubByAccount[entry.Id] = hub;
                        }
                        return () => hub;
                    }
                });
            await Runtime.StartAsync();
            return this;
        }

        /// <summary>Starts with the account <paramref name="accountId"/> (the 2.6 one, whose id is known before it is listed) calling <paramref name="hub"/>.</summary>
        public async Task<Session> StartWithHubAsync(string accountId, DesktopFakeHub hub)
        {
            lock (_gate) HubByAccount[accountId] = hub;
            return await StartAsync();
        }

        /// <summary>The hub the NEXT account that is made will call (accounts are given hubs in the order they are made; the 2.6 account is given its hub by id).</summary>
        public void NextAccountCalls(DesktopFakeHub hub)
        {
            lock (_gate) _next.Enqueue(hub);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    private static async Task<Session> NewSession(string? root = null, FakeConditions? conditions = null) =>
        await new Session(root ?? TestFolders.New("compose"), conditions).StartAsync();

    private static BlindAccountEntry Entry(Session session, string id) => session.Accounts.GetSnapshot().Accounts.Single(a => a.Entry.Id == id).Entry;

    private static string SecretsFolder(Session session, string id) =>
        Path.Combine(BlindAccountNames.DataDirectory(session.Root, Entry(session, id)), "secrets");

    private static IEnumerable<string> FilesOutsideSecrets(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "secrets" + Path.DirectorySeparatorChar) && Path.GetFileName(f) != "accounts.lock");

    /// <summary>Makes the account paired with <paramref name="hub"/> (the computer's answer is accepted through the real pairing), seeds the hub's whitelist row and marks the first load as done: what a first load leaves.</summary>
    private static async Task PairAsync(Session session, string accountId, DesktopFakeHub hub)
    {
        var app = session.Accounts.For(accountId);
        BlindPhoneCode.TryParse(app.PairingCode(), out var phone).Should().BeTrue();
        app.AcceptCallCode(hub.CallCodeFor(phone!.Secret)).Should().BeNull();
        var services = session.Accounts.ServicesFor(accountId);
        await services.GetRequiredService<BlindStartup>().EnsureReadyAsync();
        await services.GetRequiredService<IWhitelistRepository>().CreateAsync(hub.WhitelistRow());
        services.GetRequiredService<BlindPhoneState>().InitialLoadDone = true;
    }

    private static List<string> FolderNames(Session session, string accountId)
    {
        using var connection = session.Accounts.ServicesFor(accountId).GetRequiredService<IDbConnectionFactory>().CreateConnection();
        if (connection.State != ConnectionState.Open) connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM tbl_folder";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    // ---- an empty root ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEmptyRoot_StartsWithNoAccount_MakesNoKey_AndTheLoopRuns()
    {
        await using var session = await NewSession();

        var snapshot = session.Accounts.GetSnapshot();
        snapshot.Accounts.Should().BeEmpty();
        snapshot.RegistryProblem.Should().BeNull();
        snapshot.CanAdd.Should().BeTrue();
        session.Runtime.Scheduler.IsRunning.Should().BeTrue();
        Directory.Exists(Path.Combine(session.Root, "secrets")).Should().BeFalse("no account, no key");
        Directory.Exists(Path.Combine(session.Root, "accounts")).Should().BeFalse();
    }

    // ---- an account added the old way ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAccountAddedWithAPairingCode_HasItsIdentityAndItsKeysInItsOwnFolder_AndNothingInTheRoot()
    {
        await using var session = await NewSession();

        var added = await session.Accounts.AddAsync("Berlin office");

        added.Ok.Should().BeTrue(added.Error);
        var folder = Path.Combine(session.Root, "accounts", added.AccountId!);
        Directory.GetFiles(Path.Combine(folder, "secrets")).Select(Path.GetFileName).Order().Should().Equal("backup-key.dpapi", "identity-seed.dpapi", "pairing-secret.dpapi");
        File.Exists(Path.Combine(folder, "beememorybank.db")).Should().BeTrue();
        File.Exists(Path.Combine(folder, "state.json")).Should().BeTrue();
        Directory.Exists(Path.Combine(session.Root, "secrets")).Should().BeFalse("the root's slot belongs to the 2.6 account, which this device does not have");
        File.Exists(Path.Combine(session.Root, "state.json")).Should().BeFalse();
        var status = session.Accounts.For(added.AccountId!).GetStatus();
        status.StartError.Should().BeNull();
        status.NodeId.Should().NotBeNull();
        status.DisplayName.Should().Be("Test Desktop");
        status.AwaitingAnswer.Should().BeTrue();
        session.Accounts.For(added.AccountId!).PairingCode().Should().StartWith("bmb-blind-phone:?");
    }

    [Fact]
    public async Task TheKeysOfAnAccount_AreInItsDpapiBlobsOnly_NotInAnyOtherFileOfTheRoot()
    {
        await using var session = await NewSession();
        var first = (await session.Accounts.AddAsync("Berlin office")).AccountId!;
        var second = (await session.Accounts.AddAsync("Paris")).AccountId!;

        foreach (var id in new[] { first, second })
            FindSecretsOutsideTheSecretsFolders(session, id).Should().BeEmpty("the keys live in the DPAPI blobs only");
        var files = FilesOutsideSecrets(session.Root).ToList();
        files.Should().Contain(f => f.EndsWith("beememorybank.db")).And.Contain(f => f.EndsWith("state.json")).And.Contain(f => f.EndsWith("accounts.json"))
            .And.Contain(f => f.EndsWith("blind-log.jsonl"), "the scan looked at the databases, the states, the registry and the logs");
    }

    [Fact]
    public async Task Control_TheLeakScan_FindsASecretThatIsPlantedInAFile()
    {
        await using var session = await NewSession();
        var id = (await session.Accounts.AddAsync("Berlin office")).AccountId!;
        var seed = new DpapiSecretStore(SecretsFolder(session, id), Entry(session, id)).LoadIdentitySeed()!;

        File.WriteAllText(Path.Combine(session.Root, "planted.txt"), "key=" + Convert.ToBase64String(seed));

        var leaks = FindSecretsOutsideTheSecretsFolders(session, id);
        leaks.Should().Contain(l => l.StartsWith("seed base64") && l.EndsWith("planted.txt"));
        leaks.Should().OnlyContain(l => l.StartsWith("seed ") && l.EndsWith("planted.txt"), "base64 and base64url may coincide, nothing else is found");
    }

    private static List<string> FindSecretsOutsideTheSecretsFolders(Session session, string id)
    {
        var secrets = new DpapiSecretStore(SecretsFolder(session, id), Entry(session, id));
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
        foreach (var file in FilesOutsideSecrets(session.Root))
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
        await using (var session = await NewSession(root))
            await session.Accounts.AddAsync("Berlin office");

        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Should().OnlyContain(f => f.StartsWith(root + Path.DirectorySeparatorChar));
        Path.GetFileName(PlatformSelector.Create().Paths.DataDirectory).Should().Be("BeeMemoryBankBlind", "the default root is the blind app's own folder");
    }

    // ---- two accounts ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoAccounts_HaveTheirOwnIdentitiesKeysAndPairingCodes()
    {
        await using var session = await NewSession();
        var first = (await session.Accounts.AddAsync("Berlin office")).AccountId!;
        var second = (await session.Accounts.AddAsync("Paris")).AccountId!;

        var a = session.Accounts.For(first).GetStatus();
        var b = session.Accounts.For(second).GetStatus();
        (a.NodeId == b.NodeId).Should().BeFalse("every account makes its own identity");
        session.Accounts.For(first).PairingCode().Should().NotBe(session.Accounts.For(second).PairingCode());
        var seedA = new DpapiSecretStore(SecretsFolder(session, first), Entry(session, first)).LoadIdentitySeed();
        var seedB = new DpapiSecretStore(SecretsFolder(session, second), Entry(session, second)).LoadIdentitySeed();
        seedA.Should().NotBeNull();
        seedB.Should().NotBeNull().And.NotEqual(seedA);
    }

    [Fact]
    public async Task ABlobOfOneAccount_IsProtectedWithThatAccountsEntropy_AndNotWithTheLegacyOnes()
    {
        await using var session = await NewSession();
        var id = (await session.Accounts.AddAsync("Berlin office")).AccountId!;
        var blob = File.ReadAllBytes(Path.Combine(SecretsFolder(session, id), "backup-key.dpapi"));

        var own = Encoding.UTF8.GetBytes($"BeeMemoryBankBlind/v1/{id}/backup-key");
        var legacy = Encoding.UTF8.GetBytes("BeeMemoryBankBlind/v1/backup-key");
        ProtectedData.Unprotect(blob, own, DataProtectionScope.CurrentUser).Should().HaveCount(32);
        var withLegacy = () => ProtectedData.Unprotect(blob, legacy, DataProtectionScope.CurrentUser);
        withLegacy.Should().Throw<CryptographicException>("the 2.6 account's entropy does not open another account's blob");
    }

    [Fact]
    public async Task ARestart_KeepsEveryAccount_WithTheSameIdentitiesAndPairingCodes()
    {
        var root = TestFolders.New("compose");
        string first, second;
        Guid? nodeFirst, nodeSecond;
        string? codeSecond;
        await using (var session = await NewSession(root))
        {
            first = (await session.Accounts.AddAsync("Berlin office")).AccountId!;
            second = (await session.Accounts.AddAsync("Paris")).AccountId!;
            nodeFirst = session.Accounts.For(first).GetStatus().NodeId;
            nodeSecond = session.Accounts.For(second).GetStatus().NodeId;
            codeSecond = session.Accounts.For(second).PairingCode();
        }

        await using var again = await NewSession(root);

        again.Accounts.GetSnapshot().Accounts.Select(a => a.Entry.Name).Should().Equal("Berlin office", "Paris");
        again.Accounts.For(first).GetStatus().NodeId.Should().Be(nodeFirst);
        again.Accounts.For(second).GetStatus().NodeId.Should().Be(nodeSecond);
        again.Accounts.For(second).PairingCode().Should().Be(codeSecond);
        again.Accounts.For(first).GetStatus().StartError.Should().BeNull();
    }

    [Fact]
    public async Task RemovingAnAccount_DeletesItsKeysAndItsFolder_AndLeavesTheOther()
    {
        await using var session = await NewSession();
        var first = (await session.Accounts.AddAsync("Berlin office")).AccountId!;
        var second = (await session.Accounts.AddAsync("Paris")).AccountId!;
        var secondFolder = Path.Combine(session.Root, "accounts", second);
        var keptBlobs = Directory.GetFiles(Path.Combine(secondFolder, "secrets")).Select(f => (f, File.ReadAllBytes(f))).ToList();
        var seedOfFirst = new DpapiSecretStore(SecretsFolder(session, first), Entry(session, first)).LoadIdentitySeed();

        await session.Accounts.RemoveAsync(first);

        Directory.Exists(Path.Combine(session.Root, "accounts", first)).Should().BeFalse("its DPAPI files, its state, its database: the whole folder");
        session.Accounts.GetSnapshot().Accounts.Select(a => a.Entry.Id).Should().Equal(second);
        foreach (var (file, bytes) in keptBlobs) File.ReadAllBytes(file).Should().Equal(bytes, "the other account's keys are untouched");
        session.Accounts.For(second).GetStatus().StartError.Should().BeNull();
        seedOfFirst.Should().NotBeNull();
        File.ReadAllText(Path.Combine(session.Root, "accounts.json")).Should().NotContain(first).And.Contain(second);
    }

    [Fact]
    public async Task ALostBackupKey_IsReportedForThatAccountOnly_AndNoNewKeyIsMade()
    {
        var root = TestFolders.New("compose");
        string first, second;
        await using (var session = await NewSession(root))
        {
            first = (await session.Accounts.AddAsync("Berlin office")).AccountId!;
            second = (await session.Accounts.AddAsync("Paris")).AccountId!;
        }
        var blob = Path.Combine(root, "accounts", first, "secrets", "backup-key.dpapi");
        File.WriteAllBytes(blob, [1, 2, 3, 4]); // a blob that DPAPI cannot open, as after a reset Windows profile
        var before = File.ReadAllBytes(blob);

        await using var again = await NewSession(root);

        again.Accounts.For(first).GetStatus().BackupKeyLost.Should().BeTrue();
        again.Accounts.For(second).GetStatus().BackupKeyLost.Should().BeFalse("the other account's keys are its own");
        File.ReadAllBytes(blob).Should().Equal(before, "a missing key is never replaced by a new one over existing data");
        var card = BlindAccountCard.Build(again.Accounts.GetSnapshot().Accounts.Single(a => a.Entry.Id == first), TimeZoneInfo.Utc, DateTimeOffset.UtcNow);
        card.State.Should().Contain("Keys missing");
    }

    [Fact]
    public async Task EachAccount_GetsTheHostsBackupPicker_AndItsOwnTrayLine()
    {
        await using var session = await NewSession();
        var first = (await session.Accounts.AddAsync("Berlin office")).AccountId!;
        var second = (await session.Accounts.AddAsync("Paris")).AccountId!;

        var pickerOne = session.Accounts.ServicesFor(first).GetService<IBlindBackupExporter>();
        var pickerTwo = session.Accounts.ServicesFor(second).GetService<IBlindBackupExporter>();
        pickerOne.Should().NotBeNull("Save to... needs the host's picker in every account");
        pickerOne.Should().BeSameAs(pickerTwo, "one picker for the window");
        var lineOne = session.Accounts.ServicesFor(first).GetRequiredService<IBlindNotifications>();
        var lineTwo = session.Accounts.ServicesFor(second).GetRequiredService<IBlindNotifications>();
        lineOne.Should().BeSameAs(session.Runtime.Notifications.For(first));
        lineTwo.Should().BeSameAs(session.Runtime.Notifications.For(second));
        lineOne.Should().NotBeSameAs(lineTwo, "a job of one account shows on its own line");

        lineOne.Show("First load", 0.42);
        session.Runtime.Notifications.Line.Should().Be("First load: 42 %");
        session.Runtime.Notifications.For(second).Line.Should().BeNull();
    }

    // ---- the upgrade of a 2.6.4 install -------------------------------------------------------------------------------------------------

    /// <summary>What a 2.6.4 install leaves: the single-account composition over the root, with DPAPI blobs in <c>secrets</c>, <c>state.json</c> and the database in the root, paired with a hub.</summary>
    private static async Task<(Guid NodeId, DesktopFakeHub Hub)> Make264InstallAsync(string root)
    {
        var paths = new WindowsBlindPaths(root);
        var hub = new DesktopFakeHub("legacy-hub.test");
        var services = new ServiceCollection();
        services.AddSingleton<IBlindSecretStore>(new DpapiSecretStore(paths.SecretsDirectory));
        services.AddSingleton<IBlindStateStore>(new AtomicJsonStateStore(paths.StatePath));
        services.AddSingleton<IBlindLifecycle, NoLifecycle>();
        BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(paths.DataDirectory, paths.DatabasePath, null, () => "Old PC"));
        await using (var provider = services.BuildServiceProvider())
        {
            var controller = provider.GetRequiredService<BlindAppController>();
            await controller.InitializeAsync();
            BlindPhoneCode.TryParse(controller.PairingCode(), out var phone).Should().BeTrue();
            controller.AcceptCallCode(hub.CallCodeFor(phone!.Secret)).Should().BeNull();
            provider.GetRequiredService<BlindPhoneState>().InitialLoadDone = true;
            var nodeId = controller.GetStatus().NodeId!.Value;
            SqliteConnection.ClearAllPools();
            return (nodeId, hub);
        }
    }

    private sealed class NoLifecycle : IBlindLifecycle
    {
        public void StopBackgroundWork() { }
        public void StopBackupService() { }
        public void RestartAfterWipe() { }
    }

    private static Dictionary<string, string> Fingerprint(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "accounts.lock" && !f.EndsWith("-wal") && !f.EndsWith("-shm"))
            .ToDictionary(f => Path.GetRelativePath(root, f), HashOf, StringComparer.OrdinalIgnoreCase);

    /// <summary>The hash of a file that a running app may hold open (the database): read with sharing.</summary>
    private static string HashOf(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [Fact]
    public async Task A264Install_BecomesAccountOne_WithNothingMoved_NoKeyNoStateNoNameChanged_AndKeepsSyncing()
    {
        var root = TestFolders.New("upgrade");
        var (nodeId, hub) = await Make264InstallAsync(root);
        var before = Fingerprint(root);
        before.Keys.Should().Contain(["state.json", Path.Combine("secrets", "identity-seed.dpapi"), Path.Combine("secrets", "backup-key.dpapi"), "beememorybank.db"]);
        var statePairs = File.ReadAllText(Path.Combine(root, "state.json"));

        await using var session = await new Session(root).StartWithHubAsync(BlindAccountEntry.LegacyId, hub);

        // Exactly one account, the 2.6 one, adopted where it is.
        var snapshot = session.Accounts.GetSnapshot();
        snapshot.RegistryProblem.Should().BeNull();
        var account = snapshot.Accounts.Should().ContainSingle().Which;
        account.Entry.Id.Should().Be(BlindAccountEntry.LegacyId);
        account.Entry.Layout.Should().Be(BlindAccountLayouts.Root);
        account.Entry.EffectiveLimits.IsEmpty.Should().BeTrue("account 1 after the upgrade has no limits");
        account.Entry.UnmeteredOnly.Should().BeFalse();
        account.Entry.PauseOnPowerSaving.Should().BeFalse();
        account.Status!.NodeId.Should().Be(nodeId, "the same device identity");
        account.Status.IsPaired.Should().BeTrue();
        account.Status.InitialLoadDone.Should().BeTrue();
        account.Status.StartError.Should().BeNull();

        // Nothing was moved, renamed or re-keyed: every file that was there is there with the same bytes (the database and its log may have grown).
        var after = Fingerprint(root);
        foreach (var (file, hash) in before)
        {
            after.Should().ContainKey(file, "nothing is moved or renamed on upgrade");
            if (file.EndsWith(".dpapi") || file == "state.json") after[file].Should().Be(hash, file + " is byte for byte what it was");
        }
        after.Keys.Except(before.Keys).Should().OnlyContain(f => f == "accounts.json" || f == "usage.json" || f == "device-usage.json" || f.StartsWith("beememorybank.db") || f == "blind-log.jsonl" || f.StartsWith("baselines"),
            "the only new things are the list of accounts and the usage counts (and what opening the database and the log make)");
        File.ReadAllText(Path.Combine(root, "state.json")).Should().Be(statePairs);
        Directory.Exists(Path.Combine(root, "accounts")).Should().BeFalse("account 1 stays in the root; there is no accounts folder until a second account is added");

        // The legacy DPAPI blobs still open with the 2.6 entropy through the old constructor.
        new DpapiSecretStore(Path.Combine(root, "secrets")).LoadIdentitySeed().Should().NotBeNull();

        // And it keeps syncing: a round against its hub applies the hub's event, under the same node id.
        hub.AddFolder();
        var services = session.Accounts.ServicesFor(BlindAccountEntry.LegacyId);
        await services.GetRequiredService<BlindStartup>().EnsureReadyAsync();
        await services.GetRequiredService<IWhitelistRepository>().CreateAsync(hub.WhitelistRow());
        var result = await session.Accounts.For(BlindAccountEntry.LegacyId).RequestSyncAsync();
        result.Should().Be("Synced.");
        FolderNames(session, BlindAccountEntry.LegacyId).Should().Contain(hub.Canary);
        hub.Callers().Should().Contain(nodeId);
    }

    [Fact]
    public async Task ASecondAccount_NextToAnUpgradedOne_SyncsFromItsOwnHub_AndTheCanariesNeverCross()
    {
        var root = TestFolders.New("upgrade");
        var (_, hubOne) = await Make264InstallAsync(root);
        var hubTwo = new DesktopFakeHub("second-hub.test");
        await using var session = await new Session(root).StartWithHubAsync(BlindAccountEntry.LegacyId, hubOne);
        session.NextAccountCalls(hubTwo);
        var second = (await session.Accounts.AddAsync("Paris")).AccountId!;
        await PairAsync(session, second, hubTwo);
        var services = session.Accounts.ServicesFor(BlindAccountEntry.LegacyId);
        await services.GetRequiredService<BlindStartup>().EnsureReadyAsync();
        await services.GetRequiredService<IWhitelistRepository>().CreateAsync(hubOne.WhitelistRow());
        hubOne.AddFolder();
        hubTwo.AddFolder();

        // The device loop is running: ask it for a round of each account (its first pass came before the hubs had anything to say).
        session.Runtime.Scheduler.RequestSync(BlindAccountEntry.LegacyId);
        session.Runtime.Scheduler.RequestSync(second);
        for (var i = 0; i < 400 && !(FolderNames(session, BlindAccountEntry.LegacyId).Contains(hubOne.Canary) && FolderNames(session, second).Contains(hubTwo.Canary)); i++)
            await Task.Delay(50);

        FolderNames(session, BlindAccountEntry.LegacyId).Should().Contain(hubOne.Canary).And.NotContain(hubTwo.Canary);
        FolderNames(session, second).Should().Contain(hubTwo.Canary).And.NotContain(hubOne.Canary);
        hubOne.Requests.Should().NotContain(r => r.Contains(hubTwo.Canary));
        hubTwo.Requests.Should().NotContain(r => r.Contains(hubOne.Canary));
        hubOne.Callers().Should().NotIntersectWith(hubTwo.Callers(), "each hub saw only its own account's identity");
        new DpapiSecretStore(Path.Combine(root, "secrets")).LoadIdentitySeed().Should().NotBeNull("account 1's keys are where they always were");
        Directory.Exists(Path.Combine(root, "accounts", second, "secrets")).Should().BeTrue();
    }

    // ---- the two switches ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSwitches_AskTheDeviceConditions_AndAnUnknownAnswerWaits()
    {
        var conditions = new FakeConditions { IsMetered = true, IsPowerSaving = false };
        await using var session = await NewSession(conditions: conditions);
        var id = (await session.Accounts.AddAsync("Berlin office")).AccountId!;
        session.Accounts.SetSwitches(id, unmeteredOnly: true, pauseOnPowerSaving: true).Should().BeNull();

        session.Accounts.PauseOf(id).Should().Match<BlindPause?>(p => p!.Reason == BlindPauseReasons.MeteredNetwork && !p.ConditionUnknown);

        conditions.IsMetered = false;
        session.Accounts.PauseOf(id).Should().BeNull("an unmetered network and no battery saver: nothing holds it");

        conditions.IsPowerSaving = true;
        session.Accounts.PauseOf(id).Should().Match<BlindPause?>(p => p!.Reason == BlindPauseReasons.PowerSaving);

        conditions.IsPowerSaving = false;
        conditions.IsMetered = null;
        session.Accounts.PauseOf(id).Should().Match<BlindPause?>(p => p!.Reason == BlindPauseReasons.MeteredNetwork && p.ConditionUnknown, "unknown with the switch on is a wait");
    }

    [Fact]
    public async Task TheRealWindowsConditions_CanBeReadOnThisMachine_WithoutThrowing()
    {
        await using var session = await NewSession();

        var conditions = session.Platform.Autostart is not null ? session.Platform.DeviceConditions : throw new InvalidOperationException();
        var read = () => (conditions.IsMetered, conditions.IsPowerSaving);

        read.Should().NotThrow("whatever the machine says, even 'cannot tell', is an answer");
        conditions.Should().BeOfType<WindowsDeviceConditions>();
    }
}
