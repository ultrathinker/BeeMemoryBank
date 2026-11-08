using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// The full-peer alarm (docs/design/lan-peer-sync.md 10): a computer, a server or a phone with no contact by any route for three days is
/// <c>peer_silent</c> in <c>GET /node/alarms</c>. The shell notifies it as it does a silent blind node: one notice per episode after two polls
/// that agree, a reminder every 24 hours, the name only while the vault is unlocked, and the tray counts it ("N devices need attention").
/// </summary>
public sealed class PeerSilentAlarmTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Desktop = Guid.Parse("a0a00000-0000-4000-8000-0000000000a1");
    private static readonly Guid Attic = Guid.Parse("b11d0000-0000-8000-8000-00000000000a");

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : IBlindAlarmEpisodeStore
    {
        public Dictionary<string, Dictionary<string, DateTime>> Profiles { get; } = new();

        public IReadOnlyDictionary<string, DateTime> Load(string profileId) =>
            Profiles.TryGetValue(profileId, out var e) ? new Dictionary<string, DateTime>(e) : new Dictionary<string, DateTime>();

        public void Save(string profileId, IReadOnlyDictionary<string, DateTime> notified) =>
            Profiles[profileId] = new Dictionary<string, DateTime>(notified);
    }

    private readonly Clock _clock = new();
    private readonly RecordingNotifier _notifier = new();
    private NodeAlarmsPoll _next = Judged();

    private BlindAlarmWatcher Make() =>
        new(_ => Task.FromResult(_next), _notifier, new MemoryStore(), () => "profile-a", _clock, _ => { }, TimeSpan.Zero);

    /// <summary>The last contact is four days before the first poll after the grace: the alarm is "four days".</summary>
    private static readonly DateTime LastContact = T0.UtcDateTime.AddMinutes(11).AddDays(-4);

    private static BlindAlarmEntry PeerSilent(Guid node, string? name = "Desktop", DateTime? since = null) =>
        new("peer_silent", node, name, since ?? LastContact, null, Notify: true, Banner: false);

    private static NodeAlarmsPoll Judged(params BlindAlarmEntry[] alarms) => new(new BlindAlarmsAnswer("ok", false, alarms));

    private async Task PollAsync(BlindAlarmWatcher watcher, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            _clock.Now += BlindAlarmWatcher.PollInterval;
            await watcher.PollOnceAsync(CancellationToken.None);
        }
    }

    private async Task<BlindAlarmWatcher> PastTheGraceAsync()
    {
        var watcher = Make();
        await PollAsync(watcher, (int)BlindAlarmWatcher.StartGrace.TotalMinutes + 1);
        return watcher;
    }

    [Fact]
    public async Task ASilentDevice_IsNotifiedOncePerEpisode_WithItsNameAndTheDays()
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(PeerSilent(Desktop));

        await PollAsync(watcher);
        _notifier.Notices.Should().BeEmpty("one poll is not enough: no flapping");
        await PollAsync(watcher);

        var notice = _notifier.Notices.Should().ContainSingle().Subject;
        notice.Title.Should().Be("A device is not in contact");
        notice.Message.Should().Be("\"Desktop\" has not been heard from for 4 days, directly or through your other devices.");

        await PollAsync(watcher, 30);
        _notifier.Notices.Should().ContainSingle("the same episode is not notified again within a day");
    }

    [Fact]
    public async Task ALockedVault_HasNoName_AndTheTextSaysADeviceOfThisNetwork()
    {
        var watcher = await PastTheGraceAsync();
        _next = new NodeAlarmsPoll(new BlindAlarmsAnswer("ok", true, [PeerSilent(Desktop, name: null)]));

        await PollAsync(watcher, 2);

        _notifier.Notices.Should().ContainSingle().Which.Message
            .Should().Be("A device of this network has not been heard from for 4 days, directly or through your other devices.");
    }

    [Fact]
    public async Task AReminder_ComesAfterADay_AndCountsTheDaysAgain()
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(PeerSilent(Desktop));
        await PollAsync(watcher, 2);

        await PollAsync(watcher, (int)BlindAlarmWatcher.RemindEvery.TotalMinutes - 1);
        _notifier.Notices.Should().HaveCount(1);
        await PollAsync(watcher);

        _notifier.Notices.Should().HaveCount(2, "a day after the first notice, a reminder");
        _notifier.Notices.Last().Message.Should().Contain("for 5 days");
    }

    [Fact]
    public async Task AnEpisodeThatEnds_AndStartsAgain_IsNotifiedAgain()
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(PeerSilent(Desktop));
        await PollAsync(watcher, 2);

        _next = Judged(); // it came back
        await PollAsync(watcher, 2);
        _next = Judged(PeerSilent(Desktop, since: LastContact.AddDays(5))); // and went quiet again
        await PollAsync(watcher, 2);

        _notifier.Notices.Should().HaveCount(2);
    }

    [Fact]
    public async Task ASilentDevice_IsCountedForTheTray_AndTheWordsAreDevices()
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(PeerSilent(Desktop));

        await PollAsync(watcher, 2);

        watcher.AttentionCount.Should().Be(1);
        watcher.AttentionPeerCount.Should().Be(1);
        BlindAlarmWatcher.TooltipSuffix(watcher.AttentionCount, watcher.AttentionPeerCount).Should().Be("1 device needs attention");
    }

    [Fact]
    public async Task ABlindNodeAndADevice_AreBothCounted()
    {
        var watcher = await PastTheGraceAsync();
        _next = Judged(PeerSilent(Desktop), new BlindAlarmEntry("silent", Attic, "Attic box", LastContact, null, true, false));

        await PollAsync(watcher, 2);

        watcher.AttentionCount.Should().Be(2);
        watcher.AttentionPeerCount.Should().Be(1);
        BlindAlarmWatcher.TooltipSuffix(2, 1).Should().Be("2 devices need attention");
        _notifier.Notices.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(1, 0, "1 blind node needs attention")]
    [InlineData(3, 0, "3 blind nodes need attention")]
    [InlineData(1, 1, "1 device needs attention")]
    [InlineData(2, 1, "2 devices need attention")]
    [InlineData(2, 2, "2 devices need attention")]
    public void TheTooltip_KeepsTheBlindWordsUntilADeviceIsAmongThem(int count, int fullPeers, string? expected) =>
        BlindAlarmWatcher.TooltipSuffix(count, fullPeers).Should().Be(expected);

    [Fact]
    public void TheDays_AreWholeDaysSinceTheLastContact_AtLeastThree_AndSeveralWhenThereIsNoTime()
    {
        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

        BlindAlarmWatcher.TextFor(PeerSilent(Desktop, since: now.AddDays(-9).AddHours(-3)), now).Message.Should().Contain("for 9 days");
        BlindAlarmWatcher.TextFor(PeerSilent(Desktop, since: now.AddDays(-3)), now).Message.Should().Contain("for 3 days");
        BlindAlarmWatcher.TextFor(PeerSilent(Desktop, since: now.AddHours(-1)), now).Message.Should().Contain("for 3 days", "the node raises it at three days; a clock a little behind does not say less");
        BlindAlarmWatcher.TextFor(PeerSilent(Desktop) with { Since = null }, now).Message.Should().Contain("for several days");
    }

    [Fact]
    public async Task MoreThanThreeAlarms_AreSummedUp_InWordsThatFitDevicesToo()
    {
        var watcher = await PastTheGraceAsync();
        var other = Guid.Parse("a0a00000-0000-4000-8000-0000000000a2");
        var third = Guid.Parse("a0a00000-0000-4000-8000-0000000000a3");
        var fourth = Guid.Parse("a0a00000-0000-4000-8000-0000000000a4");
        _next = Judged(PeerSilent(Desktop), PeerSilent(other, "Mac"), PeerSilent(third, "Phone"), PeerSilent(fourth, "Server"));

        await PollAsync(watcher, 2);

        var notices = _notifier.Notices.ToArray();
        notices.Should().HaveCount(3);
        notices.Last().Title.Should().Be("Devices need attention");
        notices.Last().Message.Should().Be("2 more alarms. Open Bee Memory Bank, Devices, to see them.");
    }

    [Fact]
    public async Task MoreThanThreeBlindAlarms_StillSayBlindNodes()
    {
        var watcher = await PastTheGraceAsync();
        var ids = Enumerable.Range(1, 4).Select(i => Guid.Parse($"b11d0000-0000-8000-8000-0000000000{i:x2}")).ToArray();
        _next = Judged(ids.Select(id => new BlindAlarmEntry("silent", id, "Box", LastContact, null, true, false)).ToArray());

        await PollAsync(watcher, 2);

        _notifier.Notices.Last().Title.Should().Be("Blind nodes need attention");
    }

    // ── the node's answer ────────────────────────────────────────────────────────────────────────────────

    private sealed class Answering(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    [Fact]
    public async Task APeerSilentEntry_ReadsFromTheNodesJson_WithANullNameWhileLocked()
    {
        const string json = """
            {"state":"ok","locked":true,"alarms":[
              {"kind":"peer_silent","nodeId":"a0a00000-0000-4000-8000-0000000000a1","name":null,"since":"2026-11-04T08:30:00Z","protocol":null,"notify":true,"banner":false}
            ]}
            """;
        using var request = new NodeAlarmsRequest(() => "http://127.0.0.1:5310", () => "key-0123456789", new Answering(json), log: _ => { });

        var poll = await request.PollAsync(CancellationToken.None);

        var alarm = poll.Answer!.Alarms.Should().ContainSingle().Subject;
        alarm.Kind.Should().Be(BlindAlarmWatcher.PeerSilent);
        alarm.Name.Should().BeNull();
        alarm.Since.Should().Be(new DateTime(2026, 11, 4, 8, 30, 0, DateTimeKind.Utc));
        alarm.Notify.Should().BeTrue();
        poll.Answer.Locked.Should().BeTrue();
    }
}
