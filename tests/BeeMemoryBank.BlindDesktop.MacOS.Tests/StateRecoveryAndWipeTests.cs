using BeeMemoryBank.BlindDesktop.MacOS;

namespace BeeMemoryBank.BlindDesktop.MacOS.Tests;

/// <summary>
/// Fix round 1, finding 1: setting a damaged state file aside is a courtesy, never a condition. If the copy cannot be made, whatever the
/// reason, the store must still start empty so that the start-up (and with it the recovery of the identity) goes on.
/// </summary>
public class StateStoreRecoveryTests
{
    private const string Marker = "SECRET-LOOKING-VALUE-4711";

    private static Exception Failure(string kind) => kind switch
    {
        "unauthorized" => new UnauthorizedAccessException("Access to the path is denied."),
        "io" => new IOException("There is not enough space on the disk."),
        "notsupported" => new NotSupportedException("The path format is not supported."),
        "security" => new System.Security.SecurityException("Request failed."),
        "argument" => new ArgumentException("Illegal characters in path."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("io")]
    [InlineData("notsupported")]
    [InlineData("security")]
    [InlineData("argument")]
    public void ADamagedFile_WhoseCopyCannotBeMade_StillStartsEmpty_AndRecoveryCanGoOn(string kind)
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        File.WriteAllText(path, "{ this is not json " + Marker);
        var store = new MacOsBlindStateStore(path, copyFile: (_, _) => throw Failure(kind), deleteFile: null);

        // the first read is the start-up: it must not throw
        string? first = "not read yet";
        var act = () => first = store.Get("bmb.blind.node_id");

        act.Should().NotThrow();
        first.Should().BeNull();
        store.LoadWarning.Should().Contain("no copy of it could be kept").And.Contain(Failure(kind).GetType().Name);
        store.LoadWarning.Should().NotContain(Marker, "the warning is for a log: nothing from the file goes into it");
        Directory.GetFiles(folder.Path, "*.damaged-*").Should().BeEmpty("the copy failed");

        // and the store is usable: the identity recovery writes its state
        store.Set("bmb.blind.node_id", "recovered");
        new MacOsBlindStateStore(path).Get("bmb.blind.node_id").Should().Be("recovered");
    }

    [Fact]
    public void AFailedCopy_LeavesTheDamagedFileAsItWas_UntilTheStoreWritesAgain()
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        File.WriteAllText(path, "{ broken " + Marker);
        var store = new MacOsBlindStateStore(path, (_, _) => throw new UnauthorizedAccessException(), null);

        store.Get("x").Should().BeNull();

        File.ReadAllText(path).Should().Be("{ broken " + Marker);
    }

    [Fact]
    public void AGoodCopy_IsReportedByName_WithoutTheContent()
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        File.WriteAllText(path, "{ broken " + Marker);
        var store = new MacOsBlindStateStore(path);

        store.Get("x").Should().BeNull();

        var copy = Directory.GetFiles(folder.Path, "blind-state.json.damaged-*").Should().ContainSingle().Which;
        store.LoadWarning.Should().Contain(Path.GetFileName(copy)).And.NotContain(Marker);
        File.ReadAllText(copy).Should().Contain(Marker, "the copy is the evidence, byte for byte");
    }

    [Fact]
    public void AFileThatIsFine_OrAbsent_HasNoWarning()
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        var absent = new MacOsBlindStateStore(path);
        absent.Get("a").Should().BeNull();
        absent.LoadWarning.Should().BeNull();

        absent.Set("a", "1");
        var fine = new MacOsBlindStateStore(path);
        fine.Get("a").Should().Be("1");
        fine.LoadWarning.Should().BeNull();
    }

    [Fact]
    public void InvalidUtf8InAValue_IsDamage_NotACrash()
    {
        using var folder = new TempFolder();
        var path = folder.File("blind-state.json");
        var head = System.Text.Encoding.ASCII.GetBytes("{\"version\":1,\"values\":{\"a\":\"");
        var tail = System.Text.Encoding.ASCII.GetBytes("\"}}");
        File.WriteAllBytes(path, [.. head, 0xFF, 0xFE, .. tail]);
        var store = new MacOsBlindStateStore(path);

        string? value = "not read yet";
        var act = () => value = store.Get("a");

        act.Should().NotThrow();
        value.Should().BeNull();
        Directory.GetFiles(folder.Path, "*.damaged-*").Should().ContainSingle();
    }
}

/// <summary>Fix round 1, finding 2: the host removes its own state file and its damaged copies after "Disconnect and wipe".</summary>
public class StateStoreWipeTests
{
    private static string Write(string folder, string name, string content = "x")
    {
        var path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Wipe_RemovesTheStateFile_TheDamagedCopies_AndLeftoverTemporaryFiles_AndNothingElse()
    {
        using var root = new TempFolder();
        var paths = new MacOsBlindPaths(root.Path);
        var dir = paths.DataDirectory;
        var store = new MacOsBlindStateStore(paths);
        store.Set("bmb.blind.desktop.obsolete_host_state", "present");
        store.Set("bmb.blind.name", "Test Mac");
        var damaged1 = Write(dir, "blind-state.json.damaged-20261004T101010101");
        var damaged2 = Write(dir, "blind-state.json.damaged-20261004T111111111");
        var leftoverTemp = Write(dir, "blind-state.json.0123456789abcdef0123456789abcdef.tmp");
        // things that look alike or live nearby, but are not the state store's
        var database = Write(dir, "beememorybank.db");
        var notes = Write(dir, "notes.txt");
        var backupName = Write(dir, "blind-state.json.bak");
        var longerName = Write(dir, "blind-state.jsonx");
        var inSubfolder = Write(dir, Path.Combine("media", "blind-state.json"));
        var inSubfolderDamaged = Write(dir, Path.Combine("blind-backups", "blind-state.json.damaged-1"));

        store.Wipe();

        File.Exists(store.FilePath).Should().BeFalse();
        File.Exists(damaged1).Should().BeFalse();
        File.Exists(damaged2).Should().BeFalse();
        File.Exists(leftoverTemp).Should().BeFalse();
        foreach (var kept in new[] { database, notes, backupName, longerName, inSubfolder, inSubfolderDamaged })
            File.Exists(kept).Should().BeTrue(Path.GetFileName(kept) + " is not the state store's file");
        store.Get("bmb.blind.desktop.obsolete_host_state").Should().BeNull("the store is empty in memory too");
        store.Get("bmb.blind.name").Should().BeNull();
        new MacOsBlindStateStore(paths).Get("bmb.blind.name").Should().BeNull("and a new instance finds nothing on disk");
    }

    [Fact]
    public void Wipe_NeverTouchesAnotherFolder_NotTheFullAppsEither()
    {
        using var root = new TempFolder();
        var paths = new MacOsBlindPaths(root.Path);
        var store = new MacOsBlindStateStore(paths);
        store.Set("a", "1");
        var fullApp = Path.Combine(root.Path, "BeeMemoryBankData");
        var sibling = Path.Combine(root.Path, "BeeMemoryBankBlind2");
        var theirs = new[]
        {
            Write(fullApp, "blind-state.json", "full app"),
            Write(fullApp, "blind-state.json.damaged-1", "full app"),
            Write(sibling, "blind-state.json", "other"),
            Write(root.Path, "blind-state.json", "parent"),
        };

        store.Wipe();

        File.Exists(store.FilePath).Should().BeFalse();
        foreach (var file in theirs) File.Exists(file).Should().BeTrue(file);
        File.ReadAllText(theirs[0]).Should().Be("full app");
    }

    [Fact]
    public void Wipe_WithNothingToRemove_OrNoFolder_IsFine()
    {
        using var root = new TempFolder();
        new MacOsBlindStateStore(Path.Combine(root.Path, "missing-folder", "blind-state.json")).Wipe();
        new MacOsBlindStateStore(Path.Combine(root.Path, "blind-state.json")).Wipe();
    }

    [Fact]
    public void AfterAWipe_TheStoreWorksAgain_AndWritesOnlyItsOwnFile()
    {
        using var root = new TempFolder();
        var paths = new MacOsBlindPaths(root.Path);
        var store = new MacOsBlindStateStore(paths);
        store.Set("a", "1");
        store.Wipe();

        store.Set("b", "2");

        new MacOsBlindStateStore(paths).Get("b").Should().Be("2");
        new MacOsBlindStateStore(paths).Get("a").Should().BeNull();
        Directory.GetFiles(paths.DataDirectory).Should().ContainSingle().Which.Should().Be(store.FilePath);
    }

    [Fact]
    public void Wipe_TriesEveryFile_WhenOneCannotBeRemoved_AndThenReportsIt()
    {
        using var root = new TempFolder();
        var paths = new MacOsBlindPaths(root.Path);
        var store = new MacOsBlindStateStore(paths.DataDirectory + Path.DirectorySeparatorChar + "blind-state.json", null,
            file => { if (file.EndsWith("damaged-1")) throw new UnauthorizedAccessException("denied"); File.Delete(file); });
        store.Set("a", "1");
        var stuck = Write(paths.DataDirectory, "blind-state.json.damaged-1");
        var other = Write(paths.DataDirectory, "blind-state.json.damaged-2");

        var act = store.Wipe;

        act.Should().Throw<AggregateException>().WithMessage("*damaged-1*");
        File.Exists(stuck).Should().BeTrue();
        File.Exists(other).Should().BeFalse("the others are still removed");
        File.Exists(store.FilePath).Should().BeFalse();
        store.Get("a").Should().BeNull("the store is empty in memory in any case");
    }
}
