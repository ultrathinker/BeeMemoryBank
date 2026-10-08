using System.Security.Cryptography;
using BeeMemoryBank.BlindIos.Services;

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

        clear.Should().Throw<AggregateException>().WithMessage("*identity-seed-v1*");
        keychain.Items.Keys.Should().Equal(IosKeychainSecretStore.IdentitySeedAccount);
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
