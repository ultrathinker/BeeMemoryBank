using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// LAN sync in the shell (docs/design/lan-peer-sync.md 6, D1 and D9): the person is asked ONCE per network whether to sync with the paired
/// devices there, with Yes and Not this network, and the answer is posted to the node. A Windows network is never asked about (its category
/// decides); nothing is asked in invisible mode or while the vault is locked; a network is asked at most once per run and never again after
/// an answer. The one-time notice that direct sync is on has Got it and Settings. The tray says what the status of the network is.
/// </summary>
public sealed class LanNetworkPromptTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The node as the prompt sees it: a status that answers like the real one when a network is answered, and a record of what was posted.</summary>
    private sealed class FakeNode : ILanNode
    {
        public string Setting { get; set; } = LanSyncAnswer.On;
        public bool NoticePending { get; set; }
        public List<LanNetworkEntry> Networks { get; set; } = [];
        public bool Reachable { get; set; } = true;
        public bool TakesAnswers { get; set; } = true;
        public int Polls { get; private set; }
        public ConcurrentQueue<(string Fingerprint, bool Trusted)> Answers { get; } = new();
        public int Dismissals;

        public Task<NodeLanPoll> PollAsync(CancellationToken cancellationToken)
        {
            Polls++;
            return Task.FromResult(Reachable
                ? new NodeLanPoll(new LanSyncAnswer(Setting, NoticePending, Networks.ToList()))
                : new NodeLanPoll(null, "unreachable"));
        }

        public Task<bool> AnswerNetworkAsync(string fingerprint, bool trusted, CancellationToken cancellationToken)
        {
            if (!TakesAnswers) return Task.FromResult(false);
            Answers.Enqueue((fingerprint, trusted));
            // The real node records the answer: the network is then trusted or refused, and nobody asks about it any more.
            Networks = Networks.Select(n => n.Fingerprint == fingerprint
                ? n with { Trusted = trusted, Reason = trusted ? "trusted" : "refused" }
                : n).ToList();
            return Task.FromResult(true);
        }

        public Task<bool> DismissNoticeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Dismissals);
            NoticePending = false;
            return Task.FromResult(true);
        }
    }

    private sealed record Shown(string Title, string Message, IReadOnlyList<NoticeAction> Actions, Action<string> OnAction);

    private sealed class FakeActionNotifier : IActionNotifier
    {
        public bool CanShow { get; set; } = true;
        public int Attempts { get; private set; }
        public List<Shown> Notices { get; } = [];

        public Task<bool> TryNotifyAsync(string title, string message, IReadOnlyList<NoticeAction> actions, Action<string> onAction, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (!CanShow) return Task.FromResult(false);
            Notices.Add(new Shown(title, message, actions, onAction));
            return Task.FromResult(true);
        }
    }

    private sealed class PlainNotifier : IUserNotifier
    {
        public bool CanShow { get; set; } = true;
        public int Attempts { get; private set; }
        public List<(string Title, string Message)> Notices { get; } = [];

        public void Notify(string title, string message) => TryNotify(title, message);

        public bool TryNotify(string title, string message)
        {
            Attempts++;
            if (!CanShow) return false;
            Notices.Add((title, message));
            return true;
        }
    }

    private readonly Clock _clock = new();
    private readonly FakeNode _node = new();
    private readonly FakeActionNotifier _actions = new();
    private readonly PlainNotifier _plain = new();
    private BlindAlarmsAnswer _vault = new("ok", false, []);
    private int _conditionPolls;
    private int _settingsOpened;
    private string? _profile = "profile-a";

    private LanNetworkPrompt Make(bool withActions = true, bool withPlain = true) => new(
        _node,
        _ =>
        {
            _conditionPolls++;
            return Task.FromResult(new NodeAlarmsPoll(_vault));
        },
        withActions ? _actions : null,
        withPlain ? _plain : null,
        () => Interlocked.Increment(ref _settingsOpened),
        () => _profile,
        _clock,
        _ => { });

    private static LanNetworkEntry MacNetwork(string fingerprint = "fp-home", string label = "en0 (192.168.1.20)") =>
        new("en0", "192.168.1.20", label, fingerprint, Trusted: false, Reason: "unknown", CanAsk: true, Kind: "lan");

    private static LanNetworkEntry WindowsNetwork(string reason, bool trusted, bool canAsk = false) =>
        new("Ethernet", "10.0.0.5", "Ethernet (10.0.0.5)", "fp-win", trusted, reason, canAsk, "lan");

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    // ── the question ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnknownMacNetwork_IsAskedOnce_WithTwoButtons_AndTheAnswerIsPosted()
    {
        _node.Networks = [MacNetwork()];
        var prompt = Make();

        await prompt.PollOnceAsync(CancellationToken.None);

        var notice = _actions.Notices.Should().ContainSingle("ONE notification").Subject;
        notice.Message.Should().Be("Sync with your paired devices on this network? (en0 (192.168.1.20))");
        notice.Actions.Select(a => (a.Id, a.Title)).Should().Equal(("lan.yes", "Yes"), ("lan.no", "Not this network"));
        _plain.Notices.Should().BeEmpty("the notice with buttons was shown, so the plain one is not");
        prompt.TrayState.Questions.Should().ContainSingle().Which.Fingerprint.Should().Be("fp-home");
        _node.Answers.Should().BeEmpty("nobody has answered yet");

        notice.OnAction(LanNetworkPrompt.YesId);
        await WaitForAsync(() => !_node.Answers.IsEmpty);

        _node.Answers.Should().ContainSingle().Which.Should().Be(("fp-home", true));
        await WaitForAsync(() => prompt.TrayState.Status == "trusted"); // the answer is read back at once
        prompt.TrayState.Questions.Should().BeEmpty();
    }

    [Fact]
    public async Task NotThisNetwork_PostsFalse()
    {
        _node.Networks = [MacNetwork()];
        var prompt = Make();
        await prompt.PollOnceAsync(CancellationToken.None);

        _actions.Notices.Single().OnAction(LanNetworkPrompt.NoId);
        await WaitForAsync(() => !_node.Answers.IsEmpty);

        _node.Answers.Should().ContainSingle().Which.Should().Be(("fp-home", false));
    }

    [Theory]
    [InlineData("windows-public", false, false)]
    [InlineData("windows-private", true, false)]
    [InlineData("windows-domain", true, false)]
    // The node never says canAsk for a Windows network; if it ever did, the reason still decides.
    [InlineData("windows-public", false, true)]
    [InlineData("windows-private", false, true)]
    public async Task AWindowsNetwork_IsNeverAsked(string reason, bool trusted, bool canAsk)
    {
        _node.Networks = [WindowsNetwork(reason, trusted, canAsk)];
        var prompt = Make();

        await prompt.PollOnceAsync(CancellationToken.None);
        await prompt.PollOnceAsync(CancellationToken.None);

        _actions.Attempts.Should().Be(0);
        _plain.Attempts.Should().Be(0);
        _conditionPolls.Should().Be(0, "nothing to ask, so the vault is not even looked at");
        prompt.TrayState.Questions.Should().BeEmpty();
    }

    [Fact]
    public async Task ASecondPoll_DoesNotAskAgain_AndNeitherDoesAnAnswer()
    {
        _node.Networks = [MacNetwork()];
        var prompt = Make();

        await prompt.PollOnceAsync(CancellationToken.None);
        await prompt.PollOnceAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromHours(3);
        await prompt.PollOnceAsync(CancellationToken.None);
        _actions.Notices.Should().ContainSingle("the same network is asked about once per run, answered or not");

        _actions.Notices.Single().OnAction(LanNetworkPrompt.YesId);
        await WaitForAsync(() => !_node.Answers.IsEmpty);
        // The person forgets the network on the card later in the same run: it is unknown again, and not asked again.
        _node.Networks = [MacNetwork()];
        await prompt.PollOnceAsync(CancellationToken.None);

        _actions.Notices.Should().ContainSingle("not again after an answer");
    }

    [Fact]
    public async Task ANewRunOfTheShell_AsksAgainAboutANetworkNobodyAnswered()
    {
        _node.Networks = [MacNetwork()];
        await Make().PollOnceAsync(CancellationToken.None);
        await Make().PollOnceAsync(CancellationToken.None);

        _actions.Notices.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("vpn", "vpn-off", true)]
    [InlineData("virtual", "virtual", false)]
    [InlineData("lan", "pending", false)]
    [InlineData("lan", "refused", true)]
    [InlineData("lan", "trusted", true)]
    public async Task OnlyAnUnknownLanNetworkIsAsked(string kind, string reason, bool canAsk)
    {
        _node.Networks = [MacNetwork() with { Kind = kind, Reason = reason, CanAsk = canAsk, Trusted = reason == "trusted" }];
        var prompt = Make();

        await prompt.PollOnceAsync(CancellationToken.None);

        _actions.Attempts.Should().Be(0);
        prompt.TrayState.Questions.Should().BeEmpty();
    }

    [Fact]
    public async Task ANetworkWithoutAFingerprint_IsNotAsked()
    {
        _node.Networks = [MacNetwork() with { Fingerprint = null }];

        await Make().PollOnceAsync(CancellationToken.None);

        _actions.Attempts.Should().Be(0);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("environment")]
    public async Task NothingIsAsked_WhenLanSyncIsNotOn(string setting)
    {
        _node.Setting = setting;
        _node.Networks = [MacNetwork()];

        await Make().PollOnceAsync(CancellationToken.None);

        _actions.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task NothingIsAsked_InInvisibleMode_WhileTheVaultIsLocked_OrWithoutAnAnswerAboutIt()
    {
        _node.Networks = [MacNetwork()];
        var prompt = Make();

        _vault = new BlindAlarmsAnswer("invisible", false, []);
        await prompt.PollOnceAsync(CancellationToken.None);
        _vault = new BlindAlarmsAnswer("ok", true, []);
        await prompt.PollOnceAsync(CancellationToken.None);
        _vault = null!; // the alarms poll has no report
        await prompt.PollOnceAsync(CancellationToken.None);
        _actions.Attempts.Should().Be(0);
        prompt.TrayState.Questions.Should().BeEmpty("it was not asked, so there is no question in the tray either");

        // The network was not used up by those polls: when the vault is open and visible, it is asked.
        _vault = new BlindAlarmsAnswer("warming_up", false, []);
        await prompt.PollOnceAsync(CancellationToken.None);

        _actions.Notices.Should().ContainSingle();
    }

    [Fact]
    public async Task ANodeThatDoesNotAnswer_ChangesNothing()
    {
        _node.Networks = [MacNetwork()];
        var prompt = Make();
        await prompt.PollOnceAsync(CancellationToken.None);
        var before = prompt.TrayState;

        _node.Reachable = false;
        await prompt.PollOnceAsync(CancellationToken.None);

        _actions.Notices.Should().ContainSingle();
        prompt.TrayState.Header.Should().Be(before.Header, "no answer is neither a question nor an answer");
        prompt.TrayState.Questions.Should().BeEquivalentTo(before.Questions);
    }

    [Fact]
    public async Task OneNetworkAtATime_TheNextIsAskedWhenTheFirstIsAnswered()
    {
        _node.Networks = [MacNetwork("fp-a", "en0 (10.0.0.4)"), MacNetwork("fp-b", "en1 (192.168.8.2)")];
        var prompt = Make();

        await prompt.PollOnceAsync(CancellationToken.None);
        await prompt.PollOnceAsync(CancellationToken.None);
        _actions.Notices.Should().ContainSingle().Which.Message.Should().Contain("en0 (10.0.0.4)");

        await prompt.AnswerAsync("fp-a", trusted: false);

        _actions.Notices.Should().HaveCount(2, "the answer read the status again, and the next network was asked");
        _actions.Notices[1].Message.Should().Contain("en1 (192.168.8.2)");
        prompt.TrayState.Questions.Should().ContainSingle().Which.Fingerprint.Should().Be("fp-b");
    }

    [Fact]
    public async Task ClickingTheNoticeItself_OpensTheCard_AndAnswersNothing()
    {
        _node.Networks = [MacNetwork()];
        await Make().PollOnceAsync(CancellationToken.None);

        _actions.Notices.Single().OnAction(IActionNotifier.DefaultActionId);
        await WaitForAsync(() => _settingsOpened == 1);

        _node.Answers.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAnswerTheNodeDoesNotTake_LeavesTheQuestionInTheTray()
    {
        _node.Networks = [MacNetwork()];
        var prompt = Make();
        await prompt.PollOnceAsync(CancellationToken.None);

        _node.TakesAnswers = false;
        (await prompt.AnswerAsync("fp-home", trusted: true)).Should().BeFalse();

        prompt.TrayState.Questions.Should().ContainSingle();

        _node.TakesAnswers = true;
        (await prompt.AnswerAsync("fp-home", trusted: true)).Should().BeTrue();
        prompt.TrayState.Questions.Should().BeEmpty();
        _actions.Notices.Should().ContainSingle("a failed answer is tried from the tray, not by asking again");
    }

    [Fact]
    public async Task AnAnswerGivenOnTheWebPage_TakesTheQuestionOutOfTheTray()
    {
        _node.Networks = [MacNetwork()];
        var prompt = Make();
        await prompt.PollOnceAsync(CancellationToken.None);
        prompt.TrayState.Questions.Should().ContainSingle();

        _node.Networks = [MacNetwork() with { Trusted = true, Reason = "trusted" }];
        await prompt.PollOnceAsync(CancellationToken.None);

        prompt.TrayState.Questions.Should().BeEmpty();
        _actions.Notices.Should().ContainSingle();
    }

    [Fact]
    public async Task AnotherProfile_IsAnotherNode_AndIsAskedAboutItsOwnNetworks()
    {
        _node.Networks = [MacNetwork()];
        var prompt = Make();
        await prompt.PollOnceAsync(CancellationToken.None);

        _profile = "profile-b";
        await prompt.PollOnceAsync(CancellationToken.None);

        _actions.Notices.Should().HaveCount(2);
    }

    // ── no buttons ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutButtons_TheQuestionIsAPlainNotice_AndTheTrayCarriesTheAnswers()
    {
        _node.Networks = [MacNetwork()];
        var prompt = Make(withActions: false);

        await prompt.PollOnceAsync(CancellationToken.None);
        await prompt.PollOnceAsync(CancellationToken.None);

        var notice = _plain.Notices.Should().ContainSingle().Subject;
        notice.Message.Should().StartWith("Sync with your paired devices on this network? (en0 (192.168.1.20))");
        notice.Message.Should().Contain("menu", "it says where the answer is");
        var question = prompt.TrayState.Questions.Should().ContainSingle().Subject;

        (await prompt.AnswerAsync(question.Fingerprint, trusted: true)).Should().BeTrue();

        _node.Answers.Should().ContainSingle().Which.Should().Be(("fp-home", true));
        prompt.TrayState.Questions.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenTheNoticeWithButtonsIsRefused_ThePlainOneIsShown()
    {
        _actions.CanShow = false;
        _node.Networks = [MacNetwork()];

        await Make().PollOnceAsync(CancellationToken.None);

        _actions.Attempts.Should().Be(1);
        _plain.Notices.Should().ContainSingle();
    }

    [Fact]
    public async Task ANoticeNobodyCouldShow_IsTriedAgainAfterAWhile_NotEveryMinute()
    {
        _actions.CanShow = false;
        _plain.CanShow = false;
        _node.Networks = [MacNetwork()];
        var prompt = Make();

        await prompt.PollOnceAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromMinutes(1);
        await prompt.PollOnceAsync(CancellationToken.None);
        _actions.Attempts.Should().Be(1, "tried again after the pause, not at once");
        prompt.TrayState.Questions.Should().ContainSingle("the tray has the question meanwhile");

        _clock.Now += LanNetworkPrompt.RetryEvery;
        _actions.CanShow = true;
        await prompt.PollOnceAsync(CancellationToken.None);

        _actions.Notices.Should().ContainSingle();
        await prompt.PollOnceAsync(CancellationToken.None);
        _actions.Attempts.Should().Be(2, "once shown, it is not shown again");
    }

    [Fact]
    public async Task WithNoNotifierAtAll_TheTrayIsTheQuestion_AndNothingIsRetried()
    {
        _node.Networks = [MacNetwork()];
        var prompt = Make(withActions: false, withPlain: false);

        await prompt.PollOnceAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromHours(1);
        await prompt.PollOnceAsync(CancellationToken.None);

        prompt.TrayState.Questions.Should().ContainSingle();
    }

    // ── the one-time notice (D9) ─────────────────────────────────────────────────────────────────────────

    private static LanNetworkEntry Trusted() => MacNetwork() with { Trusted = true, Reason = "trusted" };

    [Fact]
    public async Task TheNotice_IsShownOnce_WithGotItAndSettings_AndGotItPostsTheDismissal()
    {
        _node.Networks = [Trusted()];
        _node.NoticePending = true;
        var prompt = Make();

        await prompt.PollOnceAsync(CancellationToken.None);
        await prompt.PollOnceAsync(CancellationToken.None);

        var notice = _actions.Notices.Should().ContainSingle().Subject;
        notice.Message.Should().Be("Direct sync with your devices on this network is on. Only the sync connection is opened, never the web page.");
        notice.Actions.Select(a => a.Title).Should().Equal("Got it", "Settings");
        _node.Dismissals.Should().Be(0);

        notice.OnAction(LanNetworkPrompt.GotItId);
        await WaitForAsync(() => _node.Dismissals == 1);

        await prompt.PollOnceAsync(CancellationToken.None);
        _actions.Notices.Should().ContainSingle();
    }

    [Fact]
    public async Task TheNoticesSettingsButton_OpensTheCard_AndDismissesNothing()
    {
        _node.Networks = [Trusted()];
        _node.NoticePending = true;
        var prompt = Make();
        await prompt.PollOnceAsync(CancellationToken.None);

        _actions.Notices.Single().OnAction(LanNetworkPrompt.SettingsId);
        await WaitForAsync(() => _settingsOpened == 1);

        _node.Dismissals.Should().Be(0, "the card has its own Got it");
        await prompt.PollOnceAsync(CancellationToken.None);
        _actions.Notices.Should().ContainSingle("once per run, even while it is still pending");
    }

    [Fact]
    public async Task TheNotice_WaitsForTheQuestion_AndForATrustedNetwork()
    {
        _node.NoticePending = true;
        _node.Networks = [MacNetwork()];
        var prompt = Make();

        await prompt.PollOnceAsync(CancellationToken.None);
        _actions.Notices.Should().ContainSingle().Which.Actions.Select(a => a.Id).Should().Contain(LanNetworkPrompt.YesId, "the question comes first");

        await prompt.AnswerAsync("fp-home", trusted: false);
        _actions.Notices.Should().ContainSingle("the person said no: nothing is on, so it does not say so");

        _node.Networks = [Trusted()];
        await prompt.PollOnceAsync(CancellationToken.None);
        _actions.Notices.Should().HaveCount(2);
        _actions.Notices[1].Title.Should().Be(LanNetworkPrompt.NoticeTitle);
    }

    [Fact]
    public async Task TheNotice_IsNotShown_InInvisibleMode_OrWhileLocked()
    {
        _node.Networks = [Trusted()];
        _node.NoticePending = true;
        var prompt = Make();

        _vault = new BlindAlarmsAnswer("invisible", false, []);
        await prompt.PollOnceAsync(CancellationToken.None);
        _vault = new BlindAlarmsAnswer("ok", true, []);
        await prompt.PollOnceAsync(CancellationToken.None);
        _actions.Attempts.Should().Be(0);

        _vault = new BlindAlarmsAnswer("ok", false, []);
        await prompt.PollOnceAsync(CancellationToken.None);
        _actions.Notices.Should().ContainSingle();
    }

    [Fact]
    public async Task WithoutButtons_TheNoticeIsShownPlain_AndDismissedAtOnce()
    {
        _node.Networks = [Trusted()];
        _node.NoticePending = true;
        var prompt = Make(withActions: false);

        await prompt.PollOnceAsync(CancellationToken.None);
        await prompt.PollOnceAsync(CancellationToken.None);

        _plain.Notices.Should().ContainSingle().Which.Message.Should().StartWith("Direct sync with your devices on this network is on.");
        _node.Dismissals.Should().Be(1);
    }

    [Fact]
    public async Task WithoutAnyNotifier_TheNoticeIsLeftToTheCard()
    {
        _node.Networks = [Trusted()];
        _node.NoticePending = true;

        await Make(withActions: false, withPlain: false).PollOnceAsync(CancellationToken.None);

        _node.Dismissals.Should().Be(0, "nobody saw it");
    }

    [Fact]
    public async Task TheNotice_IsNotShown_WhenNothingIsPending()
    {
        _node.Networks = [Trusted()];

        await Make().PollOnceAsync(CancellationToken.None);

        _actions.Attempts.Should().Be(0);
    }

    // ── the tray ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("on", "trusted", "Sync on this network: trusted (Change...)")]
    [InlineData("on", "refused", "Sync on this network: not trusted (Change...)")]
    [InlineData("on", "unknown", "Sync on this network: not trusted (Change...)")]
    [InlineData("on", "windows-public", "Sync on this network: not trusted (Change...)")]
    [InlineData("on", "windows-private", "Sync on this network: trusted (Change...)")]
    [InlineData("off", "unknown", "Sync on this network: off (Change...)")]
    public async Task TheTrayHeader_SaysWhetherThisNetworkIsTrusted(string setting, string reason, string expected)
    {
        _node.Setting = setting;
        _node.Networks = [MacNetwork() with { Reason = reason, Trusted = reason is "trusted" or "windows-private" }];
        var prompt = Make(withActions: false, withPlain: false);

        await prompt.PollOnceAsync(CancellationToken.None);

        prompt.TrayState.Header.Should().Be(expected);
    }

    [Fact]
    public async Task TheTrayHeader_CountsNetworks_WhenTheyDiffer_AndIgnoresVirtualOnesAndVpns()
    {
        _node.Networks =
        [
            Trusted(),
            MacNetwork("fp-b", "en1 (172.16.0.9)") with { Reason = "refused", CanAsk = true },
            MacNetwork("", "docker0") with { Kind = "virtual", Reason = "virtual", CanAsk = false, Fingerprint = null },
            MacNetwork("fp-v", "utun3") with { Kind = "vpn", Reason = "vpn-off" },
        ];
        var prompt = Make(withActions: false, withPlain: false);

        await prompt.PollOnceAsync(CancellationToken.None);

        prompt.TrayState.Status.Should().Be("trusted on 1 of 2 networks");
    }

    [Fact]
    public async Task TheTray_HasNothingToSay_BeforeTheNodeAnswers_OnAnOlderNode_OrWhenTheEnvironmentDecides()
    {
        var prompt = Make();
        prompt.TrayState.Header.Should().BeNull();

        _node.Reachable = false;
        await prompt.PollOnceAsync(CancellationToken.None);
        prompt.TrayState.Header.Should().BeNull();

        _node.Reachable = true;
        _node.Setting = "environment";
        _node.Networks = [Trusted()];
        await prompt.PollOnceAsync(CancellationToken.None);
        prompt.TrayState.Header.Should().BeNull();

        _node.Setting = "on";
        _node.Networks = [];
        await prompt.PollOnceAsync(CancellationToken.None);
        prompt.TrayState.Header.Should().BeNull("no network, nothing to be trusted");
    }

    [Fact]
    public async Task Changed_IsRaisedWhenTheTrayStateChanges_NotOnEveryPoll()
    {
        _node.Networks = [MacNetwork()];
        var prompt = Make();
        var raised = 0;
        prompt.Changed += (_, _) => raised++;

        await prompt.PollOnceAsync(CancellationToken.None);
        var afterFirst = raised;
        await prompt.PollOnceAsync(CancellationToken.None);
        await prompt.PollOnceAsync(CancellationToken.None);

        afterFirst.Should().BeGreaterThan(0);
        raised.Should().Be(afterFirst);
    }
}
