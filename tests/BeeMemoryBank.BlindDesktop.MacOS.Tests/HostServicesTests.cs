using System.Text;
using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop.MacOS.Tests;

/// <summary>
/// The seams wired into Blind.AppCore the way the host wires them (the host's own lifecycle replaced by a recording one), with a fake Keychain and a temporary Application Support folder, so
/// that the seams are proved together with the core's own rules (fail closed, wipe order, secrets only in the Keychain) on any OS.
/// </summary>
public class HostServicesTests
{
    private static ServiceProvider Build(TempFolder root, FakeKeychainBackend keychain)
    {
        var options = new MacOsBlindHostOptions
        {
            ApplicationSupportRoot = root.Path,
            KeychainBackend = keychain,
            LaunchAgentsDirectory = root.File("LaunchAgents"),
            AutostartProgramArguments = ["/usr/bin/true"],
            LoadAutostartImmediately = false,
        };
        var services = new ServiceCollection();
        services.AddSeamsAndCore(options);
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void EverySeamResolvesToTheMacOsImplementation_AndTheConcreteTypesAreTheSameInstances()
    {
        using var root = new TempFolder();
        using var provider = Build(root, new FakeKeychainBackend());

        provider.GetRequiredService<IBlindPaths>().Should().BeOfType<MacOsBlindPaths>();
        provider.GetRequiredService<IBlindSecretStore>().Should().BeOfType<MacOsKeychainSecretStore>()
            .Which.Should().BeSameAs(provider.GetRequiredService<MacOsKeychainSecretStore>());
        provider.GetRequiredService<IBlindStateStore>().Should().BeOfType<MacOsBlindStateStore>();
        provider.GetRequiredService<IBlindAutostart>().Should().BeOfType<MacOsBlindAutostart>()
            .Which.Should().BeSameAs(provider.GetRequiredService<MacOsBlindAutostart>());
        provider.GetRequiredService<BlindAppController>().Should().NotBeNull();
        // the older PhoneClient contracts forward to the same objects
        provider.GetRequiredService<IBlindPhoneKeys>().Should().BeSameAs(provider.GetRequiredService<IBlindSecretStore>());
        provider.GetRequiredService<IBlindPhoneStore>().Should().BeSameAs(provider.GetRequiredService<IBlindStateStore>());
    }

    [Fact]
    public void TheHostDoesNotRegister_TheSchedulerOrTheExporter_ThoseAreTheHostsOwn()
    {
        using var root = new TempFolder();
        using var provider = Build(root, new FakeKeychainBackend());

        provider.GetService<IBlindScheduler>().Should().BeNull();
        provider.GetService<IBlindBackupExporter>().Should().BeNull();
    }

    [Fact]
    public async Task TheFirstRun_CreatesTheDatabaseAndTheIdentity_InTheBlindAppsFolder()
    {
        // Since the desktop stage, BlindAppController.InitializeAsync opens the database AND makes the identity when there is none (the
        // window must show the pairing code at once). That is the intended behaviour, not a bug: this test used to expect no identity
        // because InitializeAsync only opened the database back then.
        using var root = new TempFolder();
        var keychain = new FakeKeychainBackend();
        using var provider = Build(root, keychain);
        var controller = provider.GetRequiredService<BlindAppController>();

        await controller.InitializeAsync();

        var paths = provider.GetRequiredService<IBlindPaths>();
        File.Exists(paths.DatabasePath).Should().BeTrue();
        paths.DatabasePath.Should().StartWith(Path.Combine(root.Path, "BeeMemoryBankBlind"));
        var status = controller.GetStatus();
        status.StartError.Should().BeNull();
        status.NodeId.Should().NotBeNull("the identity is made at the first start");
        status.DisplayName.Should().Be("Blind copy", "no display-name factory was given");
        status.IsPaired.Should().BeFalse("it is not paired until the computer's answer is accepted");
        status.AwaitingAnswer.Should().BeTrue();
        controller.PairingCode().Should().StartWith("bmb-blind-phone:?");
        keychain.Items.Keys.Select(k => k.Item2).Should().BeEquivalentTo("identity-seed-v1", "backup-key-v1", "pairing-secret-v1");
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task InitializeAsync_IsRepeatable_AndKeepsTheSameIdentity()
    {
        using var root = new TempFolder();
        using var provider = Build(root, new FakeKeychainBackend());
        var controller = provider.GetRequiredService<BlindAppController>();
        await controller.InitializeAsync();
        var node = controller.GetStatus().NodeId;

        await controller.InitializeAsync();

        controller.GetStatus().NodeId.Should().Be(node);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task TheIdentity_PutsItsThreeSecretsInTheKeychain_AndNowhereElse()
    {
        using var root = new TempFolder();
        var keychain = new FakeKeychainBackend();
        using var provider = Build(root, keychain);
        await provider.GetRequiredService<BlindAppController>().InitializeAsync();
        var pairing = provider.GetRequiredService<BlindMobilePairing>();
        var secrets = provider.GetRequiredService<IBlindSecretStore>();

        await pairing.CreateIdentityAsync("Test Mac");

        var seed = secrets.LoadIdentitySeed()!;
        var backup = secrets.LoadBackupKey()!;
        var pairingSecret = secrets.LoadPairingSecret()!;
        keychain.Items.Keys.Select(k => k.Item2).Should().BeEquivalentTo("identity-seed-v1", "backup-key-v1", "pairing-secret-v1");
        provider.GetRequiredService<BlindAppController>().PairingCode().Should().NotBeNullOrEmpty();
        // the state (the core's own, over the macOS state file) knows the identity, not its keys
        provider.GetRequiredService<BlindPhoneState>().NodeId.Should().NotBeNull();

        SqliteConnection.ClearAllPools();
        var files = Directory.GetFiles(root.Path, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".tmp")).ToList();
        files.Should().NotBeEmpty();
        foreach (var (name, secret) in new[] { ("seed", seed), ("backup key", backup), ("pairing secret", pairingSecret) })
        {
            foreach (var file in files)
            {
                var content = ReadShared(file);
                foreach (var form in Forms(secret))
                    IndexOf(content, form.Bytes).Should().Be(-1, $"the {name} ({form.Name}) must not be in {Path.GetFileName(file)}");
            }
        }
    }

    [Fact]
    public async Task AnIdentityWhoseSecretsAreGone_FailsClosed_AndMintsNothingNew()
    {
        using var root = new TempFolder();
        var keychain = new FakeKeychainBackend();
        Guid nodeId;
        using (var first = Build(root, keychain))
        {
            await first.GetRequiredService<BlindAppController>().InitializeAsync();
            await first.GetRequiredService<BlindMobilePairing>().CreateIdentityAsync("Test Mac");
            nodeId = first.GetRequiredService<BlindPhoneState>().NodeId!.Value;
            SqliteConnection.ClearAllPools();
        }
        // the state file is lost and so are the Keychain items (a new login keychain, a restored profile ...): the database still has the identity
        File.Delete(Path.Combine(root.Path, "BeeMemoryBankBlind", MacOsBlindStateStore.FileName));
        foreach (var key in keychain.Items.Keys.ToList()) keychain.Delete(key.Item1, key.Item2);
        var addsBefore = keychain.AddCalls;

        using var second = Build(root, keychain);
        await second.GetRequiredService<BlindAppController>().InitializeAsync();
        var act = () => second.GetRequiredService<BlindMobilePairing>().CreateIdentityAsync("Test Mac");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Disconnect and wipe required*");
        keychain.Items.Should().BeEmpty("a missing seed is never replaced by a new one over the existing identity");
        keychain.AddCalls.Should().Be(addsBefore);
        SqliteConnection.ClearAllPools();
        nodeId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AnIdentityWhoseStateIsLost_ButWhoseSecretsAreThere_IsRecovered_NotRemade()
    {
        using var root = new TempFolder();
        var keychain = new FakeKeychainBackend();
        Guid nodeId;
        byte[] seed;
        using (var first = Build(root, keychain))
        {
            await first.GetRequiredService<BlindAppController>().InitializeAsync();
            await first.GetRequiredService<BlindMobilePairing>().CreateIdentityAsync("Test Mac");
            nodeId = first.GetRequiredService<BlindPhoneState>().NodeId!.Value;
            seed = first.GetRequiredService<IBlindSecretStore>().LoadIdentitySeed()!;
            SqliteConnection.ClearAllPools();
        }
        File.Delete(Path.Combine(root.Path, "BeeMemoryBankBlind", MacOsBlindStateStore.FileName));

        using var second = Build(root, keychain);
        await second.GetRequiredService<BlindAppController>().InitializeAsync();
        await second.GetRequiredService<BlindMobilePairing>().CreateIdentityAsync("Test Mac");

        second.GetRequiredService<BlindPhoneState>().NodeId.Should().Be(nodeId);
        second.GetRequiredService<IBlindSecretStore>().LoadIdentitySeed().Should().Equal(seed);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task DisconnectAndWipe_ForgetsTheSecrets_TheState_AndTheDatabase_ThenAsksTheHostToRestart()
    {
        using var root = new TempFolder();
        var keychain = new FakeKeychainBackend();
        using var provider = Build(root, keychain);
        var controller = provider.GetRequiredService<BlindAppController>();
        await controller.InitializeAsync();
        await provider.GetRequiredService<BlindMobilePairing>().CreateIdentityAsync("Test Mac");
        var paths = provider.GetRequiredService<IBlindPaths>();
        File.Exists(paths.DatabasePath).Should().BeTrue();
        var lifecycle = provider.GetRequiredService<RecordingLifecycle>();
        // host-level state the core does not know about, a copy of a damaged state file, an unrelated file in the folder, and the full app's folder
        provider.GetRequiredService<IBlindStateStore>().Set("bmb.blind.desktop.host_state", "present");
        var stateFile = Path.Combine(paths.DataDirectory, MacOsBlindStateStore.FileName);
        var damagedCopy = Path.Combine(paths.DataDirectory, MacOsBlindStateStore.FileName + ".damaged-20261004T120000000");
        File.WriteAllText(damagedCopy, "old damaged state");
        var unrelated = Path.Combine(paths.DataDirectory, "unrelated.txt");
        File.WriteAllText(unrelated, "not ours to remove");
        var fullAppFolder = Path.Combine(root.Path, "BeeMemoryBankData");
        Directory.CreateDirectory(fullAppFolder);
        var fullAppFile = Path.Combine(fullAppFolder, MacOsBlindStateStore.FileName);
        File.WriteAllText(fullAppFile, "the full app's file");
        File.Exists(stateFile).Should().BeTrue();

        await controller.DisconnectAndWipeAsync();

        File.Exists(stateFile).Should().BeFalse("the host's state file is part of what a wipe removes");
        File.Exists(damagedCopy).Should().BeFalse();
        File.Exists(unrelated).Should().BeTrue("only the state store's own files go, not everything in the folder");
        File.ReadAllText(fullAppFile).Should().Be("the full app's file", "the full app's folders are never touched");
        new MacOsBlindStateStore(paths).Get("bmb.blind.desktop.host_state").Should().BeNull();
        keychain.Items.Should().BeEmpty();
        provider.GetRequiredService<BlindPhoneState>().NodeId.Should().BeNull();
        File.Exists(paths.DatabasePath).Should().BeFalse();
        Directory.GetFiles(paths.DataDirectory, "beememorybank.db*").Should().BeEmpty();
        lifecycle.Stopped.Should().BeGreaterThan(0, "the running work is stopped before anything is removed");
        lifecycle.Restarted.Should().Be(1);
        Directory.Exists(paths.DataDirectory).Should().BeTrue("the app's own folder stays; only what the blind copy kept in it goes");
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static IEnumerable<(string Name, byte[] Bytes)> Forms(byte[] secret)
    {
        yield return ("raw", secret);
        yield return ("hex upper", Encoding.ASCII.GetBytes(Convert.ToHexString(secret)));
        yield return ("hex lower", Encoding.ASCII.GetBytes(Convert.ToHexString(secret).ToLowerInvariant()));
        yield return ("base64", Encoding.ASCII.GetBytes(Convert.ToBase64String(secret)));
        yield return ("base64url", Encoding.ASCII.GetBytes(Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')));
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);
}
