using System.Diagnostics;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Rekey;

namespace BeeMemoryBank.Rekey.Tests;

/// <summary>
/// Review release-b-fix #2: a vault reached through a link is refused. D itself, or any directory above it, being a
/// junction or symbolic link, the verb stops before it locks, creates or reads anything, and the carry-over refuses such
/// a source. Symbolic links everywhere; on Windows, junctions as well (they need no special right). Where the account may
/// not create a link, the test has nothing to check and returns.
/// </summary>
public sealed class RekeyLinkedRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-linkroot-" + Guid.NewGuid().ToString("N"));
    private readonly string _realParent, _realVault;

    public RekeyLinkedRootTests()
    {
        _realParent = Path.Combine(_root, "real");
        _realVault = Path.Combine(_realParent, "vault");
        Directory.CreateDirectory(_realVault);
        File.WriteAllText(Path.Combine(_realVault, RekeyRunner.MainDb), "not opened by these tests");
    }

    public void Dispose()
    {
        try { NoFollow.DeleteTree(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool Symlink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Junction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        return Directory.Exists(link);
    }

    private sealed class Named(string name) : IRekeyStep
    {
        public string Name => name;
        public Task<RekeyStepResult> RunAsync(RekeyContext ctx) => throw new InvalidOperationException("must not run");
        public Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx) => throw new InvalidOperationException("must not run");
    }

    private sealed class NoPreflight : IRekeyPreflight
    {
        public Task<RekeyPreflightReport> RunAsync(string sourceDir, Microsoft.Data.Sqlite.SqliteConnection liveMain,
            Microsoft.Data.Sqlite.SqliteConnection? liveChat, RekeyKeys keys, CancellationToken ct) => throw new InvalidOperationException("must not run");
    }

    private static Task<RekeyOutcome> RunVerbAsync(string d) => RekeyRunner.RunAsync(new RekeyOptions
    {
        DataDir = d, OwnerPassword = "irrelevant",
        Steps = RekeyRunner.RequiredSteps.Select(n => (IRekeyStep)new Named(n)).ToList(),
        Preflight = new NoPreflight(),
    });

    private void AssertRefusedWithNothingCreated(RekeyOutcome outcome, string d)
    {
        outcome.Exit.Should().Be(RekeyExit.FailedBeforeSwap);
        outcome.Message.Should().Contain("junction or symbolic link");
        foreach (var sibling in new[] { RekeySwapJournal.NewDirFor(d), RekeySwapJournal.LockPathFor(d), VaultStartup.LeasePathFor(d),
                     RekeySwapJournal.NewDirFor(_realVault), RekeySwapJournal.LockPathFor(_realVault) })
            Path.Exists(sibling).Should().BeFalse($"nothing is created: {sibling}");
        Directory.EnumerateFileSystemEntries(_realVault).Select(Path.GetFileName).Should().Equal(RekeyRunner.MainDb);
    }

    [Fact]
    public async Task AVaultThatIsASymbolicLink_IsRefused()
    {
        var d = Path.Combine(_root, "linked-vault");
        if (!Symlink(d, _realVault)) return;

        AssertRefusedWithNothingCreated(await RunVerbAsync(d), d);
    }

    [Fact]
    public async Task AVaultUnderALinkedParent_IsRefused()
    {
        var linkedParent = Path.Combine(_root, "linked-parent");
        if (!Symlink(linkedParent, _realParent)) return;
        var d = Path.Combine(linkedParent, "vault");

        AssertRefusedWithNothingCreated(await RunVerbAsync(d), d);
    }

    [Fact]
    public async Task AVaultThatIsAJunction_IsRefused()
    {
        var d = Path.Combine(_root, "junction-vault");
        if (!Junction(d, _realVault)) return;

        AssertRefusedWithNothingCreated(await RunVerbAsync(d), d);
    }

    [Fact]
    public async Task AVaultUnderAJunction_IsRefused()
    {
        var junctionParent = Path.Combine(_root, "junction-parent");
        if (!Junction(junctionParent, _realParent)) return;
        var d = Path.Combine(junctionParent, "vault");

        AssertRefusedWithNothingCreated(await RunVerbAsync(d), d);
    }

    [Fact]
    public void TheCarryOver_RefusesASourceReachedThroughALink()
    {
        var d = Path.Combine(_root, "linked-vault");
        if (!Symlink(d, _realVault) && !Junction(d, _realVault)) return;
        var newDir = Path.Combine(_root, "new");
        Directory.CreateDirectory(newDir);

        var carry = () => RekeySwap.CarryOver(d, newDir, new List<string>());

        carry.Should().Throw<InvalidOperationException>().WithMessage("*junction or symbolic link*");
    }

    [Fact]
    public void TheRealPath_IsNotALink()
    {
        NoFollow.IsOrUnderLink(_realVault).Should().BeFalse("a plain directory under plain directories passes");
    }
}
