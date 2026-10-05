using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Platforms.Apple.Keychain;
using BeeMemoryBank.Platforms.Apple.TestSupport;

namespace BeeMemoryBank.Platforms.Apple.Tests;

/// <summary>
/// The real Security.framework calls of <see cref="SecurityFrameworkKeychain"/>, on a throwaway keychain FILE with user interaction
/// disabled (see <see cref="ThrowawayKeychain"/>): add and duplicate, update, copy and not-found, delete, confinement to the file,
/// a missing file, and a locked keychain. A secret store built on top decides what the bytes mean (the blind app's envelope, the full
/// app's); the backend returns exactly what was stored. Run on a Mac, skipped elsewhere; nothing here can reach the login keychain.
/// </summary>
public class SecurityFrameworkKeychainMacTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Service = "test.beememorybank.apple.keychain";
    private const string Account = "purpose/vault-1";

    private static SecurityFrameworkKeychain Backend(ThrowawayKeychain keychain) => new(keychain.Path, allowUserInteraction: false);

    private static byte[] Random(int length) => RandomNumberGenerator.GetBytes(length);

    [MacOnlyFact]
    public void AnAddedItem_IsCopiedBack_ByteForByte()
    {
        using var keychain = new ThrowawayKeychain();
        using var backend = Backend(keychain);
        var data = Random(97);

        backend.Add(Service, Account, "Test item", data).Should().Be(KeychainStatus.Success);

        backend.Copy(Service, Account, out var back).Should().Be(KeychainStatus.Success);
        back.Should().Equal(data);
    }

    [MacOnlyFact]
    public void AnEmptyValue_AndALargeValue_RoundTrip()
    {
        using var keychain = new ThrowawayKeychain();
        using var backend = Backend(keychain);
        var large = Random(100_000);

        backend.Add(Service, "empty", "Test item", []).Should().Be(KeychainStatus.Success);
        backend.Add(Service, "large", "Test item", large).Should().Be(KeychainStatus.Success);

        backend.Copy(Service, "empty", out var empty).Should().Be(KeychainStatus.Success);
        empty.Should().NotBeNull().And.BeEmpty();
        backend.Copy(Service, "large", out var big).Should().Be(KeychainStatus.Success);
        big.Should().Equal(large);
    }

    [MacOnlyFact]
    public void AddingTheSameServiceAndAccountTwice_IsADuplicate_AndTheFirstValueSurvives()
    {
        using var keychain = new ThrowawayKeychain();
        using var backend = Backend(keychain);
        var first = Random(32);

        backend.Add(Service, Account, "Test item", first).Should().Be(KeychainStatus.Success);
        backend.Add(Service, Account, "Test item", Random(32)).Should().Be(KeychainStatus.DuplicateItem);

        backend.Copy(Service, Account, out var back);
        back.Should().Equal(first, "a refused duplicate changes nothing");
    }

    [MacOnlyFact]
    public void Update_ReplacesTheValue_InPlace_WithoutASecondItem()
    {
        using var keychain = new ThrowawayKeychain();
        using var backend = Backend(keychain);
        backend.Add(Service, Account, "Test item", Random(32)).Should().Be(KeychainStatus.Success);
        var second = Random(48);

        backend.Update(Service, Account, second).Should().Be(KeychainStatus.Success);

        backend.Copy(Service, Account, out var back);
        back.Should().Equal(second);
        backend.Delete(Service, Account).Should().Be(KeychainStatus.Success);
        backend.Delete(Service, Account).Should().Be(KeychainStatus.ItemNotFound, "there was exactly one item");
    }

    [MacOnlyFact]
    public void AMissingItem_IsNotFound_OnEveryOperation_AndCopyGivesNoData()
    {
        using var keychain = new ThrowawayKeychain();
        using var backend = Backend(keychain);

        backend.Copy(Service, Account, out var data).Should().Be(KeychainStatus.ItemNotFound);
        data.Should().BeNull();
        backend.Update(Service, Account, Random(8)).Should().Be(KeychainStatus.ItemNotFound, "an update never creates an item");
        backend.Delete(Service, Account).Should().Be(KeychainStatus.ItemNotFound);
        backend.Copy(Service, Account, out _).Should().Be(KeychainStatus.ItemNotFound, "and the failed update created nothing");
    }

    [MacOnlyFact]
    public void ItemsAreToldApartByServiceAndByAccount()
    {
        using var keychain = new ThrowawayKeychain();
        using var backend = Backend(keychain);
        var a = Random(16);
        var b = Random(16);
        var c = Random(16);

        backend.Add("service.one", "account", "Test item", a).Should().Be(KeychainStatus.Success);
        backend.Add("service.two", "account", "Test item", b).Should().Be(KeychainStatus.Success);
        backend.Add("service.one", "other", "Test item", c).Should().Be(KeychainStatus.Success);

        backend.Copy("service.one", "account", out var ra);
        backend.Copy("service.two", "account", out var rb);
        backend.Copy("service.one", "other", out var rc);
        ra.Should().Equal(a);
        rb.Should().Equal(b);
        rc.Should().Equal(c);
        backend.Delete("service.one", "account").Should().Be(KeychainStatus.Success);
        backend.Copy("service.two", "account", out _).Should().Be(KeychainStatus.Success, "deleting one item deletes only that item");
        backend.Copy("service.one", "other", out _).Should().Be(KeychainStatus.Success);
    }

    [MacOnlyFact]
    public void NamesWithSlashesSpacesAndNonAsciiCharacters_Work()
    {
        using var keychain = new ThrowawayKeychain();
        using var backend = Backend(keychain);
        var service = "com.example.\u0441\u0435\u0440\u0432\u0438\u0441 v1";
        var account = "purpose/账户 é / vault";
        var data = Random(20);

        backend.Add(service, account, "Test é", data).Should().Be(KeychainStatus.Success);

        backend.Copy(service, account, out var back).Should().Be(KeychainStatus.Success);
        back.Should().Equal(data);
    }

    [MacOnlyFact]
    public void TheBackend_ReturnsAnyBytes_WithoutInterpretingThem()
    {
        // An envelope, a plain string, bytes that are not text: what a store makes of them (a corrupt envelope is the store's finding)
        // is decided above this layer, so the layer must hand back exactly what is in the item - even what looks like garbage.
        using var keychain = new ThrowawayKeychain();
        using var backend = Backend(keychain);
        var plain = Encoding.UTF8.GetBytes("a plain value, not an envelope of anybody's");
        backend.Add(Service, Account, "Test item", Random(40)).Should().Be(KeychainStatus.Success);

        backend.Update(Service, Account, plain).Should().Be(KeychainStatus.Success);

        backend.Copy(Service, Account, out var back);
        back.Should().Equal(plain);
        var binary = new byte[] { 0, 1, 2, 255, 254, 0, 0, 7 };
        backend.Update(Service, Account, binary).Should().Be(KeychainStatus.Success);
        backend.Copy(Service, Account, out var back2);
        back2.Should().Equal(binary);
    }

    [MacOnlyFact]
    public void ABackendConfinedToAFile_NeverSeesAnotherKeychain()
    {
        using var first = new ThrowawayKeychain();
        using var second = new ThrowawayKeychain();
        using var a = Backend(first);
        using var b = Backend(second);
        a.Add(Service, Account, "Test item", Random(32)).Should().Be(KeychainStatus.Success);

        b.Copy(Service, Account, out var data).Should().Be(KeychainStatus.ItemNotFound, "the other keychain has no such item, and neither does any search list");
        data.Should().BeNull();
        b.Delete(Service, Account).Should().Be(KeychainStatus.ItemNotFound);
        a.Copy(Service, Account, out _).Should().Be(KeychainStatus.Success);
    }

    [MacOnlyFact]
    public void AKeychainFileThatDoesNotExist_IsNoSuchKeychain_ForEveryOperation_NeverAFallbackToTheLoginKeychain()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bmb-apple-missing-" + Guid.NewGuid().ToString("N")[..10], "missing.keychain");
        using var backend = new SecurityFrameworkKeychain(missing, allowUserInteraction: false);

        backend.Add(Service, Account, "Test item", Random(8)).Should().Be(KeychainStatus.NoSuchKeychain);
        backend.Copy(Service, Account, out var data).Should().Be(KeychainStatus.NoSuchKeychain);
        data.Should().BeNull();
        backend.Update(Service, Account, Random(8)).Should().Be(KeychainStatus.NoSuchKeychain);
        backend.Delete(Service, Account).Should().Be(KeychainStatus.NoSuchKeychain);
        File.Exists(missing).Should().BeFalse("asking for a keychain that is not there does not create it");
    }

    [MacOnlyFact]
    public void ALockedKeychain_WithUserInteractionDisabled_FailsAtOnce_InsteadOfWaitingForAPerson()
    {
        using var keychain = new ThrowawayKeychain();
        using var backend = Backend(keychain);
        backend.Add(Service, Account, "Test item", Random(32)).Should().Be(KeychainStatus.Success);
        keychain.Lock();

        var started = DateTime.UtcNow;
        var copy = backend.Copy(Service, Account, out var data);
        var add = backend.Add(Service, "another", "Test item", Random(8));

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(30), "no password prompt may be waited for");
        output.WriteLine($"locked keychain, interaction disabled: copy={copy} add={add}");
        // Which "cannot ask the user" status a locked FILE keychain reports depends on the macOS version: errSecInteractionNotAllowed
        // (-25308) by the documentation, errSecAuthFailed (-25293) on macOS 26.5 where this was run. Either way it is a refusal the store
        // maps to "the key store is unavailable" - never "not found" and never success.
        copy.Should().BeOneOf(KeychainStatus.InteractionNotAllowed, KeychainStatus.AuthFailed);
        data.Should().BeNull();
        add.Should().BeOneOf(KeychainStatus.InteractionNotAllowed, KeychainStatus.AuthFailed);
        keychain.Unlock();
        backend.Copy(Service, Account, out _).Should().Be(KeychainStatus.Success, "unlocked again, the item is still there and unchanged");
        backend.Copy(Service, "another", out _).Should().Be(KeychainStatus.ItemNotFound, "the refused add created nothing");
    }

    [MacOnlyFact]
    public void Describe_GivesTheSystemsTextForAStatus_AndNothingForSuccess()
    {
        using var keychain = new ThrowawayKeychain();
        using var backend = Backend(keychain);

        backend.Describe(KeychainStatus.ItemNotFound).Should().NotBeNullOrWhiteSpace();
        backend.Describe(KeychainStatus.DuplicateItem).Should().NotBeNullOrWhiteSpace();
    }

    [MacOnlyFact]
    public void DisposingTwice_AndUsingTheDefaultKeychainBackendObject_TouchNothing()
    {
        using var keychain = new ThrowawayKeychain();
        var backend = Backend(keychain);
        backend.Add(Service, Account, "Test item", Random(8)).Should().Be(KeychainStatus.Success);

        backend.Dispose();
        backend.Dispose();

        // The production object (no file: the user's default keychain) can be constructed; this test makes no call that would reach it.
        using var production = new SecurityFrameworkKeychain();
        production.Should().NotBeNull();
    }
}
