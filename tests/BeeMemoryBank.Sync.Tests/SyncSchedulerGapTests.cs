namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The scheduler holds a triggered cycle back until <see cref="SyncScheduler.MinCycleGap"/> has
/// passed since the previous one started, so a burst of saves does not open one rate-limited
/// handshake per save (BMB-31 scenario 12: a save a second drew 429s for minutes).
/// </summary>
public class SyncSchedulerGapTests
{
    private static readonly DateTime T0 = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(3);

    [Fact]
    public void Trigger_right_after_a_cycle_waits_out_the_rest_of_the_gap() =>
        SyncScheduler.RemainingGap(T0, T0.AddSeconds(1), Gap).Should().Be(TimeSpan.FromSeconds(2));

    [Fact]
    public void Trigger_after_the_gap_runs_at_once() =>
        SyncScheduler.RemainingGap(T0, T0.AddSeconds(5), Gap).Should().Be(TimeSpan.Zero);

    [Fact]
    public void First_cycle_is_not_held_back() =>
        SyncScheduler.RemainingGap(DateTime.MinValue, T0, Gap).Should().Be(TimeSpan.Zero);

    [Fact]
    public void Default_gap_keeps_one_node_well_under_the_peer_limit_of_30_handshakes_a_minute()
    {
        var scheduler = new SyncScheduler(null!, null!, null!, null!);
        (60 / scheduler.MinCycleGap.TotalSeconds).Should().BeLessThanOrEqualTo(20,
            "a phone behind the same NAT needs room in the same 30-a-minute budget");
    }
}
