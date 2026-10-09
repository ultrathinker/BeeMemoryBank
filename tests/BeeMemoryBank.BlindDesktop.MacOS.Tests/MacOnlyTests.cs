using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.Platforms.Apple.Keychain;
using BeeMemoryBank.Platforms.Apple.LaunchAgents;
using BeeMemoryBank.Platforms.Apple.TestSupport;

namespace BeeMemoryBank.BlindDesktop.MacOS.Tests;

/// <summary>The real Security.framework calls, on a throwaway keychain file. Run on a Mac; skipped elsewhere.</summary>
public class KeychainMacTests
{
    private const string Service = "test.beememorybank.blind.keychain";

    private static byte[] Random32() => RandomNumberGenerator.GetBytes(32);

    [MacOnlyFact]
    public void ThreeSecrets_RoundTrip_ThroughTheRealKeychain()
    {
        using var keychain = new ThrowawayKeychain();
        using var store = new MacOsKeychainSecretStore(keychain.Path, Service);
        var seed = Random32();
        var backup = Random32();
        var pairing = RandomNumberGenerator.GetBytes(20);

        store.SaveIdentitySeed(seed);
        store.SaveBackupKey(backup);
        store.SavePairingSecret(pairing);

        store.LoadIdentitySeed().Should().Equal(seed);
        store.LoadBackupKey().Should().Equal(backup);
        store.LoadPairingSecret().Should().Equal(pairing);
    }

    [MacOnlyFact]
    public void AMissingSecret_IsNull_AndNothingIsCreated()
    {
        using var keychain = new ThrowawayKeychain();
        using var store = new MacOsKeychainSecretStore(keychain.Path, Service);

        store.LoadIdentitySeed().Should().BeNull();
        store.LoadBackupKey().Should().BeNull();
        store.LoadPairingSecret().Should().BeNull();
        // still nothing after the loads: they did not mint anything
        store.LoadIdentitySeed().Should().BeNull();
    }

    [MacOnlyFact]
    public void Overwriting_ReplacesTheSecret_WithoutADuplicateItem()
    {
        using var keychain = new ThrowawayKeychain();
        using var store = new MacOsKeychainSecretStore(keychain.Path, Service);
        var first = Random32();
        var second = Random32();

        store.SaveBackupKey(first);
        store.SaveBackupKey(second);

        store.LoadBackupKey().Should().Equal(second);
        var backend = new SecurityFrameworkKeychain(keychain.Path, allowUserInteraction: false);
        // exactly one item for the account: deleting once leaves none
        backend.Delete(Service, MacOsKeychainSecretStore.BackupKeyAccount).Should().Be(KeychainStatus.Success);
        backend.Delete(Service, MacOsKeychainSecretStore.BackupKeyAccount).Should().Be(KeychainStatus.ItemNotFound);
    }

    [MacOnlyFact]
    public void ClearPairingSecret_AndClear_RemoveTheItems()
    {
        using var keychain = new ThrowawayKeychain();
        using var store = new MacOsKeychainSecretStore(keychain.Path, Service);
        store.SaveIdentitySeed(Random32());
        store.SaveBackupKey(Random32());
        store.SavePairingSecret(Random32());

        store.ClearPairingSecret();
        store.LoadPairingSecret().Should().BeNull();
        store.LoadIdentitySeed().Should().NotBeNull();

        store.Clear();
        store.LoadIdentitySeed().Should().BeNull();
        store.LoadBackupKey().Should().BeNull();
        store.Clear();   // nothing left: still fine
    }

    [MacOnlyFact]
    public void AnItemTamperedWithOutsideTheAdapter_IsRefused_NotReturned()
    {
        using var keychain = new ThrowawayKeychain();
        using var store = new MacOsKeychainSecretStore(keychain.Path, Service);
        store.SaveIdentitySeed(Random32());
        // another program (or the security tool) replaces the item's data with its own value, through the same Keychain API
        var raw = new SecurityFrameworkKeychain(keychain.Path, allowUserInteraction: false);
        raw.Update(Service, MacOsKeychainSecretStore.IdentitySeedAccount, Encoding.UTF8.GetBytes("a plain value, not an envelope of ours at all")).Should().Be(KeychainStatus.Success);

        var act = () => store.LoadIdentitySeed();

        act.Should().Throw<BlindSecretStoreException>().Where(e => e.Failure == BlindSecretStoreFailure.Corrupt);
    }

    [MacOnlyFact]
    public void ASecretCopiedFromOneAccountToAnother_IsRefused()
    {
        using var keychain = new ThrowawayKeychain();
        using var store = new MacOsKeychainSecretStore(keychain.Path, Service);
        store.SaveIdentitySeed(Random32());
        store.SaveBackupKey(Random32());
        var raw = new SecurityFrameworkKeychain(keychain.Path, allowUserInteraction: false);
        raw.Copy(Service, MacOsKeychainSecretStore.IdentitySeedAccount, out var seedItem).Should().Be(KeychainStatus.Success);
        raw.Update(Service, MacOsKeychainSecretStore.BackupKeyAccount, seedItem!).Should().Be(KeychainStatus.Success);

        var act = () => store.LoadBackupKey();

        act.Should().Throw<BlindSecretStoreException>().Where(e => e.Failure == BlindSecretStoreFailure.Corrupt);
    }

    [MacOnlyFact]
    public void TheItem_IsFiledUnderTheServiceAndAccount_WithOurLabel_AndNoRawSecretInIt()
    {
        using var keychain = new ThrowawayKeychain();
        using var store = new MacOsKeychainSecretStore(keychain.Path, Service);
        var seed = Random32();
        store.SaveIdentitySeed(seed);

        var raw = new SecurityFrameworkKeychain(keychain.Path, allowUserInteraction: false);
        raw.Copy(Service, MacOsKeychainSecretStore.IdentitySeedAccount, out var item).Should().Be(KeychainStatus.Success);
        item!.Length.Should().Be(1 + 32 + 32, "version, digest, secret");
        item.AsSpan(33).ToArray().Should().Equal(seed, "the secret is the tail of the envelope (the Keychain is the protection, the envelope is the integrity check)");
        raw.Copy("another.service", MacOsKeychainSecretStore.IdentitySeedAccount, out _).Should().Be(KeychainStatus.ItemNotFound);
    }

    [MacOnlyFact]
    public void TheAdapterConfinedToAFile_NeverSeesAnotherKeychain()
    {
        using var first = new ThrowawayKeychain();
        using var second = new ThrowawayKeychain();
        using var storeA = new MacOsKeychainSecretStore(first.Path, Service);
        using var storeB = new MacOsKeychainSecretStore(second.Path, Service);
        storeA.SaveBackupKey(Random32());

        storeB.LoadBackupKey().Should().BeNull("the other keychain has no such item, and neither does any search list the adapter might fall back to");
        storeA.LoadBackupKey().Should().NotBeNull();
    }

    [MacOnlyFact]
    public void AKeychainFileThatDoesNotExist_IsAnError_NeverAFallbackToTheLoginKeychain()
    {
        using var folder = new TempFolder();
        using var store = new MacOsKeychainSecretStore(folder.File("missing.keychain"), Service);

        var load = () => store.LoadIdentitySeed();
        var save = () => store.SaveIdentitySeed(Random32());

        load.Should().Throw<BlindSecretStoreException>().Where(e => e.Failure == BlindSecretStoreFailure.Os && e.OsStatus == KeychainStatus.NoSuchKeychain);
        save.Should().Throw<BlindSecretStoreException>().Where(e => e.OsStatus == KeychainStatus.NoSuchKeychain);
    }

    [MacOnlyFact]
    public void TheThrowawayKeychain_IsInNoSearchList_AndItsFileIsGoneAfterwards()
    {
        string path;
        using (var keychain = new ThrowawayKeychain())
        {
            path = keychain.Path;
            File.Exists(path).Should().BeTrue();
            var listed = Run("/usr/bin/security", "list-keychains", "-d", "user");
            listed.Should().NotContain(keychain.Path, "the test keychain is not added to the user's search list");
        }
        File.Exists(path).Should().BeFalse();
    }

    [MacOnlyFact]
    public void TheDefaultKeychainStore_CanBeMade_WithoutTouchingTheKeychain()
    {
        // Constructing the production store (default keychain) reads and writes nothing; this test deliberately makes no call that would.
        using var store = new MacOsKeychainSecretStore();
        store.Should().NotBeNull();
    }

    private static string Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}

/// <summary>Process-runner behavior checked on a Mac.</summary>
public class ProcessRunnerMacTests
{
    [MacOnlyFact]
    public void TheProcessRunner_TimesOut_AndKillsAHangingTool()
    {
        var started = DateTime.UtcNow;

        var result = new ProcessCommandRunner().Run("/bin/sleep", ["30"], TimeSpan.FromMilliseconds(500));

        result.TimedOut.Should().BeTrue();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [MacOnlyFact]
    public void TheProcessRunner_TimesOut_AndTheHangingProcessIsGoneAfterwards()
    {
        // The hang carries a marker in its command line for its whole life (`sleep` alone has none to find):
        // the compound command keeps the shell from exec-replacing itself with sleep, so the shell whose
        // arguments hold the marker is the process that hangs, and `pgrep -f` can see it.
        var marker = "bmb-gone-check-" + Guid.NewGuid().ToString("N");
        var arguments = new[] { "-c", "sleep 30; exit 0", marker };
        var runner = new ProcessCommandRunner();

        var hung = Task.Run(() => runner.Run("/bin/sh", arguments, TimeSpan.FromSeconds(1)));

        // the marker is really findable while the process lives - otherwise the gone-check below would
        // pass vacuously if the marker never matched anything at all
        var seen = false;
        for (var attempt = 0; attempt < 40 && !seen; attempt++)
        {
            seen = runner.Run("/usr/bin/pgrep", ["-f", marker], TimeSpan.FromSeconds(5)).Succeeded;
            if (!seen) Thread.Sleep(50);
        }
        seen.Should().BeTrue("the hanging process must be findable by its marker while it lives");

        hung.Result.TimedOut.Should().BeTrue();
        runner.Run("/usr/bin/pgrep", ["-f", marker], TimeSpan.FromSeconds(5)).Succeeded
            .Should().BeFalse("the runner kills the tree and waits for the kill: nothing matches the marker afterwards");
    }
}

/// <summary>
/// The real launchd: a TEST label, a plist in a folder of the test's own (not ~/Library/LaunchAgents), a program that does nothing
/// (`/usr/bin/true`), bootstrapped into gui/&lt;uid&gt; and booted out again. The test always removes its label and its folder.
/// </summary>
public class LaunchAgentMacTests
{
    [MacOnlyFact]
    public void BootstrapAndBootout_WithATestLabel_RealLaunchd()
    {
        using var folder = new TempFolder();
        var label = "test.beememorybank.blind.autostart." + Guid.NewGuid().ToString("N")[..10];
        var autostart = new MacOsBlindAutostart(new MacOsBlindAutostartOptions
        {
            Label = label,
            LaunchAgentsDirectory = folder.File("LaunchAgents"),
            ProgramArguments = ["/usr/bin/true"],
            LoadImmediately = true,
        });
        try
        {
            var enabled = autostart.Apply(true);

            enabled.Enabled.Should().BeTrue();
            enabled.Warning.Should().BeNull();
            enabled.Loaded.Should().BeTrue("launchd accepted the agent into gui/<uid>");
            autostart.IsEnabled.Should().BeTrue();
            autostart.IsCurrent.Should().BeTrue();
            autostart.IsLoaded().Should().BeTrue();
            File.Exists(autostart.PlistPath).Should().BeTrue();

            // a second call changes nothing
            autostart.Apply(true).Loaded.Should().BeTrue();

            var disabled = autostart.Apply(false);

            disabled.Enabled.Should().BeFalse();
            disabled.Loaded.Should().BeFalse();
            disabled.Warning.Should().BeNull();
            autostart.IsLoaded().Should().BeFalse();
            autostart.IsEnabled.Should().BeFalse();
            File.Exists(autostart.PlistPath).Should().BeFalse();
        }
        finally
        {
            // whatever happened above, this test's own label is not left in launchd
            if (autostart.IsLoaded()) autostart.Apply(false);
        }
        autostart.IsLoaded().Should().BeFalse();
    }

    [MacOnlyFact]
    public void ThePlistWeWrite_IsAcceptedByPlutil()
    {
        using var folder = new TempFolder();
        var path = folder.File("check.plist");
        File.WriteAllText(path, LaunchAgentPlist.Build("test.beememorybank.blind.plutil", ["/usr/bin/true", "--minimized"]));

        var result = new ProcessCommandRunner().Run("/usr/bin/plutil", ["-lint", path], TimeSpan.FromSeconds(20));

        result.Succeeded.Should().BeTrue(result.StandardOutput + result.StandardError);
    }
}
