using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Rekey;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review release-b R1-1: an Api that runs without a node (Docker, standalone) holds the vault lease for its whole life,
/// so the offline re-key refuses to run against it; once it is stopped, the lease is gone with it.
/// </summary>
public sealed class RekeyApiLeaseTests
{
    private sealed class Named(string name) : IRekeyStep
    {
        public string Name => name;
        public Task<RekeyStepResult> RunAsync(RekeyContext ctx) => throw new InvalidOperationException("must not run");
        public Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx) => throw new InvalidOperationException("must not run");
    }

    private sealed class NoPreflight : IRekeyPreflight
    {
        public Task<RekeyPreflightReport> RunAsync(string sourceDir, SqliteConnection liveMain, SqliteConnection? liveChat,
            RekeyKeys keys, CancellationToken ct) => throw new InvalidOperationException("must not run");
    }

    private static Task<RekeyOutcome> RunVerbAsync(string d) => RekeyRunner.RunAsync(new RekeyOptions
    {
        DataDir = d, OwnerPassword = "irrelevant",
        Steps = RekeyRunner.RequiredSteps.Select(n => (IRekeyStep)new Named(n)).ToList(),
        Preflight = new NoPreflight(),
    });

    [Fact]
    public async Task ARunningApi_KeepsTheVerbOut_AndItsStopReleasesTheVault()
    {
        var factory = new BmbWebApplicationFactory();
        await factory.InitializeNodeAsync("Owner", "apiLeasePw1");
        var d = factory.DataPath;

        var whileRunning = await RunVerbAsync(d);

        whileRunning.Exit.Should().Be(RekeyExit.FailedBeforeSwap);
        whileRunning.Message.Should().Contain("in use", "the Api holds vault.lease");
        Directory.Exists(RekeySwapJournal.NewDirFor(d)).Should().BeFalse("nothing was copied");
        VaultStartup.TryAcquireExclusive(d).Should().BeNull();

        // The Api stopped: its lease is released with the host (the directory itself is the factory's to remove).
        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }
}
