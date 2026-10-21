using System.Security.Cryptography;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>
/// The Keychain store's own logic over a fake Keychain (the real Security calls run on the phone): the three secrets round-trip; a missing
/// one is null and is never made up; any other Keychain error is an exception, never "lost"; an item this app did not write, or moved to
/// another account, is refused.
/// </summary>
public class IosKeychainSecretStoreTests
{
    [Fact]
    public void TheThreeSecrets_RoundTrip_EachUnderItsOwnAccount()
    {
        var keychain = new FakeKeychain();
        var store = new IosKeychainSecretStore(keychain);
        var seed = RandomNumberGenerator.GetBytes(32);
        var backup = RandomNumberGenerator.GetBytes(32);
        var pairing = RandomNumberGenerator.GetBytes(20);

        store.SaveIdentitySeed(seed);
        store.SaveBackupKey(backup);
        store.SavePairingSecret(pairing);

        store.LoadIdentitySeed().Should().Equal(seed);
        store.LoadBackupKey().Should().Equal(backup);
        store.LoadPairingSecret().Should().Equal(pairing);
        keychain.Items.Keys.Should().BeEquivalentTo(
            IosKeychainSecretStore.IdentitySeedAccount, IosKeychainSecretStore.BackupKeyAccount, IosKeychainSecretStore.PairingSecretAccount);
        // The item is the envelope (version, digest, secret), not the bare secret.
        keychain.Items[IosKeychainSecretStore.IdentitySeedAccount].Should().HaveCount(1 + 32 + seed.Length).And.NotEqual(seed);
    }

    [Fact]
    public void AMissingSecret_IsNull_AndNothingIsCreated()
    {
        var keychain = new FakeKeychain();
        var store = new IosKeychainSecretStore(keychain);

        store.LoadIdentitySeed().Should().BeNull();
        store.LoadBackupKey().Should().BeNull();
        store.LoadPairingSecret().Should().BeNull();
        keychain.Items.Should().BeEmpty();
        keychain.Writes.Should().Be(0);
    }

    [Theory]
    [InlineData(IosKeychainStatus.InteractionNotAllowed)]
    [InlineData(IosKeychainStatus.MissingEntitlement)]
    [InlineData(-1)]
    public void AKeychainThatDoesNotAnswer_IsAnException_NeverALostKey(int status)
    {
        var keychain = new FakeKeychain();
        var store = new IosKeychainSecretStore(keychain);
        store.SaveBackupKey(RandomNumberGenerator.GetBytes(32));
        keychain.FailWith = status;

        var load = () => store.LoadBackupKey();
        load.Should().Throw<IosKeychainException>().Which.Status.Should().Be(status);
        var save = () => store.SavePairingSecret(RandomNumberGenerator.GetBytes(20));
        save.Should().Throw<IosKeychainException>();
    }

    [Fact]
    public void AnItemThisAppDidNotWrite_OrOneMovedToAnotherAccount_IsRefused()
    {
        var keychain = new FakeKeychain();
        var store = new IosKeychainSecretStore(keychain);
        store.SaveBackupKey(RandomNumberGenerator.GetBytes(32));
        store.SaveIdentitySeed(RandomNumberGenerator.GetBytes(32));

        // the backup key's item copied over the seed's account
        keychain.Items[IosKeychainSecretStore.IdentitySeedAccount] = keychain.Items[IosKeychainSecretStore.BackupKeyAccount].ToArray();
        var moved = () => store.LoadIdentitySeed();
        moved.Should().Throw<IosKeychainException>().WithMessage("*changed or belongs to another purpose*");

        // a bare secret put there from outside
        keychain.Items[IosKeychainSecretStore.BackupKeyAccount] = RandomNumberGenerator.GetBytes(32);
        var bare = () => store.LoadBackupKey();
        bare.Should().Throw<IosKeychainException>();
    }

    [Fact]
    public void Saving_ReplacesTheItemInPlace_NeverDeletesItFirst()
    {
        var keychain = new FakeKeychain();
        var store = new IosKeychainSecretStore(keychain);
        store.SavePairingSecret(RandomNumberGenerator.GetBytes(20));
        var second = RandomNumberGenerator.GetBytes(20);

        store.SavePairingSecret(second);

        keychain.Deletes.Should().Be(0);
        store.LoadPairingSecret().Should().Equal(second);
    }

    [Fact]
    public void ClearingThePairingSecret_AndClear_RemoveTheItems_AndAMissingItemIsNoError()
    {
        var keychain = new FakeKeychain();
        var store = new IosKeychainSecretStore(keychain);
        store.SaveIdentitySeed(RandomNumberGenerator.GetBytes(32));
        store.SavePairingSecret(RandomNumberGenerator.GetBytes(20));

        store.ClearPairingSecret();
        store.LoadPairingSecret().Should().BeNull();
        store.ClearPairingSecret();

        store.Clear();
        keychain.Items.Should().BeEmpty();
    }

    [Fact]
    public void Clear_TriesEveryItem_AndReportsTheFailuresTogether()
    {
        var keychain = new FakeKeychain();
        var store = new IosKeychainSecretStore(keychain);
        store.SaveIdentitySeed(RandomNumberGenerator.GetBytes(32));
        store.SaveBackupKey(RandomNumberGenerator.GetBytes(32));
        keychain.FailDeleteOf = IosKeychainSecretStore.IdentitySeedAccount;

        var clear = () => store.Clear();

        // fixed words and the status number: the item's name holds the account's id and is never said (fix round 3)
        var thrown = clear.Should().Throw<AggregateException>().Which;
        thrown.Message.Should().Contain("did not remove a secret of this copy").And.Contain("OSStatus -25291").And.NotContain("identity-seed-v1");
        thrown.InnerExceptions.Should().ContainSingle().Which.Message.Should().NotContain("identity-seed-v1");
        keychain.Items.Keys.Should().Equal(IosKeychainSecretStore.IdentitySeedAccount);
    }

    [Fact]
    public void TheLegacyAccount_KeepsThe26ItemNamesByteForByte_AndTheConstantsAreWhatTheContractSays()
    {
        // An upgrade moves nothing: the items of the 2.6 install are found where they were, under the names the old app gave them.
        var legacy = new BlindAccountEntry(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
        BlindAccountNames.KeychainAccount(legacy, BlindAccountNames.Role.IdentitySeed).Should().Be("identity-seed-v1").And.Be(IosKeychainSecretStore.IdentitySeedAccount);
        BlindAccountNames.KeychainAccount(legacy, BlindAccountNames.Role.PairingSecret).Should().Be("pairing-secret-v1").And.Be(IosKeychainSecretStore.PairingSecretAccount);
        BlindAccountNames.KeychainAccount(legacy, BlindAccountNames.Role.BackupKey).Should().Be("backup-key-v1").And.Be(IosKeychainSecretStore.BackupKeyAccount);
        IosKeychainSecretStore.DefaultService.Should().Be("com.beememorybank.blind");

        // A store made for the legacy entry and the one the 2.6 code made (no account at all) write the very same items.
        var keychain = new FakeKeychain();
        var secret = RandomNumberGenerator.GetBytes(32);
        new IosKeychainSecretStoreFactory(keychain).For(legacy).SaveIdentitySeed(secret);
        new IosKeychainSecretStore(keychain).LoadIdentitySeed().Should().Equal(secret);
        keychain.Items.Keys.Should().Equal("identity-seed-v1");
    }

    [Fact]
    public void ANewAccount_FilesItsItemsUnderItsOwnId_AndRemovingItDeletesThoseAndNoOthers()
    {
        var keychain = new FakeKeychain();
        var factory = new IosKeychainSecretStoreFactory(keychain);
        var legacy = new BlindAccountEntry(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
        var a = new BlindAccountEntry("a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", BlindAccountLayouts.Dir, "A");
        var b = new BlindAccountEntry("b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", BlindAccountLayouts.Dir, "B");
        foreach (var account in new[] { legacy, a, b })
        {
            var store = factory.For(account);
            store.SaveIdentitySeed(RandomNumberGenerator.GetBytes(32));
            store.SaveBackupKey(RandomNumberGenerator.GetBytes(32));
            store.SavePairingSecret(RandomNumberGenerator.GetBytes(20));
        }

        keychain.Items.Keys.Should().BeEquivalentTo(
            "identity-seed-v1", "backup-key-v1", "pairing-secret-v1",
            a.Id + "/identity-seed-v1", a.Id + "/backup-key-v1", a.Id + "/pairing-secret-v1",
            b.Id + "/identity-seed-v1", b.Id + "/backup-key-v1", b.Id + "/pairing-secret-v1");

        factory.For(a).Clear();

        keychain.Items.Keys.Should().NotContain(k => k.StartsWith(a.Id, StringComparison.Ordinal));
        keychain.Items.Should().HaveCount(6, "the other accounts' items, account 1 among them, are untouched");
    }

    internal sealed class FakeKeychain : IIosKeychain
    {
        public Dictionary<string, byte[]> Items { get; } = [];
        public int? FailWith { get; set; }
        public string? FailDeleteOf { get; set; }
        public int Writes { get; private set; }
        public int Deletes { get; private set; }

        public int Read(string account, out byte[]? data)
        {
            data = null;
            if (FailWith is { } status) return status;
            if (!Items.TryGetValue(account, out var item)) return IosKeychainStatus.ItemNotFound;
            data = item.ToArray();
            return IosKeychainStatus.Success;
        }

        public int Write(string account, byte[] data)
        {
            if (FailWith is { } status) return status;
            Writes++;
            Items[account] = data.ToArray();
            return IosKeychainStatus.Success;
        }

        public int Delete(string account)
        {
            if (FailWith is { } status) return status;
            if (account == FailDeleteOf) return -25291;
            Deletes++;
            return Items.Remove(account) ? IosKeychainStatus.Success : IosKeychainStatus.ItemNotFound;
        }
    }
}

/// <summary>The contract every head's secret store runs, over the iPhone's Keychain items (a fake Keychain below the real store logic): two accounts never see each other's secrets, clearing one leaves the other, a restart finds what was saved, and an item copied into another account's slot is refused.</summary>
public sealed class IosKeychainSecretStoreFactoryContractTests : BlindSecretStoreContract
{
    private IosKeychainSecretStoreTests.FakeKeychain _keychain = new();

    protected override IBlindSecretStoreFactory CreateFactory()
    {
        _keychain = new IosKeychainSecretStoreTests.FakeKeychain();
        return new IosKeychainSecretStoreFactory(_keychain);
    }

    // The Keychain is what survives a restart; the store objects hold nothing.
    protected override IBlindSecretStoreFactory Reopen() => new IosKeychainSecretStoreFactory(_keychain);

    protected override void CopyBlob(BlindAccountEntry from, BlindAccountEntry to, BlindAccountNames.Role role) =>
        _keychain.Items[BlindAccountNames.KeychainAccount(to, role)] = _keychain.Items[BlindAccountNames.KeychainAccount(from, role)].ToArray();
}

/// <summary>The contract every head's state store runs, over the iPhone's state files (<c>blind-state.json</c> in each account's folder).</summary>
public sealed class IosBlindStateStoreFactoryContractTests : BlindStateStoreContract, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-ios-state-" + Guid.NewGuid().ToString("N"));

    protected override IBlindStateStoreFactory CreateFactory() => new IosBlindStateStoreFactory(_root);

    protected override IBlindStateStoreFactory Reopen() => new IosBlindStateStoreFactory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
