using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindDesktop.MacOS.Tests;

public class PathsTests
{
    [Fact]
    public void TheDataFolder_IsAFolderOfItsOwn_UnderApplicationSupport()
    {
        using var root = new TempFolder();

        var paths = new MacOsBlindPaths(root.Path);

        paths.DataDirectory.Should().Be(Path.Combine(root.Path, "BeeMemoryBankBlind"));
        paths.DatabasePath.Should().Be(Path.Combine(root.Path, "BeeMemoryBankBlind", "beememorybank.db"),
            "Blind.AppCore's wipe removes beememorybank.db* from the data folder");
        Directory.Exists(paths.DataDirectory).Should().BeTrue();
    }

    [Fact]
    public void TheDefaultRoot_IsLibraryApplicationSupport_OfTheUser()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");

        MacOsBlindPaths.DefaultApplicationSupport().Should().Be(expected);
        new MacOsBlindPaths(createDirectory: false).DataDirectory.Should().Be(Path.Combine(expected, "BeeMemoryBankBlind"));
    }

    [Fact]
    public void NothingIsEverPlacedUnderTheFullAppsFolders()
    {
        var paths = new MacOsBlindPaths(createDirectory: false);

        Path.GetFileName(paths.DataDirectory).Should().Be("BeeMemoryBankBlind");
        Path.GetFileName(paths.DataDirectory).Should().NotBe("BeeMemoryBankData", "that is the full app's data folder");
        paths.DataDirectory.Split(Path.DirectorySeparatorChar).Should().NotContain("BeeMemoryBankData").And.NotContain("BeeMemoryBank");
        paths.DataDirectory.Should().NotBe(Path.Combine(MacOsBlindPaths.DefaultApplicationSupport(), "BeeMemoryBankData"));
    }

    [Fact]
    public void Creating_Twice_IsFine_AndKeepsWhatIsInThere()
    {
        using var root = new TempFolder();
        var first = new MacOsBlindPaths(root.Path);
        File.WriteAllText(Path.Combine(first.DataDirectory, "keep.txt"), "x");

        var second = new MacOsBlindPaths(root.Path);

        File.Exists(Path.Combine(second.DataDirectory, "keep.txt")).Should().BeTrue();
    }

    [Fact]
    public void TheFolder_IsCreatedWithMode0700()
    {
        if (OperatingSystem.IsWindows()) return;   // Unix modes: asserted on the Mac (and any Unix)
        using var root = new TempFolder();

        var paths = new MacOsBlindPaths(root.Path);

        File.GetUnixFileMode(paths.DataDirectory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void AWiderExistingFolder_IsTightenedTo0700()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempFolder();
        var wide = Path.Combine(root.Path, "BeeMemoryBankBlind");
        Directory.CreateDirectory(wide);
        File.SetUnixFileMode(wide, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _ = new MacOsBlindPaths(root.Path);

        File.GetUnixFileMode(wide).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void ABlankRoot_IsRefused()
    {
        var act = () => new MacOsBlindPaths(" ");
        act.Should().Throw<ArgumentException>();
    }
}

public class StateStoreTests
{
    [Fact]
    public void ValuesRoundTrip_AndSurviveANewInstance()
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        var store = new MacOsBlindStateStore(path);

        store.Get("bmb.blind.name").Should().BeNull();
        store.Set("bmb.blind.name", "Office Mac");
        store.Set("bmb.blind.schedule", "Weekly");

        store.Get("bmb.blind.name").Should().Be("Office Mac");
        var reopened = new MacOsBlindStateStore(path);
        reopened.Get("bmb.blind.name").Should().Be("Office Mac");
        reopened.Get("bmb.blind.schedule").Should().Be("Weekly");
    }

    [Fact]
    public void SettingNull_RemovesTheKey_AndThatIsCommittedToo()
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        var store = new MacOsBlindStateStore(path);
        store.Set("a", "1");
        store.Set("b", "2");

        store.Set("a", null);

        new MacOsBlindStateStore(path).Get("a").Should().BeNull();
        new MacOsBlindStateStore(path).Get("b").Should().Be("2");
    }

    [Fact]
    public void TheBlindPhoneState_WorksOnIt_AndClearForgetsIt()
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        var state = new BlindPhoneState(new MacOsBlindStateStore(path));
        var nodeId = Guid.NewGuid();
        state.NodeId = nodeId;
        state.DisplayName = "Mac";
        state.Schedule = BlindBackupSchedule.Daily;
        state.InitialLoadDone = true;

        var again = new BlindPhoneState(new MacOsBlindStateStore(path));
        again.NodeId.Should().Be(nodeId);
        again.Schedule.Should().Be(BlindBackupSchedule.Daily);
        again.InitialLoadDone.Should().BeTrue();

        again.Clear();

        var afterWipe = new BlindPhoneState(new MacOsBlindStateStore(path));
        afterWipe.NodeId.Should().BeNull();
        afterWipe.DisplayName.Should().BeNull();
        afterWipe.InitialLoadDone.Should().BeFalse();
    }

    [Fact]
    public void TheWrite_IsATemporaryFileAndAReplace_NoTemporaryFileIsLeft_AndTheFileIsNeverHalfWritten()
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        var store = new MacOsBlindStateStore(path);
        var seen = new List<string>();

        for (var i = 0; i < 25; i++)
        {
            store.Set("counter", i.ToString());
            // after every Set the file on disk parses and holds exactly the value just set
            seen.Add(new MacOsBlindStateStore(path).Get("counter")!);
        }

        seen.Should().Equal(Enumerable.Range(0, 25).Select(i => i.ToString()));
        Directory.GetFiles(folder.Path).Should().ContainSingle().Which.Should().Be(path, "no .tmp file stays behind");
    }

    [Fact]
    public void AFailedWrite_ThrowsAndLeavesFileAndMemoryAsTheyWere()
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        var store = new MacOsBlindStateStore(path);
        store.Set("k", "old");
        // the target path becomes a directory: the replace cannot succeed
        File.Delete(path);
        Directory.CreateDirectory(path);

        var act = () => store.Set("k", "new");

        act.Should().Throw<Exception>();
        store.Get("k").Should().Be("old", "the in-memory value changes only after the disk write succeeded");
        Directory.GetFiles(folder.Path, "*.tmp").Should().BeEmpty("the failed write cleans up its temporary file");
    }

    [Fact]
    public void ADamagedFile_IsKeptAsACopy_AndTheStoreStartsEmpty()
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        File.WriteAllText(path, "{ this is not json");

        var store = new MacOsBlindStateStore(path);

        store.Get("anything").Should().BeNull();
        Directory.GetFiles(folder.Path, "blind-state.json.damaged-*").Should().ContainSingle();
        File.ReadAllText(path).Should().Be("{ this is not json", "the original is not overwritten until the store writes");
        store.Set("x", "1");
        new MacOsBlindStateStore(path).Get("x").Should().Be("1");
    }

    [Fact]
    public void AFileWithTheWrongShape_IsDamagedToo()
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        File.WriteAllText(path, """{"version":1,"values":{"a":5}}""");

        new MacOsBlindStateStore(path).Get("a").Should().BeNull();
        Directory.GetFiles(folder.Path, "*.damaged-*").Should().ContainSingle();
    }

    [Fact]
    public void ConcurrentWriters_NeverCorruptTheFile()
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        var store = new MacOsBlindStateStore(path);

        Parallel.For(0, 40, i => store.Set("k" + i, "v" + i));

        var reopened = new MacOsBlindStateStore(path);
        for (var i = 0; i < 40; i++) reopened.Get("k" + i).Should().Be("v" + i);
        Directory.GetFiles(folder.Path).Should().ContainSingle();
    }

    [Fact]
    public void UnicodeAndOddValues_Survive()
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        var value = "caf" + (char)0xE9 + " \"quoted\" \\ back\nline " + char.ConvertFromUtf32(0x1F41D);
        new MacOsBlindStateStore(path).Set("name", value);

        new MacOsBlindStateStore(path).Get("name").Should().Be(value);
    }

    [Fact]
    public void TheFile_IsPrivate_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");

        new MacOsBlindStateStore(path).Set("a", "b");

        File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void ItLivesInTheBlindAppsFolder()
    {
        using var root = new TempFolder();
        var paths = new MacOsBlindPaths(root.Path);

        var store = new MacOsBlindStateStore(paths);

        store.FilePath.Should().Be(Path.Combine(paths.DataDirectory, "blind-state.json"));
    }
}
