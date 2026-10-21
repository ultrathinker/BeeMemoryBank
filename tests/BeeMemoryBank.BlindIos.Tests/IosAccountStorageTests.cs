using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>A recording stand-in for the iOS file attributes (NSFileManager): which paths were protected, which were left out of the backups.</summary>
internal sealed class FakeFileProtection : IIosFileProtection
{
    public HashSet<string> Protected { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Excluded { get; } = new(StringComparer.Ordinal);

    public void Protect(string path) => Protected.Add(Path.GetFullPath(path));

    public void ExcludeFromBackup(string path) => Excluded.Add(Path.GetFullPath(path));
}

/// <summary>
/// Where each account keeps its state file and which of its folders get the file protection and the no-backup mark: the 2.6 names for account 1, the same names inside the account's own folder for
/// the others, and the 2.6 root's attributes on EVERY account directory.
/// </summary>
public sealed class IosAccountStorageTests : IDisposable
{
    private static readonly BlindAccountEntry Legacy = new(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
    private static readonly BlindAccountEntry A = new("a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", BlindAccountLayouts.Dir, "A");
    private static readonly BlindAccountEntry B = new("b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", BlindAccountLayouts.Dir, "B");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-ios-storage-" + Guid.NewGuid().ToString("N"));

    public IosAccountStorageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Account1_KeepsTheStateFileOfThe26App_AndANewAccountTheSameNameInsideItsFolder()
    {
        IosBlindStateStoreFactory.PathOf(_root, Legacy).Should().Be(Path.Combine(_root, "blind-state.json"));
        IosBlindStateStoreFactory.PathOf(_root, A).Should().Be(Path.Combine(_root, "accounts", A.Id, "blind-state.json"));
        MacOsBlindStateStore.FileName.Should().Be("blind-state.json");
    }

    [Fact]
    public void AStateFileTheOldAppWrote_IsReadByAccount1_AsItIs()
    {
        // The 2.6 app wrote { "version": 1, "values": { ... } } with MacOsBlindStateStore; nothing rewrites it on the way in.
        var path = Path.Combine(_root, "blind-state.json");
        new MacOsBlindStateStore(path).Set("bmb.blind.node_id", "0f7c6a70-0000-4000-8000-000000000001");
        var before = File.ReadAllBytes(path);

        var read = new IosBlindStateStoreFactory(_root).For(Legacy);

        read.Get("bmb.blind.node_id").Should().Be("0f7c6a70-0000-4000-8000-000000000001");
        File.ReadAllBytes(path).Should().Equal(before, "reading changes nothing on disk");
        new IosBlindStateStoreFactory(_root).For(A).Get("bmb.blind.node_id").Should().BeNull("a new account never sees account 1's state");
    }

    [Fact]
    public void TheFactory_HandsTheSameStoreForOneFile_SoTwoObjectsNeverHoldTwoTruths()
    {
        var factory = new IosBlindStateStoreFactory(_root);

        factory.For(A).Should().BeSameAs(factory.For(A));
        factory.For(A).Should().NotBeSameAs(factory.For(B));
        factory.For(A).Should().NotBeSameAs(factory.For(Legacy));
    }

    [Fact]
    public void TheRoot_IsProtectedAndLeftOutOfBackups_AndSoAreTheFilesAlreadyInIt()
    {
        File.WriteAllText(Path.Combine(_root, "beememorybank.db"), "x");
        var files = new FakeFileProtection();

        IosAccountFolders.PrepareRoot(files, _root);

        files.Protected.Should().Contain(Path.GetFullPath(_root)).And.Contain(Path.GetFullPath(Path.Combine(_root, "beememorybank.db")));
        files.Excluded.Should().Contain(Path.GetFullPath(_root));
    }

    [Fact]
    public void EveryAccountFolder_GetsTheAttributesOfTheRoot_TheFoldersAboveItAndTheFilesInIt()
    {
        foreach (var account in new[] { A, B }) Directory.CreateDirectory(Path.Combine(_root, "accounts", account.Id));
        File.WriteAllText(Path.Combine(_root, "accounts", A.Id, "blind-state.json"), "{}");
        var files = new FakeFileProtection();

        IosAccountFolders.PrepareAccount(files, _root, A);
        IosAccountFolders.PrepareAccount(files, _root, B);

        foreach (var folder in new[] { _root, Path.Combine(_root, "accounts"), Path.Combine(_root, "accounts", A.Id), Path.Combine(_root, "accounts", B.Id) })
        {
            files.Protected.Should().Contain(Path.GetFullPath(folder), folder);
            files.Excluded.Should().Contain(Path.GetFullPath(folder), folder);
        }
        files.Protected.Should().Contain(Path.GetFullPath(Path.Combine(_root, "accounts", A.Id, "blind-state.json")));
    }

    [Fact]
    public void AFolderThatIsNotThere_IsSkipped_NotMade()
    {
        // The host makes an account's folder; this only marks what exists (the recovery runtime of a removal that a crash cut off is built over a folder that may be gone).
        var files = new FakeFileProtection();

        IosAccountFolders.PrepareAccount(files, _root, A);

        Directory.Exists(Path.Combine(_root, "accounts")).Should().BeFalse();
        files.Protected.Should().NotContain(Path.GetFullPath(Path.Combine(_root, "accounts", A.Id)));
    }
}
