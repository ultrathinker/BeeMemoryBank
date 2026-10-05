using System.Text;
using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.Platforms.Apple.Keychain;

namespace BeeMemoryBank.BlindDesktop.MacOS.Tests;

/// <summary>An in-memory keychain with the status codes of the real one.</summary>
internal sealed class FakeKeychainBackend : IKeychainBackend
{
    private readonly Dictionary<(string, string), byte[]> _items = [];
    public Dictionary<string, int> FailNext { get; } = [];   // operation name -> status to return once
    /// <summary>A locked keychain: every call fails until it is unlocked again (the items stay).</summary>
    public bool Locked { get; set; }
    public int AddCalls, UpdateCalls, CopyCalls, DeleteCalls;
    public List<string> Labels { get; } = [];

    public IReadOnlyDictionary<(string, string), byte[]> Items => _items;

    public void Put(string service, string account, byte[] raw) => _items[(service, account)] = raw;

    private bool Fails(string op, out int status)
    {
        status = 0;
        if (Locked)
        {
            status = KeychainStatus.InteractionNotAllowed;
            return true;
        }
        return FailNext.Remove(op, out status);
    }

    public int Add(string service, string account, string label, byte[] data)
    {
        AddCalls++;
        if (Fails("add", out var s)) return s;
        if (_items.ContainsKey((service, account))) return KeychainStatus.DuplicateItem;
        _items[(service, account)] = (byte[])data.Clone();
        Labels.Add(label);
        return KeychainStatus.Success;
    }

    public int Update(string service, string account, byte[] data)
    {
        UpdateCalls++;
        if (Fails("update", out var s)) return s;
        if (!_items.ContainsKey((service, account))) return KeychainStatus.ItemNotFound;
        _items[(service, account)] = (byte[])data.Clone();
        return KeychainStatus.Success;
    }

    public int Copy(string service, string account, out byte[]? data)
    {
        CopyCalls++;
        data = null;
        if (Fails("copy", out var s)) return s;
        if (!_items.TryGetValue((service, account), out var item)) return KeychainStatus.ItemNotFound;
        data = (byte[])item.Clone();
        return KeychainStatus.Success;
    }

    public int Delete(string service, string account)
    {
        DeleteCalls++;
        if (Fails("delete", out var s)) return s;
        return _items.Remove((service, account)) ? KeychainStatus.Success : KeychainStatus.ItemNotFound;
    }

    public string Describe(int status) => status == KeychainStatus.InteractionNotAllowed ? "User interaction is not allowed." : "";
}

/// <summary>The logic of the Keychain secret store, on a fake Keychain so it runs on any OS. The real Security.framework calls are in KeychainMacTests.</summary>
public class SecretStoreTests
{
    private const string Service = "com.example.test-blind";

    private static (MacOsKeychainSecretStore Store, FakeKeychainBackend Backend) Make()
    {
        var backend = new FakeKeychainBackend();
        return (new MacOsKeychainSecretStore(backend, Service), backend);
    }

    [Fact]
    public void ThreeSecrets_RoundTrip_AndStayApart()
    {
        var (store, _) = Make();
        var seed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var backup = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();
        var pairing = Enumerable.Range(201, 20).Select(i => (byte)i).ToArray();

        store.SaveIdentitySeed(seed);
        store.SaveBackupKey(backup);
        store.SavePairingSecret(pairing);

        store.LoadIdentitySeed().Should().Equal(seed);
        store.LoadBackupKey().Should().Equal(backup);
        store.LoadPairingSecret().Should().Equal(pairing);
    }

    [Fact]
    public void ASecretThatIsNotThere_IsNull_AndNothingIsMadeUp()
    {
        var (store, backend) = Make();

        store.LoadIdentitySeed().Should().BeNull();
        store.LoadBackupKey().Should().BeNull();
        store.LoadPairingSecret().Should().BeNull();
        backend.Items.Should().BeEmpty("a load never creates an item: a missing secret is for the caller to refuse, not for the store to replace");
        backend.AddCalls.Should().Be(0);
    }

    [Fact]
    public void Saving_Again_ReplacesTheSecret_InTheSameItem()
    {
        var (store, backend) = Make();
        store.SaveBackupKey([1, 2, 3, 4]);
        store.SaveBackupKey([9, 9, 9, 9, 9]);

        store.LoadBackupKey().Should().Equal(9, 9, 9, 9, 9);
        backend.Items.Should().HaveCount(1);
        backend.UpdateCalls.Should().Be(1);
    }

    [Fact]
    public void TheItems_AreFiledUnderTheService_OneAccountEach_AndNeverUnderAnotherService()
    {
        var (store, backend) = Make();
        store.SaveIdentitySeed([1]);
        store.SaveBackupKey([2]);
        store.SavePairingSecret([3]);

        backend.Items.Keys.Should().BeEquivalentTo(new[]
        {
            (Service, "identity-seed-v1"), (Service, "backup-key-v1"), (Service, "pairing-secret-v1"),
        });
        backend.Labels.Should().OnlyContain(l => l.StartsWith("Bee Memory Bank blind app"));
    }

    [Fact]
    public void ClearPairingSecret_SpendsOnlyThePairingSecret()
    {
        var (store, _) = Make();
        store.SaveIdentitySeed([1]);
        store.SaveBackupKey([2]);
        store.SavePairingSecret([3]);

        store.ClearPairingSecret();

        store.LoadPairingSecret().Should().BeNull();
        store.LoadIdentitySeed().Should().Equal(1);
        store.LoadBackupKey().Should().Equal(2);
        store.ClearPairingSecret();   // spending a spent secret is not an error
    }

    [Fact]
    public void Clear_ForgetsEverySecret()
    {
        var (store, backend) = Make();
        store.SaveIdentitySeed([1]);
        store.SaveBackupKey([2]);
        store.SavePairingSecret([3]);

        store.Clear();

        backend.Items.Should().BeEmpty();
        store.LoadIdentitySeed().Should().BeNull();
        store.Clear();   // nothing left is fine
    }

    [Fact]
    public void Clear_TriesEveryAccount_EvenWhenOneFails_AndThenReportsIt()
    {
        var (store, backend) = Make();
        store.SaveIdentitySeed([1]);
        store.SaveBackupKey([2]);
        store.SavePairingSecret([3]);
        backend.FailNext["delete"] = KeychainStatus.InteractionNotAllowed;   // the first delete fails

        var act = store.Clear;

        act.Should().Throw<AggregateException>().WithMessage("*could not be removed*");
        backend.DeleteCalls.Should().Be(3, "a wipe that stops at the first failure leaves the rest of the secrets behind");
        backend.Items.Should().HaveCount(1);
    }

    [Fact]
    public void AnItemChangedFromOutside_IsRefused_NotUsed()
    {
        var (store, backend) = Make();
        store.SaveIdentitySeed(Enumerable.Repeat((byte)7, 32).ToArray());
        // somebody puts a plain value into the item (the security tool would do exactly this)
        backend.Put(Service, "identity-seed-v1", Encoding.UTF8.GetBytes("not-the-envelope-and-long-enough-to-pass-a-length-check"));

        var act = () => store.LoadIdentitySeed();

        act.Should().Throw<BlindSecretStoreException>().Where(e => e.Failure == BlindSecretStoreFailure.Corrupt);
    }

    [Fact]
    public void AFlippedBit_InTheSecret_IsRefused()
    {
        var (store, backend) = Make();
        store.SaveBackupKey(Enumerable.Repeat((byte)5, 32).ToArray());
        var raw = backend.Items[(Service, "backup-key-v1")];
        raw[^1] ^= 0x01;

        var act = () => store.LoadBackupKey();

        act.Should().Throw<BlindSecretStoreException>().Where(e => e.Failure == BlindSecretStoreFailure.Corrupt);
    }

    [Fact]
    public void ASecretCopiedOverAnotherPurpose_IsRefused()
    {
        var (store, backend) = Make();
        store.SaveIdentitySeed(Enumerable.Repeat((byte)3, 32).ToArray());
        store.SaveBackupKey(Enumerable.Repeat((byte)4, 32).ToArray());
        // the identity seed's item is copied over the backup key's: both are valid items, but not for that purpose
        backend.Put(Service, "backup-key-v1", (byte[])backend.Items[(Service, "identity-seed-v1")].Clone());

        var act = () => store.LoadBackupKey();

        act.Should().Throw<BlindSecretStoreException>().Where(e => e.Failure == BlindSecretStoreFailure.Corrupt,
            "the item is bound to its account like the Android blob is bound to its AAD");
    }

    [Fact]
    public void ATruncatedItem_IsRefused()
    {
        var (store, backend) = Make();
        backend.Put(Service, "backup-key-v1", [1, 2, 3]);

        var act = () => store.LoadBackupKey();

        act.Should().Throw<BlindSecretStoreException>().Where(e => e.Failure == BlindSecretStoreFailure.Corrupt);
    }

    [Fact]
    public void ALockedKeychain_IsAnError_NotAMissingSecret()
    {
        var (store, backend) = Make();
        store.SaveBackupKey([1, 2, 3]);
        backend.FailNext["copy"] = KeychainStatus.InteractionNotAllowed;

        var act = () => store.LoadBackupKey();

        // null here would read as "the key is lost" and push the person toward a wipe
        act.Should().Throw<BlindSecretStoreException>()
            .Where(e => e.Failure == BlindSecretStoreFailure.Os && e.OsStatus == KeychainStatus.InteractionNotAllowed)
            .WithMessage("*-25308*User interaction is not allowed*");
        store.LoadBackupKey().Should().Equal(new byte[] { 1, 2, 3 }, "the item itself is untouched");
    }

    [Fact]
    public void AFailedSave_IsAnError_AndLeavesTheOldSecret()
    {
        var (store, backend) = Make();
        store.SaveBackupKey([1, 2, 3]);
        backend.FailNext["update"] = KeychainStatus.AuthFailed;

        var act = () => store.SaveBackupKey([4, 5, 6]);

        act.Should().Throw<BlindSecretStoreException>().Where(e => e.OsStatus == KeychainStatus.AuthFailed);
        store.LoadBackupKey().Should().Equal(1, 2, 3);
    }

    [Fact]
    public void TheSecretIsNeverVisibleInTheMessagesOfFailures()
    {
        var (store, backend) = Make();
        var secret = Enumerable.Repeat((byte)0xAB, 32).ToArray();
        backend.FailNext["add"] = KeychainStatus.AuthFailed;

        var act = () => store.SaveIdentitySeed(secret);

        var message = act.Should().Throw<BlindSecretStoreException>().Which.Message;
        message.Should().NotContain(Convert.ToHexString(secret)).And.NotContain(Convert.ToBase64String(secret));
    }

    [Fact]
    public void SealAndOpen_ProtectThePurpose_AndRoundTripEmptyAndLongSecrets()
    {
        var empty = MacOsKeychainSecretStore.Open("a", MacOsKeychainSecretStore.Seal("a", []));
        empty.Should().BeEmpty();
        var long64 = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();
        MacOsKeychainSecretStore.Open("a", MacOsKeychainSecretStore.Seal("a", long64)).Should().Equal(long64);
        var act = () => MacOsKeychainSecretStore.Open("b", MacOsKeychainSecretStore.Seal("a", long64));
        act.Should().Throw<BlindSecretStoreException>();
    }

    [Fact]
    public void TheStore_IsBuiltOnlyWithAService()
    {
        var act = () => new MacOsKeychainSecretStore(new FakeKeychainBackend(), " ");
        act.Should().Throw<ArgumentException>();
    }
}
