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

        // A computer that is simply away is not an error: unreachable, not failed.
        round.Failed.Should().Be(0);
        round.Unreachable.Should().Be(1);
        round.UnreachablePeer.Should().Be("Laptop");
        round.Problem.Should().BeNull();
        round.Reached.Should().Be(0);
        var status = (await sync.PeersAsync()).Should().ContainSingle().Subject;
        status.Should().Match<PeerStatus>(p => p.NodeId == peer && p.LastSuccess == null && p.LastAttempt != null && !p.Pinned);
        status.ProblemCode.Should().Be(FullSync.ProblemUnreachable);
        status.Summary.Should().Contain("No contact yet");
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
    public void ThePeersLine_ShowsTheLastContact_AndTheKey_TheProblemIsSaidElsewhere()
    {
        var at = new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);
        var fine = new PeerStatus(Guid.NewGuid(), "PC", "https://pc:5311", true, at, at, null);
        fine.Summary.Should().StartWith("Last contact").And.Contain("key pinned");

        // The problem is a code the screens turn into their own words; the contact line stays clean.
        var failedLater = fine with { LastAttempt = at.AddMinutes(1), ProblemCode = FullSync.ProblemUnreachable };
        failedLater.Summary.Should().StartWith("Last contact").And.NotContain("not reachable");
        failedLater.ProblemCode.Should().Be(FullSync.ProblemUnreachable);
    }

    [Fact]
    public void Classify_AwayIsUnreachable_RefusalAndWrongKeyAreReal()
    {
        FullSync.Classify(new TaskCanceledException(), false).Should().Be(FullSync.ProblemUnreachable);
        FullSync.Classify(new HttpRequestException("no", null, null), false).Should().Be(FullSync.ProblemUnreachable);
        FullSync.Classify(new System.Net.Sockets.SocketException(), false).Should().Be(FullSync.ProblemUnreachable);
        FullSync.Classify(new HttpRequestException("no", null, HttpStatusCode.Unauthorized), false).Should().Be(FullSync.ProblemRefused);
        FullSync.Classify(new HttpRequestException(HttpRequestError.SecureConnectionError, "ssl"), true).Should().Be(FullSync.ProblemWrongKey);
    }

    // ---- 2.12: an administrator disconnected this phone ----

    private static async Task<(TestVault Test, FullSync Sync)> PhoneWithAPeerAsync(string name)
    {
        var (test, _) = await TestVault.CreateAsync(name);
        using (var scope = test.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = Guid.NewGuid(), DisplayName = "Office computer", Ed25519PublicKey = new byte[32], ApiAddress = "https://127.0.0.1:1",
                Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        return (test, Sync(test));
    }

    [Fact]
    public void Classify_AProvenRevocation_IsTheCalmDisconnectedState_NotARefusal()
    {
        var ex = new BeeMemoryBank.Sync.ThisDeviceRevokedException(Guid.NewGuid());
        FullSync.Classify(ex, false).Should().Be(FullSync.ProblemDisconnected);
        FullSync.Classify(ex, true).Should().Be(FullSync.ProblemDisconnected);
        FullSync.Describe(ex, new PeerStatus(Guid.NewGuid(), "Laptop", "", false, null, null, null))
            .Should().Be("An administrator disconnected this device from the bank. What is already on it stays readable here; it no longer receives changes.");
    }

    [Fact]
    public async Task WhenAComputerSaysThisPhoneWasDisconnected_TheRoundSaysSo_AndTheLoopStopsAskingEveryMinute()
    {
        var (test, sync) = await PhoneWithAPeerAsync("sync-disconnected");
        await using var _ = test;
        var calls = 0;
        sync.SessionForTests = (_, _, row, _) =>
        {
            calls++;
            throw new BeeMemoryBank.Sync.ThisDeviceRevokedException(row.NodeId);
        };

        var first = await sync.RoundAsync(automatic: true);

        first.Problem.Should().Be(FullSync.ProblemDisconnected);
        first.Failed.Should().Be(1);
        sync.DisconnectedSince.Should().NotBeNull();
        (await sync.PeersAsync()).Single().ProblemCode.Should().Be(FullSync.ProblemDisconnected);

        var quiet = await sync.RoundAsync(automatic: true);
        calls.Should().Be(1, "the loop's next rounds do not call the computers again before FullSync.DisconnectedRetry");
        quiet.Problem.Should().Be(FullSync.ProblemDisconnected, "the sync screen keeps the calm sentence");

        await sync.RoundAsync();
        calls.Should().Be(2, "a round the person asks for always runs");
    }

    [Fact]
    public async Task AfterTheDeviceIsAddedBack_ASessionThatGoesThrough_EndsTheDisconnectedState()
    {
        var (test, sync) = await PhoneWithAPeerAsync("sync-reconnected");
        await using var _ = test;
        var revoked = true;
        sync.SessionForTests = (_, _, row, _) => revoked ? throw new BeeMemoryBank.Sync.ThisDeviceRevokedException(row.NodeId) : Task.FromResult(3);
        await sync.RoundAsync();
        sync.DisconnectedSince.Should().NotBeNull();

        revoked = false;
        var round = await sync.RoundAsync();

        round.Problem.Should().BeNull();
        round.Applied.Should().Be(3);
        sync.DisconnectedSince.Should().BeNull();
    }
}
