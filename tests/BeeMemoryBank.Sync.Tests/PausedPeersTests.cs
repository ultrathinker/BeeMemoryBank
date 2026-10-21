using BeeMemoryBank.Sync;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Release 2.7: a blind node that answers a push 503 + X-BMB-Paused is a peer whose owner's limit stops it. The memory is modelled on
/// <see cref="ClosedPeers"/> (a peer that answered and said no): one retry when the node said, never later than an hour, one line for the whole episode, and
/// nothing that grows (the back-off of <see cref="UnreachablePeers"/> is for a peer that does not answer).
/// </summary>
public class PausedPeersTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Peer = Guid.Parse("00000000-0000-4000-8000-000000000001");

    [Fact]
    public void ThePauseHoldsUntilTheTimeTheNodeNamed_ThenTheNodeIsAskedAgain()
    {
        var paused = new PausedPeers();
        paused.NotePaused(Peer, T0, TimeSpan.FromMinutes(20), PeerPauseReport.Traffic, "http://blind.local");

        paused.ShouldSkip(Peer, T0.AddMinutes(1), "http://blind.local").Should().BeTrue();
        paused.ShouldSkip(Peer, T0.AddMinutes(19), "http://blind.local").Should().BeTrue();
        paused.ShouldSkip(Peer, T0.AddMinutes(20), "http://blind.local").Should().BeFalse("the node said when to ask again");
    }

    [Theory]
    [InlineData(5 * 3600)]   // "until midnight", five hours away
    [InlineData(86400)]
    public void ATimeLongerThanAnHourIsAnHour_SoARaisedLimitIsFoundWithinTheHour(int seconds)
    {
        var paused = new PausedPeers();
        paused.NotePaused(Peer, T0, TimeSpan.FromSeconds(seconds), PeerPauseReport.Traffic);

        paused.ShouldSkip(Peer, T0.AddMinutes(59)).Should().BeTrue();
        paused.ShouldSkip(Peer, T0.AddHours(1)).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ATimeShorterThanTheMinimumIsTheMinimum_SoANodeThatSaysNowIsNotAskedInALoop(int seconds)
    {
        var paused = new PausedPeers();
        paused.NotePaused(Peer, T0, TimeSpan.FromSeconds(seconds), PeerPauseReport.Disk);

        paused.ShouldSkip(Peer, T0.AddSeconds(14)).Should().BeTrue();
        paused.ShouldSkip(Peer, T0.AddSeconds(15)).Should().BeFalse();
    }

    [Fact]
    public void OneEpisode_OneLine_AndNoGrowth_HoweverManyTimesTheNodeSaysItAgain()
    {
        var paused = new PausedPeers();
        var opened = 0;
        var now = T0;
        DateTime? firstNext = null;
        for (var i = 0; i < 50; i++)
        {
            if (paused.NotePaused(Peer, now, TimeSpan.FromMinutes(30), PeerPauseReport.Disk)) opened++;
            // Exactly the time named, at every repeat: no back-off.
            paused.ShouldSkip(Peer, now.AddMinutes(29)).Should().BeTrue();
            paused.ShouldSkip(Peer, now.AddMinutes(30)).Should().BeFalse("the wait never grows: it is the node's own time every time");
            firstNext ??= now.AddMinutes(30);
            now = now.AddMinutes(30);
        }

        opened.Should().Be(1, "the repeats of one episode are not new lines");
        paused.Count.Should().Be(1);
    }

    [Fact]
    public void AnotherReasonInsideTheEpisodeIsTheSameEpisode_OnlyTheWordsChange()
    {
        var paused = new PausedPeers();
        paused.NotePaused(Peer, T0, TimeSpan.FromMinutes(30), PeerPauseReport.Traffic).Should().BeTrue();

        paused.NotePaused(Peer, T0.AddMinutes(30), TimeSpan.FromMinutes(30), PeerPauseReport.Disk).Should().BeFalse("still paused");

        paused.ReasonOf(Peer).Should().Be(PeerPauseReport.Disk);
    }

    [Fact]
    public void AnAnswerThatIsNotAPauseEndsTheEpisode_AndALaterPauseIsANewOne()
    {
        var paused = new PausedPeers();
        paused.NotePaused(Peer, T0, TimeSpan.FromMinutes(30), PeerPauseReport.Disk);

        paused.NoteOpen(Peer).Should().BeTrue("the one line that says it goes on");
        paused.NoteOpen(Peer).Should().BeFalse("and only once");
        paused.ShouldSkip(Peer, T0.AddMinutes(1)).Should().BeFalse();
        paused.ReasonOf(Peer).Should().BeNull();

        paused.NotePaused(Peer, T0.AddHours(3), TimeSpan.FromMinutes(30), PeerPauseReport.Disk).Should().BeTrue("a later pause is a new episode");
    }

    [Fact]
    public void APauseLearnedAtOneAddressIsNotTheNextAddresses()
    {
        var paused = new PausedPeers();
        paused.NotePaused(Peer, T0, TimeSpan.FromMinutes(30), PeerPauseReport.Disk, "http://old.local").Should().BeTrue();

        paused.ShouldSkip(Peer, T0.AddMinutes(1), "http://new.local").Should().BeFalse("the pause was the old address's");
        paused.NotePaused(Peer, T0.AddMinutes(2), TimeSpan.FromMinutes(30), PeerPauseReport.Disk, "http://new.local").Should().BeTrue("a pause at a new address is a new episode");
    }

    [Fact]
    public void RetainKeepsOnlyThePeersOfTheWhitelist_AndForgetDropsOne()
    {
        var paused = new PausedPeers();
        var kept = Guid.NewGuid();
        paused.NotePaused(kept, T0, TimeSpan.FromHours(1), PeerPauseReport.Disk);
        for (var i = 0; i < 100; i++) paused.NotePaused(Guid.NewGuid(), T0, TimeSpan.FromHours(1), PeerPauseReport.Traffic);

        paused.Retain(new HashSet<Guid> { kept }).Should().Be(100);

        paused.Count.Should().Be(1);
        paused.ShouldSkip(kept, T0.AddMinutes(1)).Should().BeTrue();
        paused.Forget(kept);
        paused.Count.Should().Be(0);
    }

    [Fact]
    public void TwoSessionsThatReportTheFirstPauseAtTheSameMoment_OpenOneEpisode()
    {
        var paused = new PausedPeers();
        var opened = 0;
        Parallel.For(0, 32, _ =>
        {
            if (paused.NotePaused(Peer, T0, TimeSpan.FromMinutes(30), PeerPauseReport.Traffic)) Interlocked.Increment(ref opened);
        });

        opened.Should().Be(1);
    }
}
