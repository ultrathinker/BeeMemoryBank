using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>When a full node produces a baseline (docs/design/step-a-spec.md 7.5): cadence, early requests, debounce, preconditions.</summary>
public class BaselineSchedulerTests
{
    private sealed class ManualClock(DateTime start) : TimeProvider
    {
        public DateTime Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class FakePolicy(bool activated = true) : ISyncProtocolPolicy
    {
        public bool Activated { get; set; } = activated;
        public int Declared => Activated ? 4 : 3;
        public bool SealsMetadata => Activated;
        public bool IsCompatiblePeer(int? declaredProtocol, IReadOnlyCollection<string>? capabilities) => true;
        public bool ShouldPullFrom(int? declaredProtocol) => true;
        public void MarkActivated() => Activated = true;
    }

    private sealed class FakeLog(long head) : IBaselineLogHead
    {
        public long Head { get; set; } = head;
        public Task<long> GetAsync(CancellationToken ct = default) => Task.FromResult(Head);
    }

    private sealed class FakeSource : IBaselineSource
    {
        public ServableBaseline? Newest { get; set; }
        public Task<ServableBaseline?> GetServableAsync(CancellationToken ct = default) => Task.FromResult(Newest);
        public void Set(DateTime createdAt, long head) => Newest = new ServableBaseline(Files(createdAt, head), head);
    }

    private static BaselineFiles Files(DateTime createdAt, long head) =>
        new("nowhere", new BaselineManifest(BaselineManifest.CurrentFormat, Guid.NewGuid(), Guid.NewGuid(), createdAt, head, 1, [], new string('a', 32), 1, 39,
            new string('0', 64), 1, new string('0', 64), 0, 0), [], new byte[64]);

    private sealed class FakeProducer(FakeSource source, ManualClock clock) : IBaselineProducer
    {
        public List<string> Reasons { get; } = [];
        public Func<Exception?>? Fail { get; set; }
        public Action? DuringProduction { get; set; }

        public Task<BaselineFiles> ProduceAsync(string reason, CancellationToken ct = default)
        {
            Reasons.Add(reason);
            DuringProduction?.Invoke();
            if (Fail?.Invoke() is { } failure) throw failure;
            var files = Files(clock.Now, 100);
            source.Newest = new ServableBaseline(files, 100);
            return Task.FromResult(files);
        }
    }

    private sealed class Rig
    {
        public ManualClock Clock { get; } = new(new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc));
        public FakePolicy Policy { get; } = new();
        public FakeSource Source { get; } = new();
        public FakeLog Log { get; } = new(100);
        public FakeProducer Producer { get; }
        public BaselineScheduler Scheduler { get; }

        public Rig()
        {
            Producer = new FakeProducer(Source, Clock);
            Scheduler = new BaselineScheduler(Producer, Source, Policy, Log, NullLogger<BaselineScheduler>.Instance, Clock);
            // A fresh baseline exists, so only a request or the age rule can make another.
            Source.Set(Clock.Now, 100);
        }
    }

    [Fact]
    public async Task Nothing_is_produced_before_activation_even_when_asked()
    {
        var rig = new Rig();
        rig.Policy.Activated = false;
        rig.Scheduler.RequestEarly(BaselineReasons.Activation);

        (await rig.Scheduler.TickAsync()).Should().BeFalse();
        rig.Producer.Reasons.Should().BeEmpty();
    }

    [Fact]
    public async Task An_early_request_is_served_at_the_next_tick_with_its_reason()
    {
        var rig = new Rig();
        rig.Scheduler.RequestEarly(BaselineReasons.HardDelete);

        (await rig.Scheduler.TickAsync()).Should().BeTrue();

        rig.Producer.Reasons.Should().Equal(BaselineReasons.HardDelete);
        rig.Scheduler.PendingReason.Should().BeNull();
        (await rig.Scheduler.TickAsync()).Should().BeFalse("nothing is pending and the baseline is fresh");
    }

    [Fact]
    public async Task A_burst_of_requests_makes_one_baseline_and_the_next_waits_the_debounce()
    {
        var rig = new Rig();
        for (var i = 0; i < 5; i++) rig.Scheduler.RequestEarly(BaselineReasons.HardDelete);
        await rig.Scheduler.TickAsync();
        rig.Producer.Reasons.Should().HaveCount(1);

        rig.Scheduler.RequestEarly(BaselineReasons.KeyRotation);
        rig.Clock.Advance(TimeSpan.FromMinutes(9));
        (await rig.Scheduler.TickAsync()).Should().BeFalse("ten minutes have not passed");
        rig.Scheduler.PendingReason.Should().Be(BaselineReasons.KeyRotation);

        rig.Clock.Advance(TimeSpan.FromMinutes(1.1));
        (await rig.Scheduler.TickAsync()).Should().BeTrue();
        rig.Producer.Reasons.Should().Equal(BaselineReasons.HardDelete, BaselineReasons.KeyRotation);
    }

    [Fact]
    public async Task A_request_that_arrives_while_a_baseline_is_made_is_not_lost()
    {
        var rig = new Rig();
        rig.Scheduler.RequestEarly(BaselineReasons.LateProtocol3);
        rig.Producer.DuringProduction = () => rig.Scheduler.RequestEarly(BaselineReasons.HardDelete);

        await rig.Scheduler.TickAsync();

        rig.Scheduler.PendingReason.Should().NotBeNull("the hard delete may be newer than the cut this baseline took");
        rig.Producer.DuringProduction = null;
        rig.Clock.Advance(BaselineScheduler.Debounce + TimeSpan.FromSeconds(1));
        (await rig.Scheduler.TickAsync()).Should().BeTrue();
        rig.Scheduler.PendingReason.Should().BeNull();
    }

    [Theory]
    [InlineData(BaselineRefusal.Locked)]
    [InlineData(BaselineRefusal.EventsHeld)]
    [InlineData(BaselineRefusal.NoMetadataKey)]
    public async Task A_locked_or_held_node_keeps_the_request_and_asks_again_at_once(BaselineRefusal why)
    {
        var rig = new Rig();
        rig.Producer.Fail = () => new BaselineRefusedException(why, "later");
        rig.Scheduler.RequestEarly(BaselineReasons.Activation);

        (await rig.Scheduler.TickAsync()).Should().BeFalse();
        rig.Scheduler.PendingReason.Should().Be(BaselineReasons.Activation);

        rig.Producer.Fail = null;
        (await rig.Scheduler.TickAsync()).Should().BeTrue("a node that was only locked does not wait ten minutes after it unlocks");
        rig.Producer.Reasons.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_failed_baseline_waits_the_debounce_before_the_next_attempt()
    {
        var rig = new Rig();
        rig.Producer.Fail = () => new BaselineRefusedException(BaselineRefusal.SelfCheckFailed, "damaged");
        rig.Scheduler.RequestEarly(BaselineReasons.Activation);

        (await rig.Scheduler.TickAsync()).Should().BeFalse();
        rig.Producer.Fail = null;
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        (await rig.Scheduler.TickAsync()).Should().BeFalse("it does not hammer a baseline that failed");
        rig.Producer.Reasons.Should().HaveCount(1);

        rig.Clock.Advance(BaselineScheduler.Debounce);
        (await rig.Scheduler.TickAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task An_activated_node_with_no_baseline_makes_the_first_one()
    {
        var rig = new Rig();
        rig.Source.Newest = null;

        (await rig.Scheduler.TickAsync()).Should().BeTrue();

        rig.Producer.Reasons.Should().Equal(BaselineReasons.Scheduled);
    }

    [Fact]
    public async Task A_day_old_baseline_is_replaced_only_when_the_log_grew()
    {
        var rig = new Rig();
        rig.Clock.Advance(TimeSpan.FromHours(23));
        rig.Log.Head = 500;
        (await rig.Scheduler.TickAsync()).Should().BeFalse("not yet a day old");

        rig.Clock.Advance(TimeSpan.FromHours(2));
        rig.Log.Head = 100;
        (await rig.Scheduler.TickAsync()).Should().BeFalse("old, but the log did not grow since the cut");
        rig.Log.Head = 101;
        (await rig.Scheduler.TickAsync()).Should().BeTrue();
        rig.Producer.Reasons.Should().Equal(BaselineReasons.Scheduled);
    }

    [Fact]
    public async Task The_hosted_loop_wakes_on_a_request_without_waiting_for_the_tick()
    {
        var rig = new Rig();
        using var first = new SemaphoreSlim(0);
        rig.Producer.DuringProduction = () => first.Release();
        await rig.Scheduler.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(300);
            rig.Producer.Reasons.Should().BeEmpty("a fresh baseline and nothing asked");

            rig.Scheduler.RequestEarly(BaselineReasons.OnDemand);

            (await first.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("the request woke the loop long before the one-minute tick");
            rig.Producer.Reasons.Should().Equal(BaselineReasons.OnDemand);
        }
        finally
        {
            await rig.Scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Activation_observer_requests_an_early_baseline_and_drops_the_join_cache()
    {
        var rig = new Rig();
        var cache = new SnapshotJoinCache();
        cache.Set("a.tar.gz", "a.sig", 5, Guid.NewGuid(), 7);
        var observer = new FullNodeActivationObserver(rig.Scheduler, cache, NullLogger<FullNodeActivationObserver>.Instance);

        await observer.OnActivatedAsync(new MetadataActivation(Guid.NewGuid(), new string('b', 32), 1, DateTime.UtcNow, Guid.NewGuid()));

        rig.Scheduler.PendingReason.Should().Be(BaselineReasons.Activation);
        cache.TryGet().Should().BeNull();
        (await rig.Scheduler.TickAsync()).Should().BeTrue();
    }
}
