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
}
