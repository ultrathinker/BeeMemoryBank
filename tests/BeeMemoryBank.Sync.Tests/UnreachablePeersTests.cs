namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Plan 4.4: an unreachable peer (a phone out of the house, a switched-off blind node) is retried
/// with a growing pause rather than every cycle.
/// </summary>
public class UnreachablePeersTests
{
    private static readonly DateTime T0 = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    [Fact]
    public void Pause_DoublesPerFailure_UpToTheCap()
    {
        var peers = new UnreachablePeers();
        var node = Guid.NewGuid();

        var pauses = Enumerable.Range(0, 6).Select(_ => peers.NoteFailure(node, T0, Interval).Pause).ToList();

        pauses.Should().Equal(
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4),
            TimeSpan.FromMinutes(8), UnreachablePeers.MaxPause, UnreachablePeers.MaxPause);
    }

    [Fact]
    public void Skipped_UntilThePauseIsOver_ThenTriedAgain()
    {
        var peers = new UnreachablePeers();
        var node = Guid.NewGuid();
        peers.NoteFailure(node, T0, Interval);
        peers.NoteFailure(node, T0, Interval); // two minutes now

        peers.ShouldSkip(node, T0.AddSeconds(90)).Should().BeTrue();
        peers.ShouldSkip(node, T0.AddMinutes(2)).Should().BeFalse();
        peers.ShouldSkip(Guid.NewGuid(), T0).Should().BeFalse("other peers are not affected");
    }

    [Fact]
    public void Success_EndsTheStreak()
    {
        var peers = new UnreachablePeers();
        var node = Guid.NewGuid();
        peers.NoteFailure(node, T0, Interval);
        peers.NoteFailure(node, T0, Interval);

        peers.NoteSuccess(node).Should().Be(2);
        peers.ShouldSkip(node, T0).Should().BeFalse();
        peers.NoteFailure(node, T0, Interval).Failures.Should().Be(1, "a new streak starts from one interval");
    }

    [Fact]
    public void FailingSince_IsTheFirstFailureOfTheStreak_AndEndsWithIt()
    {
        var peers = new UnreachablePeers();
        var node = Guid.NewGuid();
        peers.NoteFailure(node, T0, Interval);
        peers.NoteFailure(node, T0.AddMinutes(2), Interval);
        peers.NoteFailure(node, T0.AddMinutes(17), Interval);

        peers.FailingSince().Should().Equal(new Dictionary<Guid, DateTime> { [node] = T0 });

        peers.NoteSuccess(node);
        peers.FailingSince().Should().BeEmpty();
    }

    /// <summary>BMB-183: a computer with no network at all fails every call, and says nothing about the peers it calls.</summary>
    [Fact]
    public void WhileThisComputerHasNoNetwork_TheStreakDoesNotGrow_ButTheBackOffDoes()
    {
        var up = true;
        var peers = new UnreachablePeers(() => up);
        var node = Guid.NewGuid();
        peers.NoteFailure(node, T0, Interval);

        up = false;
        for (var i = 1; i <= 8; i++) peers.NoteFailure(node, T0.AddHours(i * 0.5), Interval);   // four hours without a network

        peers.FailingSince()[node].Should().Be(T0.AddHours(4), "the streak starts at the latest failure, not at the first one of the train ride");
        peers.ShouldSkip(node, T0.AddHours(4).AddMinutes(1)).Should().BeTrue("the pause still grew: the peer is not asked every cycle");

        up = true;   // the network is back and the peer really is down: the streak runs on from the last failure, and now grows
        var back = T0.AddHours(4).AddMinutes(20);
        peers.NoteFailure(node, back, Interval);
        peers.NoteFailure(node, back.AddMinutes(20), Interval);
        peers.FailingSince()[node].Should().Be(T0.AddHours(4));
    }

    /// <summary>BMB-77: a night the PC slept through is not a night the peer was down.</summary>
    [Fact]
    public void AGapLongerThanTheStreakBreak_MeansThisComputerWasAsleep_SoTheStreakStartsAgain()
    {
        var peers = new UnreachablePeers();
        var node = Guid.NewGuid();
        peers.NoteFailure(node, T0, Interval);
        var woke = T0 + UnreachablePeers.StreakBreak + TimeSpan.FromHours(8);

        var (failures, _) = peers.NoteFailure(node, woke, Interval);

        peers.FailingSince()[node].Should().Be(woke);
        failures.Should().Be(2, "the back-off is not reset: only the awake streak starts again");
    }
}
