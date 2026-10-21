using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.TestSupport;
using Xunit;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// The Android head's secret store runs the CONTRACT every head runs (<see cref="BlindSecretStoreContract"/>): two accounts never see each other's secrets, clearing one leaves the other, what was saved is there after
/// a restart, and a blob copied into another account's slot does not open there. The platform key is a software one (<see cref="SoftwareKeyWrap"/>); the Keystore calls themselves are one small class
/// (<c>AndroidKeystoreKeyWrap</c>) that only a phone can run.
/// </summary>
public sealed class KeystoreSecretStoreContractTests : BlindSecretStoreContract, IDisposable
{
    private readonly TempRoot _root = new();
    private readonly SoftwareKeyWrap _keystore = new();

    public void Dispose() => _root.Dispose();

    private KeystoreBlindSecretStoreFactory Factory()
    {
        // The host makes an account's folder before its first key; the contract's accounts are created here the same way.
        foreach (var account in new[] { A, B }) Directory.CreateDirectory(BlindAccountNames.DataDirectory(_root.Path, account));
        return new KeystoreBlindSecretStoreFactory(_root.Path, _keystore);
    }

    protected override IBlindSecretStoreFactory CreateFactory() => Factory();

    protected override IBlindSecretStoreFactory Reopen() => Factory();

    protected override void CopyBlob(BlindAccountEntry from, BlindAccountEntry to, BlindAccountNames.Role role)
    {
        var factory = Factory();
        factory.WriteBlob(to, role, factory.ReadBlob(from, role) ?? throw new InvalidOperationException("nothing to copy"));
    }
}

/// <summary>The state store runs the state contract over SharedPreferences files (here a dictionary of dictionaries): own file per account, erase removes that file only, persistence across a restart.</summary>
public sealed class PreferencesStateStoreContractTests : BlindStateStoreContract
{
    private readonly FakePreferences _preferences = new();

    protected override IBlindStateStoreFactory CreateFactory() => _preferences.Factory();

    protected override IBlindStateStoreFactory Reopen() => _preferences.Factory();
}

/// <summary>The NAMING CONTRACT as the Android head applies it: what the Keystore, the files and the preferences are asked for, for the account of a 2.6 install (unchanged byte for byte) and for a new one.</summary>
public sealed class BlindHeadNamingTests : IDisposable
{
    private static readonly BlindAccountEntry Legacy = new(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
    private static readonly BlindAccountEntry Other = new("c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3", BlindAccountLayouts.Dir, "Other");
    private static readonly byte[] Secret = Enumerable.Repeat((byte)7, 32).ToArray();

    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    private void MakeFolders() => Directory.CreateDirectory(BlindAccountNames.DataDirectory(_root.Path, Other));

    [Fact]
    public void TheAccountOfA26Install_KeepsTodaysAliasesAssociatedDataAndFiles_ByteForByte()
    {
        var keystore = new SoftwareKeyWrap();
        var store = new KeystoreBlindSecretStoreFactory(_root.Path, keystore).For(Legacy);

        store.SaveIdentitySeed(Secret);
        store.SavePairingSecret(Secret);
        store.SaveBackupKey(Secret);

        keystore.Wraps.Should().BeEquivalentTo(new[]
        {
            ("bmb_blind_seed_v1", "bmb-blind-identity-seed-v1"),
            ("bmb_blind_pairing_v1", "bmb-blind-pairing-v1"),
            ("bmb_blind_backup_v1", "bmb-blind-backup-key-v1"),
        }, "these are the names 2.6.4 used; an upgrade must find its keys where they were");
        File.Exists(_root.Combine("bmb_blind_seed.bin")).Should().BeTrue("the blobs of account 1 stay in the root");
        File.Exists(_root.Combine("bmb_blind_pairing.bin")).Should().BeTrue();
        File.Exists(_root.Combine("bmb_blind_backup.bin")).Should().BeTrue();
        Directory.Exists(_root.Combine("accounts")).Should().BeFalse("saving the keys of account 1 makes no folder for accounts");
    }

    [Fact]
    public void ANewAccount_GetsAliasesAndAssociatedDataThatCarryItsId_AndItsBlobsInItsOwnKeysFolder()
    {
        MakeFolders();
        var keystore = new SoftwareKeyWrap();
        var store = new KeystoreBlindSecretStoreFactory(_root.Path, keystore).For(Other);

        store.SaveIdentitySeed(Secret);
        store.SavePairingSecret(Secret);
        store.SaveBackupKey(Secret);

        keystore.Wraps.Should().BeEquivalentTo(new[]
        {
            ($"bmb_blind_seed_v1.{Other.Id}", $"bmb-blind-identity-seed-v1/{Other.Id}"),
            ($"bmb_blind_pairing_v1.{Other.Id}", $"bmb-blind-pairing-v1/{Other.Id}"),
            ($"bmb_blind_backup_v1.{Other.Id}", $"bmb-blind-backup-key-v1/{Other.Id}"),
        });
        foreach (var file in new[] { "bmb_blind_seed.bin", "bmb_blind_pairing.bin", "bmb_blind_backup.bin" })
            File.Exists(_root.Combine("accounts", Other.Id, "keys", file)).Should().BeTrue(file);
        Directory.GetFiles(_root.Path, "bmb_blind_*", SearchOption.TopDirectoryOnly).Should().BeEmpty("nothing of a new account is written in the root");
    }

    [Fact]
    public void RemovingAnAccountsKeys_DeletesItsKeystoreEntries_AndItsBlobs_AndNothingOfAnotherAccount()
    {
        MakeFolders();
        var keystore = new SoftwareKeyWrap();
        var factory = new KeystoreBlindSecretStoreFactory(_root.Path, keystore);
        foreach (var account in new[] { Legacy, Other })
        {
            var store = factory.For(account);
            store.SaveIdentitySeed(Secret);
            store.SavePairingSecret(Secret);
            store.SaveBackupKey(Secret);
        }

        factory.For(Other).Clear();

        keystore.Deleted.Should().BeEquivalentTo(
            [$"bmb_blind_seed_v1.{Other.Id}", $"bmb_blind_pairing_v1.{Other.Id}", $"bmb_blind_backup_v1.{Other.Id}"],
            "the Keystore entries of the removed account go, so that a leftover blob cannot be opened");
        Directory.GetFiles(_root.Combine("accounts", Other.Id, "keys")).Should().BeEmpty();
        keystore.Keys.Keys.Should().BeEquivalentTo(["bmb_blind_seed_v1", "bmb_blind_pairing_v1", "bmb_blind_backup_v1"], "account 1's keys are untouched");
        factory.For(Legacy).LoadIdentitySeed().Should().Equal(Secret);
    }

    [Fact]
    public void ABlobOfOneAccount_DoesNotOpenInAnother_EvenUnderTheSameKeystoreKey_BecauseOfItsAssociatedData()
    {
        MakeFolders();
        // One key for every alias: only the AAD (…/<id>) can tell the accounts apart now.
        var keystore = new SoftwareKeyWrap { OneKeyForAllAliases = true };
        var factory = new KeystoreBlindSecretStoreFactory(_root.Path, keystore);
        factory.For(Legacy).SaveBackupKey(Secret);
        factory.WriteBlob(Other, BlindAccountNames.Role.BackupKey, factory.ReadBlob(Legacy, BlindAccountNames.Role.BackupKey)!);

        var open = () => factory.For(Other).LoadBackupKey();

        open.Should().Throw<CryptographicException>("the blob was written for account 1's associated data");
    }

    [Fact]
    public void ASecretSavedAfterTheAccountWasRemoved_DoesNotBringItsFolderBack()
    {
        var keystore = new SoftwareKeyWrap();
        var store = new KeystoreBlindSecretStoreFactory(_root.Path, keystore).For(Other);

        var save = () => store.SaveBackupKey(Secret);

        save.Should().Throw<DirectoryNotFoundException>();
        Directory.Exists(_root.Combine("accounts")).Should().BeFalse();
    }

    [Fact]
    public void ACutOffBlob_IsADamagedSecretWithASentence_NotASlicingException()
    {
        var keystore = new SoftwareKeyWrap();
        var factory = new KeystoreBlindSecretStoreFactory(_root.Path, keystore);
        factory.For(Legacy).SaveIdentitySeed(Secret);
        File.WriteAllBytes(_root.Combine("bmb_blind_seed.bin"), [1, 2, 3]);

        var open = () => factory.For(Legacy).LoadIdentitySeed();

        open.Should().Throw<InvalidDataException>().WithMessage("*damaged*");
    }

    [WindowsOnlyFact]
    public void ClearingStopsAtNothing_EveryRoleIsTried_AndWhatFailedIsSaid()
    {
        MakeFolders();
        var keystore = new SoftwareKeyWrap();
        var factory = new KeystoreBlindSecretStoreFactory(_root.Path, keystore);
        var store = factory.For(Other);
        store.SaveIdentitySeed(Secret);
        store.SavePairingSecret(Secret);
        store.SaveBackupKey(Secret);
        var keys = _root.Combine("accounts", Other.Id, "keys");

        // A file held open by someone cannot be deleted on Windows.
        using (new FileStream(Path.Combine(keys, "bmb_blind_seed.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var clear = () => store.Clear();

            clear.Should().Throw<AggregateException>("a role that cannot be cleared is reported, not hidden");
        }
        File.Exists(Path.Combine(keys, "bmb_blind_pairing.bin")).Should().BeFalse("the other roles were still removed");
        File.Exists(Path.Combine(keys, "bmb_blind_backup.bin")).Should().BeFalse();
    }

    [Fact]
    public void AKeystoreThatDoesNotDeleteAnEntry_MakesClearFail_EveryRoleIsStillTried_AndAnotherClearFinishesIt()
    {
        MakeFolders();
        var keystore = new FlakyKeyWrap();
        var factory = new KeystoreBlindSecretStoreFactory(_root.Path, keystore);
        var store = factory.For(Other);
        store.SaveIdentitySeed(Secret);
        store.SavePairingSecret(Secret);
        store.SaveBackupKey(Secret);
        keystore.FailDelete = true;

        var clear = () => store.Clear();

        var failure = clear.Should().Throw<AggregateException>("a removal that left a Keystore entry behind must not be reported done: the host keeps the account in 'deleting' and runs it again").Which;
        keystore.DeleteAttempts.Should().Be(3, "every role is tried even when the first entry cannot be deleted");
        failure.InnerExceptions.Should().HaveCount(3);
        failure.Message.Should().Contain("identity seed").And.Contain("pairing secret").And.Contain("backup key");
        failure.Message.Should().NotContain(Other.Id, "no alias in a message that reaches a screen").And.NotContain("android.security", "nor the platform's own words");
        keystore.Inner.Keys.Keys.Should().HaveCount(3, "the entries are still there");
        Directory.GetFiles(_root.Combine("accounts", Other.Id, "keys")).Should().BeEmpty("the blobs go first: a key that is left over opens nothing");

        keystore.FailDelete = false;
        store.Clear();

        keystore.Inner.Keys.Should().BeEmpty("the retry confirms every entry is absent");
    }

    [Fact]
    public void ClearingAnAccountThatHasNothingLeft_IsNotAFailure_ItIsTheRetryOfAHalfDoneRemoval()
    {
        MakeFolders();
        var keystore = new FlakyKeyWrap();
        var store = new KeystoreBlindSecretStoreFactory(_root.Path, keystore).For(Other);

        var clear = () => { store.Clear(); store.Clear(); };

        clear.Should().NotThrow();
    }

    [Fact]
    public void SpendingThePairingSecret_FailsWhenItsKeystoreEntryCannotBeDeleted_LikeTheIosKeychainDoes()
    {
        MakeFolders();
        var keystore = new FlakyKeyWrap();
        var store = new KeystoreBlindSecretStoreFactory(_root.Path, keystore).For(Other);
        store.SavePairingSecret(Secret);
        keystore.FailDelete = true;

        var spend = () => store.ClearPairingSecret();

        spend.Should().Throw<AggregateException>();
        store.LoadPairingSecret().Should().BeNull("the blob is gone; the secret cannot be used even though the entry could not be deleted");
    }

    [Fact]
    public void APreferencesFileWhoseCommitFails_MakesErasureFail_AndNothingIsTakenAsErased()
    {
        var preferences = new FakePreferences();
        var store = preferences.Factory().For(Other);
        store.Set("k", "value");
        preferences.CommitSucceeds = false;

        var erase = () => { new BlindPhoneState(store).Clear(); store.Erase(); };

        erase.Should().Throw<IOException>("a commit() that returned false did not write: the state of the account is still on the phone");
        preferences.Files[$"bmb_blind_state.{Other.Id}"].Should().ContainKey("k");
    }

    [Fact]
    public void APreferencesFileThatCannotBeDeleted_MakesErasureFail_AndTheRetryFinishesIt()
    {
        var preferences = new FakePreferences();
        var store = preferences.Factory().For(Other);
        store.Set("k", "value");
        preferences.DeleteSucceeds = false;

        var erase = () => store.Erase();

        erase.Should().Throw<IOException>().WithMessage("*file was not deleted*");
        preferences.Files.Should().ContainKey($"bmb_blind_state.{Other.Id}", "deleteSharedPreferences returned false: the file is still there");

        preferences.DeleteSucceeds = true;
        store.Erase();

        preferences.Files.Should().NotContainKey($"bmb_blind_state.{Other.Id}");
    }

    [Fact]
    public void APreferencesFileWhoseClearThrows_StillGetsItsDeleteAttempted_AndTheEraseFailsWithFixedWords()
    {
        var preferences = new FakePreferences();
        var store = preferences.Factory().For(Other);
        store.Set("k", "value");
        preferences.ClearThrows = true;

        var erase = () => store.Erase();

        var failure = erase.Should().Throw<IOException>("the account stays 'deleting' for the retry").Which;
        preferences.DeleteCalls.Should().Be(1, "every erase step is tried even when the clear threw: the file is asked to go too");
        preferences.Files.Should().NotContainKey($"bmb_blind_state.{Other.Id}", "the delete that was still attempted worked");
        failure.Message.Should().Contain("keys were not cleared").And.NotContain("file was not deleted");
        failure.Message.Should().NotContain("shared_prefs").And.NotContain("android.content", "the platform's own words are not in the message");
        failure.InnerException.Should().BeOfType<InvalidOperationException>("the cause is kept for a debugger, not for a screen");

        preferences.ClearThrows = false;
        store.Erase();
        preferences.DeleteCalls.Should().Be(2, "the retry runs both steps again and finishes");
    }

    [Fact]
    public void WhenBothEraseStepsFail_BothAreReported_OneThrewAndOneAnsweredFalse()
    {
        var preferences = new FakePreferences { ClearThrows = true, DeleteSucceeds = false };
        var store = preferences.Factory().For(Other);
        store.Set("k", "value");

        var erase = () => store.Erase();

        erase.Should().Throw<IOException>().WithMessage("*keys were not cleared and its file was not deleted*");
        preferences.DeleteCalls.Should().Be(1);
    }

    [Fact]
    public void AWriteTheCommitRefused_IsAnErrorWithAFixedSentence_NotASilentSuccess()
    {
        var preferences = new FakePreferences { CommitSucceeds = false };
        var store = preferences.Factory().For(Other);

        var set = () => store.Set("call-code", "SECRET-VALUE");
        var remove = () => store.Set("call-code", null);

        set.Should().Throw<IOException>().Which.Message.Should().NotContain("SECRET-VALUE").And.NotContain("call-code");
        remove.Should().Throw<IOException>();
    }

    [Fact]
    public void ThePreferencesOfAnAccount_AreNamedLikeTheContractSays_AndEraseRemovesThatFileOnly()
    {
        var preferences = new FakePreferences();
        var factory = preferences.Factory();
        factory.For(Legacy).Set("k", "legacy");
        factory.For(Other).Set("k", "other");

        preferences.Files.Keys.Should().BeEquivalentTo(["bmb_blind_state", $"bmb_blind_state.{Other.Id}"]);

        factory.For(Other).Erase();

        preferences.Files.Keys.Should().BeEquivalentTo(["bmb_blind_state"], "the account of a 2.6 install keeps its file");
        factory.For(Legacy).Get("k").Should().Be("legacy");
    }

    [Fact]
    public void ABlobSavedByTheOldSingleAccountCode_OpensThroughTheNewStore()
    {
        // What 2.6.4 wrote: AES-GCM under the key of alias bmb_blind_seed_v1, associated data bmb-blind-identity-seed-v1, the file bmb_blind_seed.bin in the root.
        var key = RandomNumberGenerator.GetBytes(32);
        var keystore = new SoftwareKeyWrap();
        keystore.Keys["bmb_blind_seed_v1"] = key;
        var iv = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[Secret.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(iv, Secret, cipher, tag, Encoding.UTF8.GetBytes("bmb-blind-identity-seed-v1"));
        File.WriteAllBytes(_root.Combine("bmb_blind_seed.bin"), [.. iv, .. cipher, .. tag]);

        new KeystoreBlindSecretStoreFactory(_root.Path, keystore).For(Legacy).LoadIdentitySeed().Should().Equal(Secret);
    }
}
