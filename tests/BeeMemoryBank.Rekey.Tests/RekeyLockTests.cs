using System.Security.Cryptography;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Rekey;

namespace BeeMemoryBank.Rekey.Tests;

/// <summary>
/// The locks of the verb (rekey-offline.md §2 step 0, §4 auth): the verb refuses while a node holds node.lock, a
/// normal start refuses while the verb holds the re-key lock, and every refusal leaves D as it was. Run on Windows and
/// Linux: FileShare.None is an OS lock on one and an flock on the other.
/// </summary>
public sealed class RekeyLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-lock-" + Guid.NewGuid().ToString("N"));
    private readonly string _d;
    private readonly Dictionary<string, string> _tree;

    public RekeyLockTests()
    {
        _d = Path.Combine(_root, "vault");
        Directory.CreateDirectory(_d);
        File.WriteAllText(Path.Combine(_d, RekeyRunner.MainDb), "not opened by these tests");
        _tree = Tree(_d);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static Dictionary<string, string> Tree(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(dir, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    /// <summary>What the node does in DirectoryLock.Acquire.</summary>
    private FileStream HoldNodeLock() => new(Path.Combine(_d, "node.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
        FileShare.None, 1, FileOptions.DeleteOnClose);

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

    private Task<RekeyOutcome> RunAsync(IReadOnlyList<IRekeyStep>? steps = null) => RekeyRunner.RunAsync(new RekeyOptions
    {
        DataDir = _d, OwnerPassword = "irrelevant",
        Steps = steps ?? RekeyRunner.RequiredSteps.Select(n => (IRekeyStep)new Named(n)).ToList(),
        Preflight = new NoPreflight(),
    });

    private void AssertUntouched()
    {
        Tree(_d).Should().BeEquivalentTo(_tree, "D is as it was");
        Directory.Exists(RekeySwapJournal.NewDirFor(_d)).Should().BeFalse("no rekey-new is created");
        File.Exists(RekeySwapJournal.LockPathFor(_d)).Should().BeFalse("a refused run leaves no re-key lock behind");
    }

    [Fact]
    public async Task TheVerb_RefusesWhileANodeHoldsNodeLock()
    {
        RekeyOutcome outcome;
        using (HoldNodeLock())
            outcome = await RunAsync();

        outcome.Exit.Should().Be(RekeyExit.FailedBeforeSwap);
        outcome.Message.Should().Contain("node is running");
        AssertUntouched();
    }

    [Fact]
    public async Task TheVerb_RefusesWhileAnotherReKeyHoldsTheLock()
    {
        RekeyOutcome outcome;
        using (RekeyLock.TryAcquire(_d)!)
            outcome = await RunAsync();

        outcome.Exit.Should().Be(RekeyExit.FailedBeforeSwap);
        outcome.Message.Should().Contain("Another re-key");
        Tree(_d).Should().BeEquivalentTo(_tree);
    }

    [Fact]
    public async Task TheVerb_FailsClosed_WhenThePlanLacksAStep()
    {
        var outcome = await RunAsync(RekeyRunner.RequiredSteps.Where(n => n != "KeyMaterial").Select(n => (IRekeyStep)new Named(n)).ToList());

        outcome.Exit.Should().Be(RekeyExit.FailedBeforeSwap);
        outcome.Message.Should().Contain("KeyMaterial");
        AssertUntouched();
    }

    [Fact]
    public async Task TheVerb_RefusesWhileASwapIsPending()
    {
        RekeySwapJournal.Write(_d, new RekeySwapJournal(RekeySwapJournal.NewDirFor(_d), RekeySwapJournal.OldDirFor(_d, DateTimeOffset.UtcNow), RekeySwapJournal.Prepared));

        var outcome = await RunAsync();

        outcome.Exit.Should().Be(RekeyExit.SwapPending);
        Tree(_d).Should().BeEquivalentTo(_tree);
    }

    [Fact]
    public void ANormalStart_RefusesWhileTheReKeyLockIsHeld_AndGoesOnOnceItIsNot()
    {
        var held = RekeyLock.TryAcquire(_d)!;
        RekeyLock.IsHeld(_d).Should().BeTrue();
        RekeyLock.StartRefusal(RekeySwapResolver.Resolve(_d)).Should().NotBeNull().And.Contain("re-key is running");
        RekeyLock.TryAcquire(_d).Should().BeNull("one re-key at a time");

        held.Dispose();

        RekeyLock.IsHeld(_d).Should().BeFalse("a lock file nobody holds is what a dead verb leaves");
        RekeyLock.StartRefusal(RekeySwapResolver.Resolve(_d)).Should().BeNull();
    }

    [Fact]
    public void TheNodeLock_IsExclusive_BetweenTheVerbAndANode()
    {
        using (HoldNodeLock())
            RekeyLock.TryAcquireNodeLock(_d).Should().BeNull("a running node keeps the verb out");
        using (RekeyLock.TryAcquireNodeLock(_d)!)
        {
            var node = () => HoldNodeLock();
            node.Should().Throw<IOException>("the verb keeps a node from starting");
        }
    }
}
