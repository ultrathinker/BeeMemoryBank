using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>When the open vault closes by itself: away from the screen past the grace, or idle on the screen past the timeout.</summary>
public class AutoLockPolicyTests
{
    private static readonly DateTime T0 = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BackWithinTheGrace_StaysOpen_PastIt_Locks()
    {
        var policy = new AutoLockPolicy { BackgroundGrace = TimeSpan.FromMinutes(1) };

        policy.Left(T0);
        policy.Returned(T0.AddSeconds(59)).Should().BeFalse();

        policy.Left(T0.AddMinutes(2));
        policy.Returned(T0.AddMinutes(3).AddSeconds(1)).Should().BeTrue();
    }

    [Fact]
    public void WithNoGrace_AnyAbsence_Locks()
    {
        var policy = new AutoLockPolicy { BackgroundGrace = TimeSpan.Zero };
        policy.Left(T0);
        policy.Returned(T0.AddMilliseconds(1)).Should().BeTrue();
    }

    [Fact]
    public void AClockThatWentBackwards_Locks()
    {
        var policy = new AutoLockPolicy { BackgroundGrace = TimeSpan.FromMinutes(5) };
        policy.Left(T0);
        policy.Returned(T0.AddMinutes(-10)).Should().BeTrue();
    }

    [Fact]
    public void TheFirstLeaving_Counts_NotTheLast()
    {
        // iOS reports "inactive" then "background": the absence started at the first of them.
        var policy = new AutoLockPolicy { BackgroundGrace = TimeSpan.FromMinutes(1) };
        policy.Left(T0);
        policy.Left(T0.AddMinutes(1));
        policy.Returned(T0.AddMinutes(1).AddSeconds(30)).Should().BeTrue();
    }

    [Fact]
    public void ReturningWithoutHavingLeft_DoesNotLock()
    {
        new AutoLockPolicy().Returned(T0).Should().BeFalse();
    }

    [Fact]
    public void IdleOnTheScreen_PastTheTimeout_Locks_AndATouchResetsIt()
    {
        var policy = new AutoLockPolicy { IdleTimeout = TimeSpan.FromMinutes(5) };
        policy.IsIdle(T0).Should().BeFalse("no activity recorded yet: the app has not been unlocked");

        policy.Activity(T0);
        policy.IsIdle(T0.AddMinutes(4)).Should().BeFalse();
        policy.Activity(T0.AddMinutes(4));
        policy.IsIdle(T0.AddMinutes(8)).Should().BeFalse();
        policy.IsIdle(T0.AddMinutes(9).AddSeconds(1)).Should().BeTrue();
    }

    [Fact]
    public void TheChoices_ReadWell()
    {
        AutoLockPolicy.GraceChoices.Select(AutoLockPolicy.Describe).Should().Equal("Immediately", "After 1 minute", "After 5 minutes");
        AutoLockPolicy.IdleChoices.Select(AutoLockPolicy.Describe).Should().Equal("After 2 minutes", "After 5 minutes", "After 15 minutes");
    }
}
