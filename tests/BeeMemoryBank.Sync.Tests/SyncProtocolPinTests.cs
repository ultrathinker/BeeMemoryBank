namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The sync protocol is a number of its own, independent of the application version. Release 2.0.1 changes the application
/// version only: a peer that speaks protocol 3 (every 1.0.x from the blind-node protocol on, e.g. 1.0.17) must still sync with
/// it in both directions, and a peer below 3 must still be refused. This pins the constants so that conflating the two
/// versions is a red test, not a field incident.
/// </summary>
public class SyncProtocolPinTests
{
    [Fact]
    public void TheProtocolStaysThree_AndNoApplicationVersionLeaksIntoIt()
    {
        SyncProtocolVersion.Current.Should().Be(3);
        SyncProtocolVersion.MinPeer.Should().Be(3);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(2, false)]
    [InlineData(1, false)]
    [InlineData(null, false)]
    public void PeersBelowThreeAreRefused(int? peer, bool compatible) =>
        SyncProtocolVersion.IsCompatiblePeer(peer).Should().Be(compatible);

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(0, false)]
    [InlineData(4, false)]
    public void TheEventProtocolsThisBuildCanApply(int version, bool canApply) =>
        SyncProtocolVersion.CanApply(version).Should().Be(canApply);
}
