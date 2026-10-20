using BeeMemoryBank.Api.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// How long a node remembers the password of a protected article it was given (BMB_PROTECTED_UNLOCK_SECONDS): a quarter of an hour unless the
/// setting says another time between 30 seconds and one hour. A short time is for trying what happens when the time is out (the dialog of the
/// save) without waiting; a value that makes no sense never switches the memory off or makes it last for days.
/// </summary>
public class ProtectedUnlockTtlTests
{
    [Theory]
    [InlineData(null, 900)]
    [InlineData("", 900)]
    [InlineData("two minutes", 900)]
    [InlineData("120", 120)]
    [InlineData(" 120 ", 120)]
    [InlineData("30", 30)]
    [InlineData("3600", 3600)]
    [InlineData("29", 900)]
    [InlineData("0", 900)]
    [InlineData("-5", 900)]
    [InlineData("3601", 900)]
    public void TheSettingIsUsedOnlyWhenItIsAReasonableNumberOfSeconds(string? setting, int expectedSeconds)
    {
        ProtectedUnlockCache.ParseTtl(setting).TotalSeconds.Should().Be(expectedSeconds);
    }

    [Fact]
    public void TheDefaultIsAQuarterOfAnHour()
    {
        ProtectedUnlockCache.DefaultTtl.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void TheTimeAPersonCanChooseForAnArticleIsFifteenMinutesToEightHours()
    {
        ProtectedUnlockCache.MinChosenTtl.Should().Be(TimeSpan.FromMinutes(15));
        ProtectedUnlockCache.MaxChosenTtl.Should().Be(TimeSpan.FromHours(8));
    }
}

/// <summary>
/// BMB-319: the time one article keeps its password for lives in that article's entry of the cache. It holds while the entry is alive (saves and unlocks
/// remember the password again) and is gone with the entry (re-lock, a run-out time, a vault lock), when the node-wide time applies again. The clock is
/// moved by the test: waiting out a quarter of an hour is not a test.
/// </summary>
public class ProtectedUnlockChosenTimeTests
{
    private const string Browser = "u1:a:sTrue:wbrowser-a";
    private static readonly Guid Chosen = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private DateTime _now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private ProtectedUnlockCache NewCache() => new(() => _now);

    [Fact]
    public void ANewEntryHasTheNodeWideTime()
    {
        var cache = NewCache();

        var expires = cache.Remember(Browser, Chosen, "pw");

        expires.Should().Be(_now.Add(ProtectedUnlockCache.Ttl));
        cache.DurationOf(Browser, Chosen).Should().Be(ProtectedUnlockCache.Ttl);
    }

    [Fact]
    public void AChosenTimeCountsFromNow_AndBelongsToThatArticleAndBrowserOnly()
    {
        var cache = NewCache();
        cache.Remember(Browser, Chosen, "pw");
        cache.Remember(Browser, Other, "pw");
        cache.Remember("u1:a:sTrue:wbrowser-b", Chosen, "pw");
        _now = _now.AddMinutes(5);

        cache.SetDuration(Browser, Chosen, TimeSpan.FromHours(8), out var expires).Should().BeTrue();

        expires.Should().Be(_now.AddHours(8));
        cache.DurationOf(Browser, Chosen).Should().Be(TimeSpan.FromHours(8));
        cache.DurationOf(Browser, Other).Should().Be(ProtectedUnlockCache.Ttl, "another article keeps the standard time");
        cache.DurationOf("u1:a:sTrue:wbrowser-b", Chosen).Should().Be(ProtectedUnlockCache.Ttl, "another browser keeps the standard time");
        cache.TryGet(Browser, Chosen, out var seen).Should().Be("pw");
        seen.Should().Be(expires);
    }

    [Fact]
    public void ASaveOrAnUnlockWhileItIsUnlocked_CountsTheChosenTimeAnew_AndDoesNotTakeTheDefaultBack()
    {
        var cache = NewCache();
        cache.Remember(Browser, Chosen, "pw");
        cache.SetDuration(Browser, Chosen, TimeSpan.FromHours(3), out _);
        _now = _now.AddHours(1);

        var expires = cache.Remember(Browser, Chosen, "pw");

        expires.Should().Be(_now.AddHours(3));
        cache.DurationOf(Browser, Chosen).Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public void WhenTheChosenTimeIsOut_TheNextUnlockHasTheStandardTimeAgain()
    {
        var cache = NewCache();
        cache.Remember(Browser, Chosen, "pw");
        cache.SetDuration(Browser, Chosen, TimeSpan.FromHours(2), out _);
        _now = _now.AddHours(2).AddSeconds(1);

        cache.TryGet(Browser, Chosen).Should().BeNull("the time is out");
        cache.DurationOf(Browser, Chosen).Should().BeNull();
        var expires = cache.Remember(Browser, Chosen, "pw");

        expires.Should().Be(_now.Add(ProtectedUnlockCache.Ttl));
        cache.DurationOf(Browser, Chosen).Should().Be(ProtectedUnlockCache.Ttl);
    }

    [Fact]
    public void AnEntryThatRanOutButWasNeverReadIsNotGivenItsOldChosenTimeBack()
    {
        // Nothing asked for the entry between its run-out and the next Remember: the sweep of Remember must drop it first.
        var cache = NewCache();
        cache.Remember(Browser, Chosen, "pw");
        cache.SetDuration(Browser, Chosen, TimeSpan.FromHours(2), out _);
        _now = _now.AddHours(3);

        var expires = cache.Remember(Browser, Chosen, "pw");

        expires.Should().Be(_now.Add(ProtectedUnlockCache.Ttl));
    }

    [Fact]
    public void ARelockForgetsTheChosenTime()
    {
        var cache = NewCache();
        cache.Remember(Browser, Chosen, "pw");
        cache.SetDuration(Browser, Chosen, TimeSpan.FromHours(8), out _);

        cache.Forget(Browser, Chosen);

        cache.DurationOf(Browser, Chosen).Should().BeNull();
        cache.Remember(Browser, Chosen, "pw").Should().Be(_now.Add(ProtectedUnlockCache.Ttl));
    }

    [Fact]
    public void AVaultLockForgetsTheChosenTimeOfEveryArticle()
    {
        var cache = NewCache();
        cache.Remember(Browser, Chosen, "pw");
        cache.Remember(Browser, Other, "pw");
        cache.SetDuration(Browser, Chosen, TimeSpan.FromHours(8), out _);
        cache.SetDuration(Browser, Other, TimeSpan.FromHours(4), out _);

        cache.Clear();

        cache.DurationOf(Browser, Chosen).Should().BeNull();
        cache.DurationOf(Browser, Other).Should().BeNull();
        cache.Remember(Browser, Chosen, "pw").Should().Be(_now.Add(ProtectedUnlockCache.Ttl));
    }

    [Fact]
    public void ALockedArticle_AnUnknownArticle_AndACallerWithoutAKey_CannotBeGivenATime()
    {
        var cache = NewCache();
        cache.Remember(Browser, Chosen, "pw");
        _now = _now.AddHours(1);   // out of time

        cache.SetDuration(Browser, Chosen, TimeSpan.FromHours(8), out _).Should().BeFalse("the article is locked: the password is not remembered any more");
        cache.SetDuration(Browser, Other, TimeSpan.FromHours(8), out _).Should().BeFalse("nothing was ever unlocked for it");
        cache.SetDuration(null, Chosen, TimeSpan.FromHours(8), out _).Should().BeFalse("no key, no entry");
        cache.TryGet(Browser, Chosen).Should().BeNull("a refused time must not bring a locked article back");
    }
}
