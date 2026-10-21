using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.BlindDesktop.Windows;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.BlindDesktop.Tests.Windows;

/// <summary>The AppCore contract of a per-account secret store, run over the real DPAPI files: two accounts never see each other's secrets, clearing one leaves the other, a blob moved into another account's slot does not open.</summary>
public sealed class WindowsSecretStoreContractTests : BlindSecretStoreContract
{
    private readonly string _root = TestFolders.New("dpapi-contract");

    protected override IBlindSecretStoreFactory CreateFactory() => new WindowsSecretStoreFactory(_root);

    protected override IBlindSecretStoreFactory Reopen() => new WindowsSecretStoreFactory(_root);

    protected override void CopyBlob(BlindAccountEntry from, BlindAccountEntry to, BlindAccountNames.Role role)
    {
        var factory = new WindowsSecretStoreFactory(_root);
        factory.WriteBlob(to, role, factory.ReadBlob(from, role) ?? throw new InvalidOperationException("nothing was saved to copy"));
    }
}

/// <summary>The AppCore contract of a per-account state store, run over the real atomic state files.</summary>
public sealed class WindowsStateStoreContractTests : BlindStateStoreContract
{
    private readonly string _root = TestFolders.New("state-contract");

    protected override IBlindStateStoreFactory CreateFactory() => new WindowsStateStoreFactory(_root);

    protected override IBlindStateStoreFactory Reopen() => new WindowsStateStoreFactory(_root);
}

/// <summary>Where each account's keys and state are, and that the 2.6 account's names are byte for byte what 2.6.4 wrote.</summary>
public sealed class WindowsAccountStoresTests
{
    private static readonly BlindAccountEntry Legacy = new(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
    private static readonly BlindAccountEntry A = new("a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", BlindAccountLayouts.Dir, "A");
    private static readonly BlindAccountEntry B = new("b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", BlindAccountLayouts.Dir, "B");

    private static byte[] Secret(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    [Fact]
    public void TheLegacyAccount_KeepsTodaysFilesAndTodaysEntropy_ByteForByte()
    {
        var root = TestFolders.New("legacy-names");
        var factory = new WindowsSecretStoreFactory(root);
        factory.For(Legacy).SaveIdentitySeed(Secret(1));
        factory.For(Legacy).SaveBackupKey(Secret(2));
        factory.For(Legacy).SavePairingSecret(Secret(3));

        Directory.GetFiles(Path.Combine(root, "secrets")).Select(Path.GetFileName).Order()
            .Should().Equal("backup-key.dpapi", "identity-seed.dpapi", "pairing-secret.dpapi");
        // What 2.6.4 wrote opens with the entropy 2.6.4 used, and the 2.6 constructor reads what the factory wrote.
        ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(root, "secrets", "identity-seed.dpapi")), Encoding.UTF8.GetBytes("BeeMemoryBankBlind/v1/identity-seed"),
            DataProtectionScope.CurrentUser).Should().Equal(Secret(1));
        new DpapiSecretStore(Path.Combine(root, "secrets")).LoadBackupKey().Should().Equal(Secret(2));
        new DpapiSecretStore(Path.Combine(root, "secrets")).LoadPairingSecret().Should().Equal(Secret(3));
    }

    [Fact]
    public void ANewAccount_HasItsOwnFolderAndItsIdInTheEntropy()
    {
        var root = TestFolders.New("account-names");
        var factory = new WindowsSecretStoreFactory(root);
        factory.For(A).SaveIdentitySeed(Secret(7));

        var file = Path.Combine(root, "accounts", A.Id, "secrets", "identity-seed.dpapi");
        File.Exists(file).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "secrets")).Should().BeFalse("the root's folder is the 2.6 account's");
        ProtectedData.Unprotect(File.ReadAllBytes(file), Encoding.UTF8.GetBytes($"BeeMemoryBankBlind/v1/{A.Id}/identity-seed"), DataProtectionScope.CurrentUser)
            .Should().Equal(Secret(7));
        var withOldEntropy = () => ProtectedData.Unprotect(File.ReadAllBytes(file), Encoding.UTF8.GetBytes("BeeMemoryBankBlind/v1/identity-seed"), DataProtectionScope.CurrentUser);
        withOldEntropy.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void ClearingAnAccount_RemovesItsBlobsAndItsEmptySecretsFolder_NotTheOthers()
    {
        var root = TestFolders.New("clear");
        var factory = new WindowsSecretStoreFactory(root);
        foreach (var account in new[] { Legacy, A, B })
        {
            factory.For(account).SaveIdentitySeed(Secret(1));
            factory.For(account).SaveBackupKey(Secret(2));
            factory.For(account).SavePairingSecret(Secret(3));
        }

        factory.For(A).Clear();

        Directory.Exists(Path.Combine(root, "accounts", A.Id, "secrets")).Should().BeFalse();
        Directory.GetFiles(Path.Combine(root, "accounts", B.Id, "secrets")).Should().HaveCount(3);
        Directory.GetFiles(Path.Combine(root, "secrets")).Should().HaveCount(3);
    }

    [Fact]
    public void TheStateFile_IsInTheAccountsFolder_AndTheLegacyOneIsTheRootsStateJson()
    {
        var root = TestFolders.New("state-names");
        var factory = new WindowsStateStoreFactory(root);

        factory.For(Legacy).Set("bmb.blind.node_id", "one");
        factory.For(A).Set("bmb.blind.node_id", "two");

        File.ReadAllText(Path.Combine(root, "state.json")).Should().Contain("one").And.NotContain("two");
        File.ReadAllText(Path.Combine(root, "accounts", A.Id, "state.json")).Should().Contain("two").And.NotContain("one");
        factory.For(A).Should().BeSameAs(factory.For(A), "one store object per file: it holds the file's content in memory");
    }

    [Fact]
    public void ErasingAnAccountsState_RemovesItsFile_AndNoOther()
    {
        var root = TestFolders.New("state-erase");
        var factory = new WindowsStateStoreFactory(root);
        factory.For(Legacy).Set("k", "1");
        factory.For(A).Set("k", "2");

        factory.For(A).Erase();

        File.Exists(Path.Combine(root, "accounts", A.Id, "state.json")).Should().BeFalse();
        File.Exists(Path.Combine(root, "state.json")).Should().BeTrue();
    }

    [Fact]
    public void AnIdThatIsNotOneThisAppMade_IsRefused_BeforeAnyPathIsBuilt()
    {
        var factory = new WindowsSecretStoreFactory(TestFolders.New("bad-id"));
        var evil = new BlindAccountEntry("..", BlindAccountLayouts.Dir, "evil");

        var act = () => factory.For(evil).SaveIdentitySeed(Secret(1));

        act.Should().Throw<ArgumentException>("an id becomes a folder name and an entropy string: only what the app made is used");
    }
}
