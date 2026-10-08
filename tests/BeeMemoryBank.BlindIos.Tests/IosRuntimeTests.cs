using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>
/// The iOS composition of the blind copy, with fakes for what is iOS's own (Keychain, BGTaskScheduler): it makes its identity, pairs with
/// the computer's answer, keeps its state across a restart of the app, and "Disconnect and wipe" removes keys, state and files, withdraws
/// the background requests and leaves a new first-run composition in place.
/// </summary>
public sealed class IosRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-ios-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly IosKeychainSecretStoreTests.FakeKeychain _keychain = new();
    private readonly FakeBackground _background = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private IosBlindRuntimeOptions Options() => new(
        new IosBlindPaths(_root), () => new IosKeychainSecretStore(_keychain), _background, DisplayName: () => "iPhone");

    [Fact]
    public async Task AFirstStart_MakesTheIdentity_KeepsTheSecretsInTheKeychain_AndTheStateInTheContainer()
    {
        await using var runtime = IosBlindRuntime.Create(Options(), () => { });
        await runtime.App.InitializeAsync();

        var status = runtime.App.GetStatus();
        status.NodeId.Should().NotBeNull();
        BlindNodeId.IsBlind(status.NodeId!.Value).Should().BeTrue();
        status.DisplayName.Should().Be("iPhone");
        status.IsPaired.Should().BeFalse();
        runtime.App.PairingCode().Should().StartWith("bmb-blind-phone:");

        _keychain.Items.Keys.Should().BeEquivalentTo(
            IosKeychainSecretStore.IdentitySeedAccount, IosKeychainSecretStore.BackupKeyAccount, IosKeychainSecretStore.PairingSecretAccount);
        var paths = new IosBlindPaths(_root);
        File.Exists(paths.DatabasePath).Should().BeTrue();
        File.Exists(Path.Combine(paths.DataDirectory, MacOsBlindStateStore.FileName)).Should().BeTrue();
        // Nothing secret in the state file or the database folder's text files.
        var seed = _keychain.Items[IosKeychainSecretStore.IdentitySeedAccount][33..];
        File.ReadAllText(Path.Combine(paths.DataDirectory, MacOsBlindStateStore.FileName))
            .Should().NotContain(Convert.ToBase64String(seed)).And.NotContain(System.Buffers.Text.Base64Url.EncodeToString(seed));
    }

    [Fact]
    public async Task TheComputersAnswer_PairsTheCopy_AndTheStateSurvivesARestartOfTheApp()
    {
        Guid nodeId;
        await using (var runtime = IosBlindRuntime.Create(Options(), () => { }))
        {
            await runtime.App.InitializeAsync();
            nodeId = runtime.App.GetStatus().NodeId!.Value;
            runtime.App.AcceptCallCode(AnswerFor(runtime.App.PairingCode()!)).Should().BeNull();
            runtime.App.GetStatus().IsPaired.Should().BeTrue();
            runtime.App.PairingCode().Should().BeNull("the one-time secret is spent");
        }
        SqliteConnection.ClearAllPools();

        await using var again = IosBlindRuntime.Create(Options(), () => { });
        await again.App.InitializeAsync();
        var status = again.App.GetStatus();
        status.NodeId.Should().Be(nodeId);
        status.IsPaired.Should().BeTrue();
        status.Endpoint.Should().Be("https://192.0.2.10:5610");
        status.StartError.Should().BeNull();
    }

    [Fact]
    public async Task AnAnswerMadeForAnotherPhone_IsRefused()
    {
        await using var runtime = IosBlindRuntime.Create(Options(), () => { });
        await runtime.App.InitializeAsync();
        var other = BlindCallCode.Create("https://192.0.2.10:5610", BlindNodeId.NewId(), SpkiPinText(), Ed25519Signer.GenerateKeyPair().publicKey,
            BlindPairingSecret.New());

        runtime.App.AcceptCallCode(other.ToString()).Should().Contain("not made for this phone");
        runtime.App.GetStatus().IsPaired.Should().BeFalse();
    }

    [Fact]
    public async Task DisconnectAndWipe_RemovesKeysStateAndFiles_WithdrawsTheBackgroundRequests_AndTheHostStartsAFreshCopy()
    {
        var host = new IosBlindHost(back => IosBlindRuntime.Create(Options(), back), TimeSpan.FromMilliseconds(10));
        var replaced = new TaskCompletionSource();
        host.Replaced += () => replaced.TrySetResult();
        var first = host.Current;
        await first.App.InitializeAsync();
        var oldId = first.App.GetStatus().NodeId;
        first.App.AcceptCallCode(AnswerFor(first.App.PairingCode()!)).Should().BeNull();

        await first.App.DisconnectAndWipeAsync();
        await replaced.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _background.Cancelled.Should().BeGreaterThan(0, "the background requests are withdrawn before anything is deleted");
        _keychain.Items.Should().BeEmpty();
        var paths = new IosBlindPaths(_root);
        File.Exists(paths.DatabasePath).Should().BeFalse();
        File.Exists(Path.Combine(paths.DataDirectory, MacOsBlindStateStore.FileName)).Should().BeFalse();

        host.Current.Should().NotBeSameAs(first);
        await host.Current.App.InitializeAsync();
        var fresh = host.Current.App.GetStatus();
        fresh.NodeId.Should().NotBeNull();
        fresh.NodeId!.Value.Should().NotBe(oldId!.Value);
        fresh.IsPaired.Should().BeFalse();
        host.Current.App.PairingCode().Should().StartWith("bmb-blind-phone:");
        await host.Current.DisposeAsync();
    }

    [Fact]
    public async Task AnAbortedWipe_GivesTheBackgroundRequestsBack()
    {
        await using var runtime = IosBlindRuntime.Create(Options(), () => { });
        var lifecycle = runtime.Services.GetRequiredService<IBlindLifecycle>();

        lifecycle.StopBackgroundWork();
        lifecycle.ResumeBackgroundWork();

        _background.Cancelled.Should().Be(1);
        _background.Submitted.Should().Be(1);
        runtime.Scheduler.IsRunning.Should().BeTrue();
        await runtime.Scheduler.StopAsync();
    }

    [Fact]
    public async Task TheAppRefreshRound_DoesNothingBeforePairing_LoadsFirst_ThenSyncs()
    {
        var app = new FakeApp();
        (await IosBackgroundRounds.RunSyncAsync(app, CancellationToken.None)).Should().Be("Not paired yet.");
        app.Calls.Should().Equal("init");

        app.Paired = true;
        app.Calls.Clear();
        await IosBackgroundRounds.RunSyncAsync(app, CancellationToken.None);
        app.Calls.Should().Equal("init", "heavy");

        app.Loaded = true;
        app.Calls.Clear();
        await IosBackgroundRounds.RunSyncAsync(app, CancellationToken.None);
        app.Calls.Should().Equal("init", "sync");
    }

    [Fact]
    public async Task TheProcessingRound_RunsTheLongJob_ThenSyncsWhenTheCopyIsLoaded()
    {
        var app = new FakeApp { Paired = true };
        await IosBackgroundRounds.RunWorkAsync(app, CancellationToken.None);
        app.Calls.Should().Equal("init", "heavy");

        app.Loaded = true;
        app.Calls.Clear();
        await IosBackgroundRounds.RunWorkAsync(app, CancellationToken.None);
        app.Calls.Should().Equal("init", "heavy", "sync");
    }

    [Fact]
    public void TheEarliestTimes_AreTheOtherCopiesRhythm()
    {
        IosBackgroundRounds.SyncNotBefore.Should().Be(TimeSpan.FromMinutes(15));
        IosBackgroundRounds.WorkNotBefore.Should().Be(TimeSpan.FromHours(1));
    }

    /// <summary>The computer's "where to call" answer to a phone code, as BlindPhoneEnrollment makes it (the test plays the computer).</summary>
    private static string AnswerFor(string phoneCode)
    {
        BlindPhoneCode.TryParse(phoneCode, out var phone).Should().BeTrue();
        return BlindCallCode.Create("https://192.0.2.10:5610", BlindNodeId.NewId(), SpkiPinText(), Ed25519Signer.GenerateKeyPair().publicKey,
            phone!.Secret.ToArray()).ToString();
    }

    private static string SpkiPinText() => System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private sealed class FakeBackground : IIosBackgroundRequests
    {
        public int Submitted { get; private set; }
        public int Cancelled { get; private set; }
        public void Submit() => Submitted++;
        public void CancelAll() => Cancelled++;
    }

    private sealed class FakeApp : IBlindAppController
    {
        public bool Paired { get; set; }
        public bool Loaded { get; set; }
        public List<string> Calls { get; } = [];
        public event Action? Changed { add { } remove { } }

        public Task InitializeAsync(CancellationToken ct = default) { Calls.Add("init"); return Task.CompletedTask; }

        public BlindAppStatus GetStatus() => new(Guid.NewGuid(), "iPhone", Paired, Loaded, null, null, BlindBackupSchedule.Off, false, null, null, [], []);

        public string? PairingCode() => null;
        public string? AcceptCallCode(string text) => null;
        public void StartRePair() { }
        public void SetSchedule(BlindBackupSchedule schedule) { }
        public Task<string> RunHeavyAsync(bool forceBackup, CancellationToken ct = default) { Calls.Add("heavy"); return Task.FromResult("Nothing due."); }
        public Task<string> RequestSyncAsync(CancellationToken ct = default) { Calls.Add("sync"); return Task.FromResult("Synced."); }
        public Task<long> ExportBackupAsync(string backupName, CancellationToken ct = default) => Task.FromResult(0L);
        public Task DisconnectAndWipeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
