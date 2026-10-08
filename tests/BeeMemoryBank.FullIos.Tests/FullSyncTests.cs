using System.Net;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.FullIos.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// The phone as a sync client, without a network: a round with nobody to call, a peer that does not answer (the round goes on and says
/// why), and the words for each kind of failure. The real exchange with a node is the simulator end-to-end run (tools/ios-e2e).
/// </summary>
public class FullSyncTests
{
    private static FullSync Sync(TestVault test) => new(test.Services, NullLogger<FullSync>.Instance);

    [Fact]
    public async Task WithNoPeer_ARoundCallsNobody()
    {
        var (test, _) = await TestVault.CreateAsync("sync-alone");
        await using var _2 = test;

        var round = await Sync(test).RoundAsync();

        round.Should().Match<SyncRoundResult>(r => r.Reached == 0 && r.Failed == 0 && r.Problem == null);
        (await Sync(test).PeersAsync()).Should().BeEmpty("the node's own row is not a peer");
    }

    [Fact]
    public async Task APeerThatDoesNotAnswer_FailsTheRound_ForThatPeerOnly_AndSaysWhy()
    {
        var (test, _) = await TestVault.CreateAsync("sync-unreachable");
        await using var _2 = test;
        var peer = Guid.NewGuid();
        using (var scope = test.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = peer,
                DisplayName = "Laptop",
                Ed25519PublicKey = new byte[32],
                // Port 1 on loopback: refused at once, nothing listens there.
                ApiAddress = "https://127.0.0.1:1",
                Status = "A",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        var sync = Sync(test);
        var changes = 0;
        sync.Changed += () => changes++;

        var round = await sync.RoundAsync();

        round.Failed.Should().Be(1);
        round.Reached.Should().Be(0);
        round.Problem.Should().StartWith("Laptop: ").And.Contain("not reachable");
        var status = (await sync.PeersAsync()).Should().ContainSingle().Subject;
        status.Should().Match<PeerStatus>(p => p.NodeId == peer && p.LastSuccess == null && p.LastAttempt != null && !p.Pinned);
        status.Summary.Should().Contain("No contact yet").And.Contain("not reachable");
        changes.Should().Be(2, "a round tells the screens when it starts and when it ends");
        sync.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void TheWords_ForAPinnedKeyThatChanged_SayNothingWasSent()
    {
        var pinned = new PeerStatus(Guid.NewGuid(), "PC", "https://192.168.1.5:5311", true, null, null, null);
        var refused = new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established");

        FullSync.Describe(refused, pinned).Should().Contain("another key").And.Contain("nothing was sent");
        FullSync.Describe(refused, pinned with { Pinned = false }).Should().Contain("not trusted");
        FullSync.Describe(new HttpRequestException("no", null, HttpStatusCode.Unauthorized), pinned).Should().Contain("no longer accepts");
        FullSync.Describe(new TaskCanceledException(), pinned).Should().Contain("did not answer");
    }

    [Fact]
    public void ThePeersLine_ShowsTheLastContact_TheKey_AndOnlyANewerProblem()
    {
        var at = new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);
        var fine = new PeerStatus(Guid.NewGuid(), "PC", "https://pc:5311", true, at, at, null);
        fine.Summary.Should().StartWith("Last contact").And.Contain("key pinned");

        var failedLater = fine with { LastAttempt = at.AddMinutes(1), Problem = "not reachable" };
        failedLater.Summary.Should().EndWith("not reachable");
    }
}
