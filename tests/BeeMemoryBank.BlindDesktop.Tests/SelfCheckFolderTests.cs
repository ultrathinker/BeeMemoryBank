using System.Security.Cryptography;
using BeeMemoryBank.BlindDesktop.Platform;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// <c>--self-check</c> writes into the folder it is given (a state key that is set and erased, a lock file, a socket), so it must never
/// be pointed at the data of a blind app: the folder has to be new or empty, and the platform's default data folder is refused by name.
/// Proved through <see cref="Program.Run"/> (the real start-up path, before any platform exists) and on the rule itself.
/// </summary>
[Collection("ErrorLog")]
public sealed class SelfCheckFolderTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = Program.Run(args, stdout, stderr,
            _ => throw new Xunit.Sdk.XunitException("the platform must not be created when the check is refused"),
            _ => throw new Xunit.Sdk.XunitException("no UI in a check"));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static (int Exit, string Out, string Err) RunWithRealPlatform(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = Program.Run(args, stdout, stderr, PlatformSelector.Create, _ => throw new Xunit.Sdk.XunitException("no UI in a check"));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Every file and folder under <paramref name="root"/> with a hash of its bytes: "untouched byte for byte".</summary>
    private static List<string> Snapshot(string root) =>
        Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(e => Path.GetRelativePath(root, e) + (File.Exists(e) ? " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(e))) : " <folder>"))
            .ToList();

    private static string FolderWith(params string[] relativeFiles)
    {
        var folder = TestFolders.New("selfcheck-folder");
        var random = new Random(7);
        foreach (var relative in relativeFiles)
        {
            var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = new byte[64];
            random.NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
        }
        return folder;
    }

    [Theory]
    [InlineData("beememorybank.db")]
    [InlineData("beememorybank.db-wal")]
    [InlineData("state.json")]
    [InlineData("blind-state.json")]
    [InlineData("blind-state.json.damaged-20261004")]
    [InlineData("blind-log.jsonl")]
    [InlineData("secrets/identity-seed.dpapi")]
    [InlineData("secrets/backup-key.dpapi")]
    [InlineData("notes.txt")]
    public void AFolderThatHoldsAnythingOfABlindApp_IsRefused_BeforeAnythingIsWritten_AndStaysByteForByte(string existing)
    {
        var folder = FolderWith(existing, ".instance.lock");
        var before = Snapshot(folder);

        var (exit, _, err) = Run("--self-check", "--data-dir", folder);

        exit.Should().Be(2);
        err.Should().Contain("--self-check refuses").And.Contain("not empty");
        Snapshot(folder).Should().Equal(before, "nothing was created, changed or removed");
    }

    [Fact]
    public void TheSecondAndThirdFormsOfTheCheck_AreRefusedTheSameWay()
    {
        var folder = FolderWith("beememorybank.db");
        var before = Snapshot(folder);

        Run("--self-check-second-start", "--data-dir", folder).Exit.Should().Be(2);
        Run("--self-check-wait", "5", "--data-dir", folder).Exit.Should().Be(2);
        Run("--self-check", "--data-dir=" + folder).Exit.Should().Be(2);

        Snapshot(folder).Should().Equal(before);
    }

    [Fact]
    public void TheDefaultDataFolder_IsRefusedByName_InAnySpelling_AndNothingIsCreated()
    {
        var real = PlatformSelector.DefaultDataDirectory();
        var existed = Directory.Exists(real);
        var spellings = new[]
        {
            real,
            real + Path.DirectorySeparatorChar,
            real.ToUpperInvariant(),
            Path.Combine(real, "scratch"),
            Path.Combine(real, "..", Path.GetFileName(real)),
            Path.GetDirectoryName(real)!,
        };

        foreach (var spelling in spellings)
        {
            var (exit, _, err) = Run("--self-check", "--data-dir", spelling);

            exit.Should().Be(2, spelling);
            err.Should().Contain("--self-check refuses", spelling);
        }

        Directory.Exists(real).Should().Be(existed, "refusing must not create the real app's folder");
    }

    [Fact]
    public void TheReasonForTheDefaultFolder_NamesIt()
    {
        var real = PlatformSelector.DefaultDataDirectory();

        SelfCheckFolder.Refusal(real, real).Should().Contain("data folder of the blind app itself");
        SelfCheckFolder.Refusal(Path.Combine(real, "x"), real).Should().Contain("inside the data folder of the blind app");
        SelfCheckFolder.Refusal(Path.GetDirectoryName(real)!, real).Should().Contain("contains the data folder");
    }

    [Fact]
    public void ANewOrEmptyFolder_Passes_ThroughTheRealPath_AndTheCheckRuns()
    {
        var empty = TestFolders.New("selfcheck-folder");
        var missing = Path.Combine(TestFolders.New("selfcheck-folder"), "does", "not", "exist");

        var (exit1, out1, err1) = RunWithRealPlatform("--self-check", "--data-dir", empty);
        var (exit2, out2, err2) = RunWithRealPlatform("--self-check", "--data-dir", missing);

        exit1.Should().Be(0, out1 + err1);
        out1.Should().Contain("SELF-CHECK PASSED");
        exit2.Should().Be(0, out2 + err2);
        out2.Should().Contain("SELF-CHECK PASSED");
        err1.Should().BeEmpty();
        err2.Should().BeEmpty();
    }

    [Fact]
    public void TheLockAndSocketFilesOfAnEarlierCheck_AreAllowed_SoTheSameFolderCanBeCheckedAgain()
    {
        var folder = TestFolders.New("selfcheck-folder");

        var first = RunWithRealPlatform("--self-check", "--data-dir", folder);
        var second = RunWithRealPlatform("--self-check", "--data-dir", folder);

        first.Exit.Should().Be(0, first.Out + first.Err);
        second.Exit.Should().Be(0, second.Out + second.Err);
        SelfCheckFolder.Refusal(folder, defaultDataFolder: null).Should().BeNull();
    }

    [Fact]
    public void ACheckWithoutAFolder_ARelativeFolder_AndAFileInsteadOfAFolder_AreRefused()
    {
        Run("--self-check").Exit.Should().Be(2);
        Run("--self-check").Err.Should().Contain("--data-dir");

        var (exit, _, err) = Run("--self-check", "--data-dir", Path.Combine("relative", "scratch"));
        exit.Should().Be(2);
        err.Should().Contain("not an absolute path");

        var file = Path.Combine(TestFolders.New("selfcheck-folder"), "a-file");
        File.WriteAllText(file, "not a folder");
        var (exit2, _, err2) = Run("--self-check", "--data-dir", file);
        exit2.Should().Be(2);
        err2.Should().Contain("is a file");
        File.ReadAllText(file).Should().Be("not a folder");
    }

    [Fact]
    public void TheFullAppsData_IsRefused()
    {
        var folder = TestFolders.New("BeeMemoryBankData");

        SelfCheckFolder.Refusal(folder, defaultDataFolder: null).Should().Contain("full app's data");
    }

    [Fact]
    public void AnUnusablePath_IsRefusedWithAReason_NotWithAnException()
    {
        SelfCheckFolder.Refusal("", null).Should().NotBeNull();
        SelfCheckFolder.Refusal("   ", null).Should().NotBeNull();
        SelfCheckFolder.Refusal(Path.Combine(Path.GetTempPath(), "bad\0name"), null).Should().NotBeNull();
    }

    [Fact]
    public void Control_TheRuleItself_SeesWhatIsInTheFolder()
    {
        // The refusal tests above would pass on a rule that refuses everything; these show it says yes where it should.
        SelfCheckFolder.Refusal(TestFolders.New("selfcheck-folder"), PlatformSelector.DefaultDataDirectory()).Should().BeNull();
        var withLeftovers = FolderWith(".instance.lock", ".instance.sock");
        SelfCheckFolder.Refusal(withLeftovers, PlatformSelector.DefaultDataDirectory()).Should().BeNull();
        SelfCheckFolder.Refusal(FolderWith(".instance.lock", "beememorybank.db"), PlatformSelector.DefaultDataDirectory()).Should().NotBeNull();
    }
}
