using System.Text;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>The naming contract the heads implement: the legacy account keeps today's names letter for letter, every other account's names carry its id.</summary>
public sealed class BlindAccountNamesTests
{
    private static readonly BlindAccountEntry Legacy = new(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
    private static readonly BlindAccountEntry A = new("a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", BlindAccountLayouts.Dir, "A");
    private static readonly BlindAccountEntry B = new("b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", BlindAccountLayouts.Dir, "B");

    [Fact]
    public void TheLegacyAccount_KeepsTheNamesOf26_LetterForLetter()
    {
        // Android (KeystoreBlindPhoneKeys in 2.6.4)
        BlindAccountNames.AndroidAlias(Legacy, BlindAccountNames.Role.IdentitySeed).Should().Be("bmb_blind_seed_v1");
        BlindAccountNames.AndroidAlias(Legacy, BlindAccountNames.Role.PairingSecret).Should().Be("bmb_blind_pairing_v1");
        BlindAccountNames.AndroidAlias(Legacy, BlindAccountNames.Role.BackupKey).Should().Be("bmb_blind_backup_v1");
        BlindAccountNames.AndroidBlobFile(BlindAccountNames.Role.IdentitySeed).Should().Be("bmb_blind_seed.bin");
        BlindAccountNames.AndroidBlobFile(BlindAccountNames.Role.PairingSecret).Should().Be("bmb_blind_pairing.bin");
        BlindAccountNames.AndroidBlobFile(BlindAccountNames.Role.BackupKey).Should().Be("bmb_blind_backup.bin");
        Encoding.UTF8.GetString(BlindAccountNames.AndroidAad(Legacy, BlindAccountNames.Role.IdentitySeed)).Should().Be("bmb-blind-identity-seed-v1");
        Encoding.UTF8.GetString(BlindAccountNames.AndroidAad(Legacy, BlindAccountNames.Role.PairingSecret)).Should().Be("bmb-blind-pairing-v1");
        Encoding.UTF8.GetString(BlindAccountNames.AndroidAad(Legacy, BlindAccountNames.Role.BackupKey)).Should().Be("bmb-blind-backup-key-v1");
        BlindAccountNames.AndroidPreferencesName(Legacy).Should().Be("bmb_blind_state");

        // Windows (DpapiSecretStore in 2.6.4)
        BlindAccountNames.WindowsSecretFile(BlindAccountNames.Role.IdentitySeed).Should().Be("identity-seed.dpapi");
        BlindAccountNames.WindowsSecretFile(BlindAccountNames.Role.PairingSecret).Should().Be("pairing-secret.dpapi");
        BlindAccountNames.WindowsSecretFile(BlindAccountNames.Role.BackupKey).Should().Be("backup-key.dpapi");
        Encoding.UTF8.GetString(BlindAccountNames.WindowsEntropy(Legacy, BlindAccountNames.Role.IdentitySeed)).Should().Be("BeeMemoryBankBlind/v1/identity-seed");
        Encoding.UTF8.GetString(BlindAccountNames.WindowsEntropy(Legacy, BlindAccountNames.Role.PairingSecret)).Should().Be("BeeMemoryBankBlind/v1/pairing-secret");
        Encoding.UTF8.GetString(BlindAccountNames.WindowsEntropy(Legacy, BlindAccountNames.Role.BackupKey)).Should().Be("BeeMemoryBankBlind/v1/backup-key");

        // iOS and macOS (IosKeychainSecretStore, MacOsKeychainSecretStore in 2.6.4)
        BlindAccountNames.KeychainService.Should().Be("com.beememorybank.blind");
        BlindAccountNames.KeychainAccount(Legacy, BlindAccountNames.Role.IdentitySeed).Should().Be("identity-seed-v1");
        BlindAccountNames.KeychainAccount(Legacy, BlindAccountNames.Role.PairingSecret).Should().Be("pairing-secret-v1");
        BlindAccountNames.KeychainAccount(Legacy, BlindAccountNames.Role.BackupKey).Should().Be("backup-key-v1");

        BlindAccountNames.StateFile.Should().Be("state.json");
    }

    [Fact]
    public void ANewAccount_CarriesItsId_InEveryNameThePlatformBindsToTheSecret()
    {
        var role = BlindAccountNames.Role.BackupKey;
        BlindAccountNames.AndroidAlias(A, role).Should().Be("bmb_blind_backup_v1." + A.Id);
        Encoding.UTF8.GetString(BlindAccountNames.AndroidAad(A, role)).Should().Be("bmb-blind-backup-key-v1/" + A.Id);
        BlindAccountNames.AndroidPreferencesName(A).Should().Be("bmb_blind_state." + A.Id);
        Encoding.UTF8.GetString(BlindAccountNames.WindowsEntropy(A, role)).Should().Be("BeeMemoryBankBlind/v1/" + A.Id + "/backup-key");
        BlindAccountNames.KeychainAccount(A, role).Should().Be(A.Id + "/backup-key-v1");
    }

    [Fact]
    public void NoTwoAccounts_ShareAnyName_ForAnyRole()
    {
        var accounts = new[] { Legacy, A, B };
        foreach (var role in Enum.GetValues<BlindAccountNames.Role>())
        {
            accounts.Select(a => BlindAccountNames.AndroidAlias(a, role)).Should().OnlyHaveUniqueItems();
            accounts.Select(a => Convert.ToHexString(BlindAccountNames.AndroidAad(a, role))).Should().OnlyHaveUniqueItems();
            accounts.Select(a => Convert.ToHexString(BlindAccountNames.WindowsEntropy(a, role))).Should().OnlyHaveUniqueItems();
            accounts.Select(a => BlindAccountNames.KeychainAccount(a, role)).Should().OnlyHaveUniqueItems();
        }
        accounts.Select(BlindAccountNames.AndroidPreferencesName).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TheFolderOfAnAccount_IsTheRoot_OrDirectlyUnderAccounts()
    {
        BlindAccountNames.DataDirectory("/app", Legacy).Should().Be("/app");
        BlindAccountNames.DataDirectory("/app", A).Should().Be(Path.Combine("/app", "accounts", A.Id));
        BlindAccountNames.DatabasePath("/app", Legacy, "/app/beememorybank.db").Should().Be("/app/beememorybank.db");
        BlindAccountNames.DatabasePath("/app", A, "/app/beememorybank.db").Should().Be(Path.Combine("/app", "accounts", A.Id, "beememorybank.db"));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("a1a1")]
    [InlineData("A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1")]
    [InlineData("legacy")]
    public void AnIdThatIsNotOneThisAppMakes_IsNeverTurnedIntoAPath_OrAName(string id)
    {
        var bad = new BlindAccountEntry(id, BlindAccountLayouts.Dir, "x");
        var folder = () => BlindAccountNames.DataDirectory("/app", bad);
        var alias = () => BlindAccountNames.AndroidAlias(bad, BlindAccountNames.Role.IdentitySeed);
        folder.Should().Throw<ArgumentException>();
        alias.Should().Throw<ArgumentException>();
    }
}

public sealed class InMemorySecretStoreContractTests : BlindSecretStoreContract
{
    private InMemoryBlindSecretStoreFactory _factory = new();

    protected override IBlindSecretStoreFactory CreateFactory() => _factory = new InMemoryBlindSecretStoreFactory();

    protected override IBlindSecretStoreFactory Reopen() => _factory;

    protected override void CopyBlob(BlindAccountEntry from, BlindAccountEntry to, BlindAccountNames.Role role) =>
        _factory.WriteBlob(to, role, _factory.ReadBlob(from, role)!);
}

public sealed class InMemoryStateStoreContractTests : BlindStateStoreContract
{
    private InMemoryBlindStateStoreFactory _factory = new();

    protected override IBlindStateStoreFactory CreateFactory() => _factory = new InMemoryBlindStateStoreFactory();

    protected override IBlindStateStoreFactory Reopen() => _factory;
}

public sealed class FileStateStoreContractTests : BlindStateStoreContract, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-statestore-tests", Guid.NewGuid().ToString("N"));

    protected override IBlindStateStoreFactory CreateFactory() => new FileBlindStateStoreFactory(_root);

    protected override IBlindStateStoreFactory Reopen() => new FileBlindStateStoreFactory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheLegacyAccountsState_IsTheStateJsonInTheRoot_AnotherAccountsIsInItsOwnFolder()
    {
        var factory = new FileBlindStateStoreFactory(_root);
        factory.For(Legacy).Set("k", "1");
        factory.For(A).Set("k", "2");

        File.Exists(Path.Combine(_root, "state.json")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "accounts", A.Id, "state.json")).Should().BeTrue();
    }

    [Fact]
    public void ADamagedStateFile_IsAnError_NotAnEmptyState()
    {
        var factory = new FileBlindStateStoreFactory(_root);
        factory.For(A).Set("k", "1");
        File.WriteAllText(Path.Combine(_root, "accounts", A.Id, "state.json"), "{ not json");

        var act = () => factory.For(A).Get("k");

        act.Should().Throw<InvalidDataException>("an empty state would read as 'not paired' and invite a second pairing over a live copy");
    }
}
