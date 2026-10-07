using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// BMB-77: the shell turns the node's blind-node alarms into notifications. One per episode, after two polls that agree; a reminder
/// at most every 24 hours; cleared after two clean polls; nothing during the start grace, in invisible mode, before the node has
/// judged or on a failed poll; at most three at once; the notified episodes survive a restart; names only while unlocked.
/// </summary>
public sealed class BlindAlarmWatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : IBlindAlarmEpisodeStore
    {
        public Dictionary<string, Dictionary<string, DateTime>> Profiles { get; } = new();
        public int Saves { get; private set; }

        public IReadOnlyDictionary<string, DateTime> Load(string profileId) =>
            Profiles.TryGetValue(profileId, out var e) ? new Dictionary<string, DateTime>(e) : new Dictionary<string, DateTime>();

        public void Save(string profileId, IReadOnlyDictionary<string, DateTime> notified)
        {
            Saves++;
            Profiles[profileId] = new Dictionary<string, DateTime>(notified);
        }
    }

    private readonly Clock _clock = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly MemoryStore _store = new();
    private NodeAlarmsPoll _next = Judged();
    private string? _profile = "profile-a";

    private BlindAlarmWatcher Make(TimeSpan? noticeGap = null) =>
        new(_ => Task.FromResult(_next), _notifier, _store, () => _profile, _clock, _ => { }, noticeGap ?? TimeSpan.Zero);

    private static readonly Guid NodeA = Guid.Parse("b11d0000-0000-8000-8000-00000000000a");
    private static readonly Guid NodeB = Guid.Parse("b11d0000-0000-8000-8000-00000000000b");
    private static readonly DateTime LastContact = new(2026, 10, 7, 2, 0, 0, DateTimeKind.Utc);

    private static BlindAlarmEntry Silent(Guid node, string? name = "Attic box", bool notify = true) =>
        new("silent", node, name, LastContact, null, notify, Banner: false);

    private static NodeAlarmsPoll Judged(params BlindAlarmEntry[] alarms) => new(new BlindAlarmsAnswer("ok", false, alarms));

    /// <summary>Polls once a minute (the clock moves first), as the loop does.</summary>
    private async Task PollAsync(BlindAlarmWatcher watcher, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            _clock.Now += BlindAlarmWatcher.PollInterval;
            await watcher.PollOnceAsync(CancellationToken.None);
        }
    }

    /// <summary>A watcher whose start grace is over (it has polled quietly for longer than the grace).</summary>
    private async Task<BlindAlarmWatcher> PastTheGraceAsync()
    {
        var watcher = Make();
        await PollAsync(watcher, (int)BlindAlarmWatcher.StartGrace.TotalMinutes + 1);
        return watcher;
    }

    [Fact]
    public async Task OneNoticePerEpisode_AfterTwoPollsThatAgree()
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(Silent(NodeA));

        await PollAsync(watcher);
        _notifier.Notices.Should().BeEmpty("one poll is not enough: no flapping");

        await PollAsync(watcher);
        var notice = _notifier.Notices.Should().ContainSingle().Subject;
        notice.Message.Should().Contain("\"Attic box\"");
        watcher.AttentionCount.Should().Be(1);

        await PollAsync(watcher, 30);
        _notifier.Notices.Should().ContainSingle("the same episode is not notified again within a day");
    }

    [Fact]
    public async Task AReminder_ComesAfterADay_NotBefore()
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(Silent(NodeA));
        await PollAsync(watcher, 2);

        // Polled every minute for a day (a gap would be a sleep, and start the grace again).
        await PollAsync(watcher, (int)BlindAlarmWatcher.RemindEvery.TotalMinutes - 1);
        _notifier.Notices.Should().HaveCount(1);

        await PollAsync(watcher);
        _notifier.Notices.Should().HaveCount(2, "a day after the first notice, a reminder");
    }

    [Fact]
    public async Task AnEpisode_ClearsAfterTwoCleanPolls_AndANewOneIsNotifiedAgain()
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(Silent(NodeA));
        await PollAsync(watcher, 2);

        _next = Judged();
        await PollAsync(watcher);
        watcher.AttentionCount.Should().Be(0, "a raised alarm needs to be seen in a row");
        _next = Judged(Silent(NodeA));
        await PollAsync(watcher, 2);
        _notifier.Notices.Should().HaveCount(1, "one clean poll does not clear an episode: it is still the same one");

        _next = Judged();
        await PollAsync(watcher, 2);
        _next = Judged(Silent(NodeA) with { Since = LastContact.AddHours(5) }); // it came back, and went silent again
        await PollAsync(watcher, 2);
        _notifier.Notices.Should().HaveCount(2);
    }

    [Fact]
    public async Task ASleepOfThePc_PausesAnEpisode_ItDoesNotEndIt()
    {
        // The node answers "still failing, below the threshold" while a sleep's restarted streak runs again (Notify false). Only absence clears.
        var watcher = await PastTheGraceAsync();
        _next = Judged(Silent(NodeA));
        await PollAsync(watcher, 2);
        _notifier.Notices.Should().ContainSingle();

        _next = Judged(Silent(NodeA, notify: false));
        await PollAsync(watcher, 6);
        watcher.AttentionCount.Should().Be(1, "the outage has not ended because the computer slept");

        _next = Judged(Silent(NodeA));
        await PollAsync(watcher, 3);
        _notifier.Notices.Should().ContainSingle("the same episode is not notified again until its day is up");

        _next = Judged(Silent(NodeB, notify: false));
        await PollAsync(watcher, 4);
        _notifier.Notices.Should().ContainSingle("an alarm that was never raised does not start an episode by being held");

        _next = Judged();
        await PollAsync(watcher, 2);
        watcher.AttentionCount.Should().Be(0, "an alarm that is gone from a judged report still clears the episode");
    }

    [Fact]
    public async Task TheNoticesOfOneBurst_AreSentApart_SoEachIsShownLongEnoughToRead()
    {
        // A second balloon on the same icon replaces the first on screen (checked on Windows 11): back to back, only the last is seen.
        var times = new List<DateTime>();
        var notifier = new TimedNotifier(times);
        var watcher = new BlindAlarmWatcher(_ => Task.FromResult(_next), notifier, _store, () => _profile, _clock, _ => { },
            noticeGap: TimeSpan.FromMilliseconds(120));
        await PollAsync(watcher, (int)BlindAlarmWatcher.StartGrace.TotalMinutes + 1);
        _next = Judged(Silent(NodeA), Silent(NodeB));
        await PollAsync(watcher);

        await PollAsync(watcher);

        times.Should().HaveCount(2);
        (times[1] - times[0]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100));
    }

    private sealed class TimedNotifier(List<DateTime> times) : IUserNotifier
    {
        public void Notify(string title, string message) => times.Add(DateTime.UtcNow);
    }

    [Fact]
    public async Task ABlindNodeThatKeepsChangingItsDeclaredProtocol_IsOneEpisode()
    {
        var watcher = await PastTheGraceAsync();
        foreach (var protocol in new[] { 5, 5, 6, 6, 7, 7, 5, 5 })
        {
            _next = Judged(new BlindAlarmEntry("pc_too_old", NodeA, "Attic box", null, protocol, true, true));
            await PollAsync(watcher);
        }

        _notifier.Notices.Should().ContainSingle("the notice does not name the protocol, so a new number is not a new problem");
    }

    [Fact]
    public async Task DuringTheStartGrace_NothingIsShown_ButTheTrayAlreadyKnows()
    {
        var watcher = Make();
        _next = Judged(Silent(NodeA));

        await PollAsync(watcher, (int)BlindAlarmWatcher.StartGrace.TotalMinutes - 1);

        _notifier.Notices.Should().BeEmpty();
        watcher.AttentionCount.Should().Be(1);

        await PollAsync(watcher, 2);
        _notifier.Notices.Should().ContainSingle("once the grace is over the raised episode is notified");
    }

    [Theory]
    [InlineData("invisible")]
    [InlineData("warming_up")]
    public async Task ANodeThatDidNotJudge_NeitherRaisesNorClears(string state)
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(Silent(NodeA));
        await PollAsync(watcher, 2);

        _next = new NodeAlarmsPoll(new BlindAlarmsAnswer(state, false, []));
        await PollAsync(watcher, 5);
        watcher.AttentionCount.Should().Be(1, "nothing was judged, so nothing cleared");

        _next = new NodeAlarmsPoll(new BlindAlarmsAnswer(state, false, [Silent(NodeB)]));
        await PollAsync(watcher, 5);
        _notifier.Notices.Should().ContainSingle();
    }

    [Fact]
    public async Task AFailedPoll_NeitherRaisesNorClears()
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(Silent(NodeA));
        await PollAsync(watcher, 2);

        _next = new NodeAlarmsPoll(null, "The node could not be reached.");
        await PollAsync(watcher, 3);

        watcher.AttentionCount.Should().Be(1);
        _notifier.Notices.Should().ContainSingle();
    }

    [Fact]
    public async Task ALockedVault_GivesTheGenericText_WithoutNames()
    {
        var watcher = await PastTheGraceAsync();
        _next = new NodeAlarmsPoll(new BlindAlarmsAnswer("ok", true, [Silent(NodeA, name: null)]));

        await PollAsync(watcher, 2);

        var notice = _notifier.Notices.Should().ContainSingle().Subject;
        notice.Message.Should().Be("A blind node has not been in contact for a long time. Open Bee Memory Bank, Blind nodes, to see which.");
    }

    [Fact]
    public async Task AtMostThreeNoticesAtOnce_TheRestAreSummedUp()
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(Enumerable.Range(1, 5).Select(i => Silent(Guid.NewGuid(), $"Box {i}")).ToArray());

        await PollAsync(watcher, 2);

        _notifier.Notices.Should().HaveCount(BlindAlarmWatcher.MaxNoticesPerBurst);
        _notifier.Notices.Last().Message.Should().StartWith("3 more blind-node alarms");
        watcher.AttentionCount.Should().Be(5);
    }

    [Fact]
    public async Task TheNotifiedEpisodes_SurviveARestart()
    {
        var first = await PastTheGraceAsync();
        _next = Judged(Silent(NodeA));
        await PollAsync(first, 2);
        _notifier.Notices.Should().ContainSingle();

        var second = Make(); // the app started again: the same store
        await PollAsync(second, (int)BlindAlarmWatcher.StartGrace.TotalMinutes + 5);

        _notifier.Notices.Should().ContainSingle("the episode was notified before the restart");
        second.AttentionCount.Should().Be(1);
    }

    [Fact]
    public async Task AClearedEpisode_IsForgottenByTheStore_Too()
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(Silent(NodeA));
        await PollAsync(watcher, 2);
        _store.Profiles["profile-a"].Should().ContainSingle();

        _next = Judged();
        await PollAsync(watcher, 2);

        _store.Profiles["profile-a"].Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownKinds_AndAlarmsTheNodeDoesNotNotify_AreIgnored()
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(
            new BlindAlarmEntry("copies_gone_from_the_future", NodeA, "Attic box", null, null, true, true),
            Silent(NodeB, notify: false));

        await PollAsync(watcher, 3);

        _notifier.Notices.Should().BeEmpty();
        watcher.AttentionCount.Should().Be(0);
    }

    [Fact]
    public async Task AfterTheComputerSlept_TheGraceStartsAgain()
    {
        var watcher = await PastTheGraceAsync();
        _clock.Now += TimeSpan.FromHours(9); // asleep: no poll for nine hours
        _next = Judged(Silent(NodeA));

        await PollAsync(watcher, 3);

        _notifier.Notices.Should().BeEmpty("a computer that just woke lets the node catch up first");
        await PollAsync(watcher, (int)BlindAlarmWatcher.StartGrace.TotalMinutes);
        _notifier.Notices.Should().ContainSingle();
    }

    [Fact]
    public async Task AnotherProfile_HasItsOwnEpisodes()
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(Silent(NodeA));
        await PollAsync(watcher, 2);

        _profile = "profile-b";
        await PollAsync(watcher);
        watcher.AttentionCount.Should().Be(0, "the other profile's alarms are not this one's");
        _store.Profiles["profile-a"].Should().ContainSingle("the first profile's episodes stay where they are");
    }

    [Fact]
    public void TheTooltip_SaysHowManyBlindNodesNeedAttention()
    {
        BlindAlarmWatcher.TooltipSuffix(0).Should().BeNull();
        BlindAlarmWatcher.TooltipSuffix(1).Should().Be("1 blind node needs attention");
        BlindAlarmWatcher.TooltipSuffix(3).Should().Be("3 blind nodes need attention");
    }

    [Theory]
    [InlineData("old_protocol", "A blind node needs an update")]
    [InlineData("pc_too_old", "Update Bee Memory Bank on this computer")]
    [InlineData("silent", "A blind node is not in contact")]
    public void EveryKind_HasAFixedText_ThatNamesTheNodeOnlyWhenItHasAName(string kind, string title)
    {
        var named = BlindAlarmWatcher.TextFor(new BlindAlarmEntry(kind, NodeA, "Attic box", LastContact, 4, true, true));
        var locked = BlindAlarmWatcher.TextFor(new BlindAlarmEntry(kind, NodeA, null, LastContact, 4, true, true));

        named.Title.Should().Be(title);
        named.Message.Should().Contain("\"Attic box\"");
        locked.Title.Should().Be(title);
        locked.Message.Should().NotContain("Attic").And.Contain("blind node");
    }

    [Fact]
    public async Task TheSettingsStore_KeepsTheEpisodesPerProfile_AndLeavesTheOtherSettingsAlone()
    {
        var path = Path.Combine(TestScratch.New("blind-alarm-episodes"), "desktop-settings.json");
        var settings = new DesktopSettingsStore(path);
        settings.SetBool("preventSleep", true);
        var store = new DesktopSettingsEpisodeStore(settings);
        var at = new DateTime(2026, 10, 7, 9, 30, 0, DateTimeKind.Utc);

        store.Save("profile-a", new Dictionary<string, DateTime> { ["silent|x||"] = at });
        store.Save("profile-b", new Dictionary<string, DateTime> { ["pc_too_old|y||4"] = at });

        store.Load("profile-a").Should().Equal(new Dictionary<string, DateTime> { ["silent|x||"] = at });
        store.Load("profile-b").Should().ContainKey("pc_too_old|y||4");
        store.Load("profile-c").Should().BeEmpty();
        settings.GetBool("preventSleep", false).Should().BeTrue();

        store.Save("profile-a", new Dictionary<string, DateTime>());
        store.Load("profile-a").Should().BeEmpty();
        store.Load("profile-b").Should().NotBeEmpty();
        await Task.CompletedTask;
    }
}

/// <summary>
/// The shell's poll of <c>GET /node/alarms</c> (BMB-77): the node's internal key, only to this computer; the report read as the Api
/// writes it; every other answer a reason, logged once until it changes; the key never in a text.
/// </summary>
public sealed class NodeAlarmsRequestTests
{
    private const string Key = "shell-alarms-test-key-0123456789abcdef";
    private const string Front = "http://127.0.0.1:5310";

    private readonly ConcurrentQueue<string> _log = new();

    private sealed class Handler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        public ConcurrentQueue<(string Method, Uri? Uri, string? Key)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.Method.Method, request.RequestUri,
                request.Headers.TryGetValues("X-Internal-Key", out var k) ? string.Join(",", k) : null));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private NodeAlarmsRequest Make(HttpMessageHandler handler, string? frontUrl = Front, string? key = Key) =>
        new(() => frontUrl, () => key, handler, log: _log.Enqueue);

    private const string Report = """
        {"state":"ok","locked":false,"alarms":[
          {"kind":"silent","nodeId":"b11d0000-0000-8000-8000-00000000000a","name":"Attic box","since":"2026-10-07T02:00:00Z",
           "protocol":null,"notify":true,"banner":false}]}
        """;

    [Fact]
    public async Task TheReport_IsReadAsTheApiWritesIt_AndTheRequestCarriesTheKey()
    {
        var handler = new Handler(HttpStatusCode.OK, Report);

        var poll = await Make(handler).PollAsync(CancellationToken.None);

        poll.Answered.Should().BeTrue();
        poll.Answer!.State.Should().Be("ok");
        var alarm = poll.Answer.Alarms.Should().ContainSingle().Subject;
        alarm.Should().Be(new BlindAlarmEntry("silent", Guid.Parse("b11d0000-0000-8000-8000-00000000000a"), "Attic box",
            new DateTime(2026, 10, 7, 2, 0, 0, DateTimeKind.Utc), null, true, false));
        var seen = handler.Requests.Should().ContainSingle().Subject;
        seen.Method.Should().Be("GET");
        seen.Uri.Should().Be(new Uri("http://127.0.0.1:5310/node/alarms"));
        seen.Key.Should().Be(Key);
        _log.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotImplemented, "older version")]
    [InlineData(HttpStatusCode.Forbidden, "not authorized")]
    [InlineData(HttpStatusCode.BadGateway, "answered 502")]
    public async Task AnyOtherAnswer_IsAReason_LoggedOnceUntilItChanges(HttpStatusCode status, string reason)
    {
        var request = Make(new Handler(status));

        var first = await request.PollAsync(CancellationToken.None);
        await request.PollAsync(CancellationToken.None);

        first.Answered.Should().BeFalse();
        first.Detail.Should().Contain(reason);
        _log.Should().ContainSingle("polled every minute, the same reason is not repeated");
    }

    [Fact]
    public async Task AnUnreadableReport_IsAReason_NotACrash()
    {
        var poll = await Make(new Handler(HttpStatusCode.OK, "<html>not json</html>")).PollAsync(CancellationToken.None);

        poll.Answered.Should().BeFalse();
        poll.Detail.Should().Contain("could not be read");
    }

    [Fact]
    public async Task ANodeThisAppDidNotStart_IsNotAsked()
    {
        var handler = new Handler(HttpStatusCode.OK, Report);

        var poll = await Make(handler, key: null).PollAsync(CancellationToken.None);

        poll.Answered.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ANodeThatIsNotOnThisComputer_IsNotAsked_AndTheKeyNeverLeaves()
    {
        var handler = new Handler(HttpStatusCode.OK, Report);

        var poll = await Make(handler, frontUrl: "http://192.0.2.10:5310").PollAsync(CancellationToken.None);

        poll.Answered.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
        _log.Should().OnlyContain(line => !line.Contains(Key));
    }
}
