using System.Security.Cryptography;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Rekey;

namespace BeeMemoryBank.Rekey.Tests;

/// <summary>
/// Review release-b R1-1: the vault lease. Everything that opens the vault holds D/vault.lease shared (a node and its
/// child Api, a standalone Api, a CLI command), the verb holds it exclusively, and each side refuses while the other
/// holds it. Run on Windows and Linux: FileShare is an OS share mode on one and flock on the other.
/// </summary>
public sealed class VaultLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-lease-" + Guid.NewGuid().ToString("N"));
    private readonly string _d;

    public VaultLeaseTests()
    {
        _d = Path.Combine(_root, "vault");
        Directory.CreateDirectory(_d);
        File.WriteAllText(Path.Combine(_d, RekeyRunner.MainDb), "not opened by these tests");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Dictionary<string, string> Tree() =>
        Directory.EnumerateFiles(_d, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != VaultStartup.LeaseFile)
            .ToDictionary(f => Path.GetRelativePath(_d, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    public void TheLease_IsSharedBetweenProcessesThatOpenTheVault_AndExclusiveForTheVerb()
    {
        using var node = VaultStartup.TryAcquireShared(_d);
        using var childApi = VaultStartup.TryAcquireShared(_d);

        node.Should().NotBeNull();
        childApi.Should().NotBeNull("a node and its child Api hold it together");
        VaultStartup.TryAcquireExclusive(_d).Should().BeNull("the verb is refused while anything holds it");
    }

    [Fact]
    public void WhileTheVerbHoldsTheLease_NothingEnters()
    {
        using var verb = VaultStartup.TryAcquireExclusive(_d);

        verb.Should().NotBeNull();
        VaultStartup.TryAcquireShared(_d).Should().BeNull();
        var enter = () => VaultStartup.Enter(_d);
        enter.Should().Throw<VaultInUseException>().WithMessage("*re-key holds the vault*");
    }

    [Fact]
    public void WhileAReKeyRuns_EnterRefusesBeforeItResolvesAnything()
    {
        // A swap in its journal, and the verb that owns it alive: resolving it now would race the verb's renames.
        RekeySwapJournal.Write(_d, new RekeySwapJournal(RekeySwapJournal.NewDirFor(_d), RekeySwapJournal.OldDirFor(_d, DateTimeOffset.UtcNow), RekeySwapJournal.Prepared));
        Directory.CreateDirectory(RekeySwapJournal.NewDirFor(_d));
        using var running = RekeyLock.TryAcquire(_d)!;

        var enter = () => VaultStartup.Enter(_d);

        enter.Should().Throw<VaultInUseException>().WithMessage("*re-key is running*");
        File.Exists(Path.Combine(_d, RekeyRunner.MainDb)).Should().BeTrue("nothing was renamed");
        RekeySwapJournal.Read(_d)!.Phase.Should().Be(RekeySwapJournal.Prepared);
    }

    [Fact]
    public void OnceTheLeaseIsReleased_EveryoneEntersAgain()
    {
        VaultStartup.TryAcquireExclusive(_d)!.Dispose();

        var (resolution, lease) = VaultStartup.Enter(_d);

        using (lease) resolution.DataDir.Should().Be(Path.GetFullPath(_d));
    }

    /// <summary>A running Api (a shared hold) keeps the verb out: exit 3, and D as it was.</summary>
    [Fact]
    public async Task TheVerb_RefusesWhileAnApiHoldsTheLease()
    {
        var before = Tree();
        RekeyOutcome outcome;
        using (VaultStartup.TryAcquireShared(_d)!)
            outcome = await RekeyRunner.RunAsync(new RekeyOptions
            {
                DataDir = _d, OwnerPassword = "irrelevant",
                Steps = RekeyRunner.RequiredSteps.Select(n => (IRekeyStep)new Named(n)).ToList(),
                Preflight = new NoPreflight(),
            });

        outcome.Exit.Should().Be(RekeyExit.FailedBeforeSwap);
        outcome.Message.Should().Contain("in use");
        Tree().Should().BeEquivalentTo(before);
        Directory.Exists(RekeySwapJournal.NewDirFor(_d)).Should().BeFalse();
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
}
