using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Infrastructure.Secrets;
using BeeMemoryBank.Platforms.Apple.Keychain;
using Xunit.Abstractions;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The Keychain store against the REAL Security.framework, on a throwaway keychain FILE with user interaction disabled
/// (<c>ThrowawayKeychain</c>: made through SecKeychainCreate, in no search list, random password, removed with the test). Nothing here
/// can reach the login keychain: every store is built with <see cref="MacOsKeychainUserSecretStore.ForKeychainFile"/>, and the test service
/// name is never the product's. A Mac only; skipped, not failed, elsewhere. The behaviors every store shares run in
/// <see cref="UserSecretStoreContract"/> against the same real keychain.
/// </summary>
public class MacOsKeychainUserSecretStoreMacTests(ITestOutputHelper output)
{
    [MacOnlyFact]
    public void TheStoredItem_IsTheDocumentedEnvelope_AtTheDocumentedServiceAndAccount()
    {
        using var h = new RealKeychainStoreHarness();
        var secret = RandomNumberGenerator.GetBytes(32);

        h.Store.Write("os-auto-unlock", "default", secret);

        h.Raw.Copy(h.Service, "os-auto-unlock/default", out var raw).Should().Be(KeychainStatus.Success);
        raw!.Length.Should().Be(1 + 32 + 32);
        raw[0].Should().Be(1);
        raw.AsSpan(33).ToArray().Should().Equal(secret);
        SHA256.HashData([.. "bmb-desktop-keychain-item-v1\0"u8.ToArray(), 0, .. "os-auto-unlock"u8.ToArray(), 0, .. "default"u8.ToArray(), 0, .. secret])
            .Should().Equal(raw.AsSpan(1, 32).ToArray(), "the digest binds domain, scope, purpose, account and data");
    }

    [MacOnlyFact]
    public void ADamagedOrForeignItem_IsMalformed_AndWritingAgainReplacesIt()
    {
        using var h = new RealKeychainStoreHarness();
        h.Store.Write("p", "a", RandomNumberGenerator.GetBytes(32));

        foreach (var damaged in new byte[][]
                 {
                     [],
                     [1],
                     Encoding.UTF8.GetBytes("a plain string put there with the security tool"),
                     new byte[33],
                     [.. new byte[] { 1 }, .. new byte[32], .. new byte[5]],
                 })
        {
            h.Raw.Update(h.Service, "p/a", damaged).Should().Be(KeychainStatus.Success);
            var read = () => h.Store.Read("p", "a");
            read.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
        }

        var fresh = RandomNumberGenerator.GetBytes(32);
        h.Store.Write("p", "a", fresh);
        h.Store.Read("p", "a").Should().Equal(fresh);
    }

    [MacOnlyFact]
    public void ALockedKeychain_WithInteractionDisabled_IsLocked_ForRead_AndForWrite_NeverNullAndNeverSuccess()
    {
        using var h = new RealKeychainStoreHarness();
        var secret = RandomNumberGenerator.GetBytes(32);
        h.Store.Write("os-auto-unlock", "default", secret);
        h.Keychain.Lock();
        var started = DateTime.UtcNow;

        var actions = new (string What, Action Act)[]
        {
            ("read", () => h.Store.Read("os-auto-unlock", "default")),
            ("write over existing", () => h.Store.Write("os-auto-unlock", "default", RandomNumberGenerator.GetBytes(32))),
            ("write new", () => h.Store.Write("update-unlock", "default", RandomNumberGenerator.GetBytes(32))),
        };

        foreach (var (what, action) in actions)
        {
            var thrown = action.Should().Throw<UserSecretStoreException>(what).Which;
            output.WriteLine($"OBSERVED locked file keychain, interaction disabled, {what} -> {thrown.FailureKind}: {thrown.Message}");
            thrown.FailureKind.Should().Be(UserSecretStoreFailureKind.Locked, what);
            thrown.Message.Should().NotContain("default").And.NotContain("os-auto-unlock");
        }
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(30), "no password prompt may be waited for");

        h.Keychain.Unlock();
        h.Store.Read("os-auto-unlock", "default").Should().Equal(secret, "unlocked again, the secret is still there and unchanged");
        h.Store.Read("update-unlock", "default").Should().BeNull("the refused write created nothing");
    }

    [MacOnlyFact]
    public void ADeleteOnALockedKeychain_ReportsTheTruth_EitherLockedAndTheItemRemains_OrSuccessAndTheItemIsGone()
    {
        // Observed on macOS 26.5: SecItemDelete does not need the (file) keychain to be unlocked. Whatever the system does, the store must
        // not lie about it: no exception means the secret is gone, an exception means it is still there.
        using var h = new RealKeychainStoreHarness();
        h.Store.Write("os-auto-unlock", "default", RandomNumberGenerator.GetBytes(32));
        h.Keychain.Lock();

        UserSecretStoreException? thrown = null;
        try { h.Store.Delete("os-auto-unlock", "default"); }
        catch (UserSecretStoreException ex) { thrown = ex; }
        output.WriteLine("OBSERVED locked file keychain, interaction disabled, delete -> "
            + (thrown is null ? "success (no exception)" : thrown.FailureKind + ": " + thrown.Message));
        h.Keychain.Unlock();

        var after = h.Store.Read("os-auto-unlock", "default");
        if (thrown is null)
        {
            after.Should().BeNull("a delete that reported success removed the item");
        }
        else
        {
            thrown.FailureKind.Should().Be(UserSecretStoreFailureKind.Locked);
            after.Should().NotBeNull("a delete that reported a refusal left the item");
        }
    }

    [MacOnlyFact]
    public void AKeychainFileThatDoesNotExist_IsUnavailable_AndIsNotCreated()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bmb-store-missing-" + Guid.NewGuid().ToString("N")[..10], "missing.keychain");
        using var store = MacOsKeychainUserSecretStore.ForKeychainFile(missing, "test.bmb.missing");

        foreach (var (what, action) in new (string, Action)[]
                 {
                     ("read", () => store.Read("p", "a")),
                     ("write", () => store.Write("p", "a", [1])),
                     ("delete", () => store.Delete("p", "a")),
                 })
        {
            var thrown = action.Should().Throw<UserSecretStoreException>(what).Which;
            output.WriteLine($"OBSERVED missing keychain file, {what} -> {thrown.FailureKind}: {thrown.Message}");
            thrown.FailureKind.Should().Be(UserSecretStoreFailureKind.Unavailable);
            thrown.Message.Should().Contain("status -25294");
        }
        File.Exists(missing).Should().BeFalse("asking for a keychain that is not there neither creates it nor falls back to the login keychain");
    }

    [MacOnlyFact]
    public void TwoDataFolders_ScopedStoresOnOneKeychain_NeverShareASecret_AndAnItemMovedBetweenThemIsMalformed()
    {
        using var h = new RealKeychainStoreHarness();
        using var folders = new TempDataFolders();
        var dirA = folders.Make();
        var dirB = folders.Make();
        var folderA = h.OpenStoreForDataPath(dirA);
        var folderB = h.OpenStoreForDataPath(dirB);
        var unscoped = MacOsKeychainUserSecretStore.ForKeychainFile(h.Keychain.Path, h.Service);
        var a = RandomNumberGenerator.GetBytes(32);
        var b = RandomNumberGenerator.GetBytes(32);

        folderA.Write("os-auto-unlock", "default", a);
        folderB.Write("os-auto-unlock", "default", b);

        folderA.Read("os-auto-unlock", "default").Should().Equal(a);
        folderB.Read("os-auto-unlock", "default").Should().Equal(b);
        unscoped.Read("os-auto-unlock", "default").Should().BeNull("an unscoped store has its own namespace");

        var scopeA = File.ReadAllText(Path.Combine(dirA, MacOsKeychainUserSecretStore.ScopeFileName));
        var scopeB = File.ReadAllText(Path.Combine(dirB, MacOsKeychainUserSecretStore.ScopeFileName));
        scopeA.Should().NotBe(scopeB);
        h.Raw.Copy(h.Service, scopeA + "/os-auto-unlock/default", out var itemOfA).Should().Be(KeychainStatus.Success);
        h.Raw.Update(h.Service, scopeB + "/os-auto-unlock/default", itemOfA!).Should().Be(KeychainStatus.Success);
        var read = () => folderB.Read("os-auto-unlock", "default");
        read.Should().Throw<UserSecretStoreException>().Which.FailureKind.Should().Be(UserSecretStoreFailureKind.Malformed);
    }

    [MacOnlyFact]
    public void ACopiedDataFolder_KeepsItsScope_SoItsSecretsStayReachable_OnTheRealKeychain()
    {
        using var h = new RealKeychainStoreHarness();
        using var folders = new TempDataFolders();
        var original = folders.Make();
        var secret = RandomNumberGenerator.GetBytes(32);
        h.OpenStoreForDataPath(original).Write("local-ca", "default", secret);
        var copy = folders.CopyOf(original);

        // The same vault under another name: the id travels in the folder, the path is irrelevant.
        h.OpenStoreForDataPath(copy).Read("local-ca", "default").Should().Equal(secret);
        File.ReadAllText(Path.Combine(copy, MacOsKeychainUserSecretStore.ScopeFileName))
            .Should().Be(File.ReadAllText(Path.Combine(original, MacOsKeychainUserSecretStore.ScopeFileName)));
    }

    [MacOnlyFact]
    public void ASymbolicLinkToADataFolder_IsTheSameVault_OnTheRealKeychain()
    {
        using var h = new RealKeychainStoreHarness();
        using var folders = new TempDataFolders();
        var original = folders.Make();
        var alias = folders.TryLinkTo(original);
        alias.Should().NotBeNull("symbolic links can be made on a Mac");
        var secret = RandomNumberGenerator.GetBytes(32);
        h.OpenStoreForDataPath(original).Write("os-auto-unlock", "default", secret);

        h.OpenStoreForDataPath(alias!).Read("os-auto-unlock", "default").Should().Equal(secret);
    }

    [MacOnlyFact]
    public void TwoServiceNames_OnOneKeychain_AreTwoNamespaces()
    {
        using var h = new RealKeychainStoreHarness();
        var other = h.OpenStore(service: h.Service + ".other");

        h.Store.Write("p", "a", [1]);
        other.Write("p", "a", [2]);

        h.Store.Read("p", "a").Should().Equal(new byte[] { 1 });
        other.Read("p", "a").Should().Equal(new byte[] { 2 });
        other.Delete("p", "a");
        h.Store.Read("p", "a").Should().Equal(new byte[] { 1 });
    }

    [MacOnlyFact]
    public void AnEmptySecret_AndALargeSecret_RoundTripThroughTheRealKeychain()
    {
        using var h = new RealKeychainStoreHarness();
        var large = RandomNumberGenerator.GetBytes(100_000);

        h.Store.Write("p", "empty", ReadOnlySpan<byte>.Empty);
        h.Store.Write("p", "large", large);

        h.Store.Read("p", "empty").Should().NotBeNull().And.BeEmpty();
        h.Store.Read("p", "large").Should().Equal(large);
    }

    [MacOnlyFact]
    public void ARealRaceBetweenManyStoreObjects_NeverTearsAnItem()
    {
        // Eight store objects over one keychain file (what several processes of the app would be), each writing the same account while
        // another reads it: a read may see any complete value, never a mixture and never a "damaged" finding.
        using var h = new RealKeychainStoreHarness();
        var stores = Enumerable.Range(0, 8).Select(_ => h.OpenStore()).ToArray();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var barrier = new Barrier(stores.Length);
        var workers = stores.Select((store, t) => new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                for (var r = 0; r < 25; r++)
                {
                    store.Write("race", "item", UserSecretStoreContract.Payload(t, r));
                    var seen = store.Read("race", "item");
                    if (!UserSecretStoreContract.IsIntactPayload(seen)) errors.Enqueue(new InvalidOperationException("torn value"));
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToList();

        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());

        errors.Should().BeEmpty();
    }

    [MacOnlyFact]
    public void TheDefaultKeychainStoreObject_CanBeBuilt_ButThisTestMakesNoCallOnIt()
    {
        // The production object (no file: the user's login keychain) must be constructible and say it is supported; no call is made on it
        // here, or anywhere in the tests - every call is on a throwaway keychain file. Building it touches no disk either: the scope id of
        // the data folder is read or created at the first use, not here (the folder below is never made).
        var neverMade = Path.Combine(Path.GetTempPath(), "bmb-never-made-" + Guid.NewGuid().ToString("N")[..10]);

        using var production = MacOsKeychainUserSecretStore.ForDataPath(neverMade);
        using var viaTheFactory = (MacOsKeychainUserSecretStore)UserSecretStores.CreateDefault(neverMade);

        production.IsSupported.Should().BeTrue();
        viaTheFactory.IsSupported.Should().BeTrue();
        Directory.Exists(neverMade).Should().BeFalse("constructing the store neither creates the data folder nor reads a scope file");
    }
}
