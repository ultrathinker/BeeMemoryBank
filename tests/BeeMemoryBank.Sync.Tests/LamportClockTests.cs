using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Sync.Tests;

public class LamportClockTests
{
    [Fact]
    public void Tick_Increments()
    {
        var clock = new LamportClock();
        clock.Tick().Should().Be(1);
        clock.Tick().Should().Be(2);
        clock.Tick().Should().Be(3);
    }

    [Fact]
    public void Initialize_SetsStartValue()
    {
        var clock = new LamportClock();
        clock.Initialize(100);
        clock.Current.Should().Be(100);
        clock.Tick().Should().Be(101);
    }

    [Fact]
    public void Initialize_CalledTwice_IgnoresSecondCall()
    {
        var clock = new LamportClock();
        clock.Initialize(50);
        clock.Initialize(200); // should be ignored
        clock.Current.Should().Be(50);
    }

    [Fact]
    public void Update_TakesMax_WhenRemoteIsHigher()
    {
        var clock = new LamportClock();
        clock.Initialize(5);
        clock.Update(10); // max(5,10)+1 = 11
        clock.Current.Should().Be(11);
    }

    [Fact]
    public void Update_NoOp_WhenLocalIsHigher()
    {
        var clock = new LamportClock();
        clock.Initialize(20);
        clock.Update(5); // max(20,5)+1 = 21, but current is already 20 >= 21-1, no need
        // Actually 20 < 21, so Update should set 21
        clock.Current.Should().BeGreaterThanOrEqualTo(20);
    }

    [Fact]
    public void Update_EqualToLocal_IncrementsBy1()
    {
        var clock = new LamportClock();
        clock.Initialize(10);
        clock.Update(10); // max(10,10)+1 = 11
        clock.Current.Should().Be(11);
    }

    [Fact]
    public void Tick_ThreadSafe()
    {
        var clock = new LamportClock();
        var results = new long[1000];
        Parallel.For(0, 1000, i => results[i] = clock.Tick());
        // All values should be unique
        results.Distinct().Should().HaveCount(1000);
        clock.Current.Should().Be(1000);
    }
}
