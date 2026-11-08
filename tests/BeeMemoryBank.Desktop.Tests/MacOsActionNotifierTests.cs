using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.MacOS;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// The notice with buttons on a Mac: permission first, one request id per notice, the button that comes back found by that id and passed on
/// once (a click on the notice itself as <see cref="IActionNotifier.DefaultActionId"/>, a swipe as nothing). The calls into
/// <c>UNUserNotificationCenter</c> are a port; the real one is checked on a Mac, from the signed app (see the last facts).
/// </summary>
public sealed class MacOsActionNotifierTests
{
    private sealed class FakePort : IUserNotificationsPort
    {
        public bool Available { get; set; } = true;
        public bool Authorized { get; set; } = true;
        public bool Accepts { get; set; } = true;
        public int AuthorizationRequests { get; private set; }
        public List<(string Id, string Title, string Body, string Category, string[] Buttons)> Posted { get; } = [];

        public bool IsAvailable => Available;

        public event Action<string, string>? ActionChosen;

        public void Choose(string requestId, string action) => ActionChosen?.Invoke(requestId, action);

        public Task<bool> RequestAuthorizationAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
            AuthorizationRequests++;
            return Task.FromResult(Authorized);
        }

        public Task<bool> PostAsync(string requestId, string title, string body, string categoryId, IReadOnlyList<NoticeAction> actions, CancellationToken cancellationToken)
        {
            if (!Accepts) return Task.FromResult(false);
            Posted.Add((requestId, title, body, categoryId, actions.Select(a => a.Id).ToArray()));
            return Task.FromResult(true);
        }
    }

    private static readonly NoticeAction[] Buttons = [new("lan.yes", "Yes"), new("lan.no", "Not this network")];

    private readonly FakePort _port = new();

    private MacOsActionNotifier Make() => new(_port, TimeProvider.System, _ => { });

    [Fact]
    public async Task ANotice_IsPostedWithItsButtons_AndTheChosenOneComesBackOnce()
    {
        var notifier = Make();
        var chosen = new List<string>();

        (await notifier.TryNotifyAsync("Sync on this network?", "Sync with your paired devices on this network? (en0)", Buttons, chosen.Add)).Should().BeTrue();

        var posted = _port.Posted.Should().ContainSingle().Subject;
        posted.Title.Should().Be("Sync on this network?");
        posted.Category.Should().Be("bmb.actions.lan.yes+lan.no");
        posted.Buttons.Should().Equal("lan.yes", "lan.no");

        _port.Choose(posted.Id, "lan.no");
        _port.Choose(posted.Id, "lan.yes");

        chosen.Should().Equal("lan.no");
    }

    [Fact]
    public async Task AClickOnTheNoticeItself_IsTheDefaultAction_AndASwipeIsNothing()
    {
        var notifier = Make();
        var clicked = new List<string>();
        var swiped = new List<string>();
        await notifier.TryNotifyAsync("a", "b", Buttons, clicked.Add);
        await notifier.TryNotifyAsync("c", "d", Buttons, swiped.Add);

        _port.Choose(_port.Posted[0].Id, MacOsActionNotifier.SystemDefaultAction);
        _port.Choose(_port.Posted[1].Id, MacOsActionNotifier.SystemDismissAction);
        _port.Choose(_port.Posted[1].Id, "lan.yes");

        clicked.Should().Equal(IActionNotifier.DefaultActionId);
        swiped.Should().BeEmpty("a notice that was swiped away is not answered, and a late button of it is not heard");
    }

    [Fact]
    public async Task ANoticeOfAnotherRun_OrOfNoOne_IsIgnored()
    {
        Make();

        var act = () => _port.Choose("bmb.notice.from-an-earlier-run", "lan.yes");

        act.Should().NotThrow();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WhereTheNotificationCentreCannotBeUsed_NothingIsAsked_AndNothingIsPosted()
    {
        _port.Available = false;

        (await Make().TryNotifyAsync("a", "b", Buttons, _ => { })).Should().BeFalse();

        _port.AuthorizationRequests.Should().Be(0);
        _port.Posted.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutPermission_TheNoticeIsNotPosted()
    {
        _port.Authorized = false;

        (await Make().TryNotifyAsync("a", "b", Buttons, _ => { })).Should().BeFalse();

        _port.Posted.Should().BeEmpty();
    }

    [Fact]
    public async Task ANoticeTheSystemRefuses_IsFalse_AndItsButtonsAreForgotten()
    {
        _port.Accepts = false;
        var notifier = Make();
        var chosen = new List<string>();

        (await notifier.TryNotifyAsync("a", "b", Buttons, chosen.Add)).Should().BeFalse();
        _port.Accepts = true;
        await notifier.TryNotifyAsync("a", "b", Buttons, _ => { });
        _port.Choose(_port.Posted.Single().Id, "lan.yes");

        chosen.Should().BeEmpty();
    }

    [Fact]
    public async Task AnActionThatThrows_DoesNotReachTheSystem()
    {
        var notifier = Make();
        await notifier.TryNotifyAsync("a", "b", Buttons, _ => throw new InvalidOperationException());

        var act = () => _port.Choose(_port.Posted.Single().Id, "lan.yes");

        act.Should().NotThrow();
    }

    [Fact]
    public void NoticesWithTheSameButtons_ShareACategory_AndOthersDoNot()
    {
        MacOsActionNotifier.CategoryFor(Buttons).Should().Be(MacOsActionNotifier.CategoryFor([new("lan.yes", "Oui"), new("lan.no", "Non")]));
        MacOsActionNotifier.CategoryFor(Buttons).Should().NotBe(MacOsActionNotifier.CategoryFor([new("lan.gotit", "Got it"), new("lan.settings", "Settings", true)]));
    }

    // ── the real notification centre ─────────────────────────────────────────────────────────────────────

    [MacOnlyFact]
    public async Task OutsideASignedAppBundle_TheRealPortSaysNoAndDoesNotTouchTheNotificationCentre()
    {
        // The test host is `dotnet`, not a .app: UNUserNotificationCenter would raise an Objective-C exception, which kills the process.
        // Reaching the next line is the check.
        var notifier = new MacOsActionNotifier();

        (await notifier.TryNotifyAsync("Test", "Test", Buttons, _ => { })).Should().BeFalse();
        new UserNotificationsPort().IsAvailable.Should().BeFalse();
    }
}
