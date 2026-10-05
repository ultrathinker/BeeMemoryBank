using System.Security.Cryptography;
using BeeMemoryBank.BlindDesktop.Windows;

namespace BeeMemoryBank.BlindDesktop.Tests.Windows;

public sealed class DpapiSecretStoreTests
{
    private static (DpapiSecretStore Store, string Folder) NewStore()
    {
        var folder = Path.Combine(TestFolders.New("dpapi"), "secrets");
        return (new DpapiSecretStore(folder), folder);
    }

    private static byte[] Bytes(byte seed, int length = 32) => Enumerable.Range(0, length).Select(i => (byte)(seed + i)).ToArray();

    [Fact]
    public void EachSecret_RoundTrips_AndReadsBackAsACopy()
    {
        var (store, _) = NewStore();
        var seed = Bytes(1);
        var backup = Bytes(100);
        var pairing = Bytes(200);

        store.SaveIdentitySeed(seed);
        store.SaveBackupKey(backup);
        store.SavePairingSecret(pairing);

        store.LoadIdentitySeed().Should().Equal(seed);
        store.LoadBackupKey().Should().Equal(backup);
        store.LoadPairingSecret().Should().Equal(pairing);

        var loaded = store.LoadBackupKey()!;
        CryptographicOperations.ZeroMemory(loaded);
        store.LoadBackupKey().Should().Equal(backup, "the caller clears what it was given; the stored secret is not that array");
    }

    [Fact]
    public void ASecretIsSurvivedByANewStoreOverTheSameFolder()
    {
        var (store, folder) = NewStore();
        store.SaveIdentitySeed(Bytes(7));

        new DpapiSecretStore(folder).LoadIdentitySeed().Should().Equal(Bytes(7));
    }

    [Fact]
    public void NothingSaved_ReadsAsNull()
    {
        var (store, _) = NewStore();
        store.LoadIdentitySeed().Should().BeNull();
        store.LoadBackupKey().Should().BeNull();
        store.LoadPairingSecret().Should().BeNull();
    }

    [Fact]
    public void TheBlobOnDisk_IsProtected_AndHoldsNeitherTheKeyNorItsEncodings()
    {
        var (store, folder) = NewStore();
        var key = Bytes(42);
        store.SaveBackupKey(key);

        var blob = File.ReadAllBytes(Directory.GetFiles(folder, "*.dpapi").Single());
        blob.AsSpan().IndexOf(key).Should().Be(-1, "the raw key must not be in the file");
        System.Text.Encoding.ASCII.GetString(blob).Should().NotContain(Convert.ToBase64String(key));
        blob.Length.Should().BeGreaterThan(key.Length, "a DPAPI blob carries a header and a MAC");
    }

    [Fact]
    public void ATamperedBlob_ReadsAsMissing_NotAsAnotherKey_AndNotAsAnException()
    {
        var (store, folder) = NewStore();
        store.SaveIdentitySeed(Bytes(1));
        store.SaveBackupKey(Bytes(2));
        store.SavePairingSecret(Bytes(3));

        foreach (var file in Directory.GetFiles(folder, "*.dpapi"))
        {
            var blob = File.ReadAllBytes(file);
            blob[^5] ^= 0xFF; // inside the MAC / ciphertext at the end
            File.WriteAllBytes(file, blob);
        }

        store.LoadIdentitySeed().Should().BeNull();
        store.LoadBackupKey().Should().BeNull();
        store.LoadPairingSecret().Should().BeNull();
    }

    [Fact]
    public void ATruncatedOrEmptyBlob_ReadsAsMissing()
    {
        var (store, folder) = NewStore();
        store.SaveBackupKey(Bytes(2));
        var file = Directory.GetFiles(folder, "*.dpapi").Single();

        File.WriteAllBytes(file, File.ReadAllBytes(file).Take(10).ToArray());
        store.LoadBackupKey().Should().BeNull();

        File.WriteAllBytes(file, []);
        store.LoadBackupKey().Should().BeNull();
    }

    [Fact]
    public void ABlobSwappedIntoAnotherRole_IsNotAccepted()
    {
        var (store, folder) = NewStore();
        store.SaveIdentitySeed(Bytes(1));
        store.SaveBackupKey(Bytes(2));

        // Each role has its own DPAPI entropy: the identity seed's blob cannot be passed off as the backup key.
        File.Copy(Path.Combine(folder, "identity-seed.dpapi"), Path.Combine(folder, "backup-key.dpapi"), overwrite: true);

        store.LoadBackupKey().Should().BeNull();
        store.LoadIdentitySeed().Should().Equal(Bytes(1));
    }

    [Fact]
    public void SavingAgain_ReplacesTheSecret_AndLeavesNoTempFiles()
    {
        var (store, folder) = NewStore();
        store.SaveBackupKey(Bytes(1));
        store.SaveBackupKey(Bytes(9));

        store.LoadBackupKey().Should().Equal(Bytes(9));
        Directory.GetFiles(folder).Should().OnlyContain(f => f.EndsWith(".dpapi"), "the atomic write leaves only the final file");
    }

    [Fact]
    public void ClearPairingSecret_SpendsOnlyThePairingSecret()
    {
        var (store, _) = NewStore();
        store.SaveIdentitySeed(Bytes(1));
        store.SaveBackupKey(Bytes(2));
        store.SavePairingSecret(Bytes(3));

        store.ClearPairingSecret();

        store.LoadPairingSecret().Should().BeNull();
        store.LoadIdentitySeed().Should().NotBeNull();
        store.LoadBackupKey().Should().NotBeNull();
        store.ClearPairingSecret(); // spending nothing is not an error
    }

    [Fact]
    public void Clear_ForgetsEverySecret_AndRemovesTheirFiles()
    {
        var (store, folder) = NewStore();
        store.SaveIdentitySeed(Bytes(1));
        store.SaveBackupKey(Bytes(2));
        store.SavePairingSecret(Bytes(3));

        store.Clear();

        store.LoadIdentitySeed().Should().BeNull();
        store.LoadBackupKey().Should().BeNull();
        store.LoadPairingSecret().Should().BeNull();
        Directory.Exists(folder).Should().BeFalse("a wipe leaves no key blob behind, and no empty secrets folder either");
        store.Clear(); // twice is fine
    }

    [Fact]
    public void Clear_KeepsAFolderThatHoldsAnythingElse_AndNeverTouchesTheForeignFile()
    {
        var (store, folder) = NewStore();
        store.SaveBackupKey(Bytes(2));
        var foreign = Path.Combine(folder, "notes.txt");
        File.WriteAllText(foreign, "not ours");

        store.Clear();

        File.Exists(foreign).Should().BeTrue("only what the store wrote is removed");
        Directory.GetFiles(folder).Should().Equal(foreign);
        store.LoadBackupKey().Should().BeNull();
    }

    [Fact]
    public void Clear_WithNoFolderAtAll_IsFine_AndLeavesTheParentAlone()
    {
        var parent = TestFolders.New("dpapi");
        var store = new DpapiSecretStore(Path.Combine(parent, "secrets"));

        store.Clear();

        Directory.Exists(parent).Should().BeTrue("only the secrets folder itself is ever removed, never its parent");
    }

    [Fact]
    public async Task ParallelSavesAndLoads_NeverSeeAHalfWrittenSecret()
    {
        var (store, _) = NewStore();
        var a = Bytes(1);
        var b = Bytes(77);
        store.SaveBackupKey(a);

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) store.SaveBackupKey(i % 2 == 0 ? a : b);
        }));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var key = store.LoadBackupKey();
                key.Should().NotBeNull();
                (key!.SequenceEqual(a) || key.SequenceEqual(b)).Should().BeTrue();
            }
        }));

        await Task.WhenAll(writers.Concat(readers));
    }
}
