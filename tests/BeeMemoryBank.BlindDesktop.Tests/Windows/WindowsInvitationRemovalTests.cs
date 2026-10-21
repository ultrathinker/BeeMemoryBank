using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Tests.Windows;

/// <summary>
/// The window's invitation watch against a removal, over the real composition (the account host, DPAPI and state files per account, the real <see cref="MainViewModel"/> and its watch) and a hub
/// that holds its answer: the person confirms Remove while the watch is inside the hub's identity page, the redemption or the status question. The account is gone when Remove ends, whatever the
/// hub does with the held request afterwards (a cancelled call, or a socket that goes on waiting): no second look at the hub, no state file written again, no account listed again.
/// </summary>
public sealed class WindowsInvitationRemovalTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>The runtime of the Windows app over a scratch folder, a clock the test moves and one hub, with the window's view model on it (its watch is the runtime's own).</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public string Root { get; } = TestFolders.New("invite-removal");
        public ManualTimeProvider Clock { get; } = new();
        public DesktopInviteHub Hub { get; }
        public BlindDesktopRuntime Runtime { get; private set; } = null!;
        public MainViewModel Window { get; private set; } = null!;
        private bool _windowOpened;

        public Rig() => Hub = new DesktopInviteHub("hub-a.test", Clock);

        public async Task<Rig> StartAsync()
        {
            Runtime = BlindDesktopRuntime.Create(new WindowsTestPlatform(PlatformSelector.Create(Root)), () => null, Clock, displayName: () => "Test Desktop", primaryHandler: _ => () => Hub);
            await Runtime.StartAsync();
            // The device loop wakes on every change of the accounts and asks about waiting invitations on its own; it is stopped, so that the only asker of the hub is the one the test names
            // (the loop asks through the same host call, which the AppCore tests drive).
            await Runtime.Scheduler.StopAsync();
            return this;
        }

        /// <summary>The window appears (its view model is made now, not earlier: a window that exists while an account is added starts the watch on its own at the first change of the accounts).</summary>
        public void OpenWindow()
        {
            Window = new MainViewModel(Runtime.Accounts, Runtime.WorkFor, Runtime.Autostart, _ => [], time: Clock, zone: TimeZoneInfo.Utc, watch: Runtime.Watch);
            _windowOpened = true;
            Window.OnWindowShown();
        }

        public async ValueTask DisposeAsync()
        {
            if (_windowOpened) Window.Dispose();
            await Runtime.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        /// <summary>Every file and folder under the root that carries the account's id in its path.</summary>
        public IEnumerable<string> PathsOf(string accountId) =>
            Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories).Where(p => p.Contains(accountId, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Gave up waiting for " + what);
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Adds an account whose invitation is still pending (its first try ended the way <paramref name="route"/> says), moves the clock past the back-off, and opens the window: its watch asks, and the hub
    /// holds the answer on <paramref name="route"/>. Returns the account's id and the held request.
    /// </summary>
    private static async Task<(string Id, InviteHold Hold)> AccountWhoseWatchIsInsideTheHubAsync(Rig rig, string route)
    {
        // identity / redeem: the first try finds no hub (a retry is pending). status: the hub took the redemption and the owner has not decided.
        if (route is "identity" or "redeem") rig.Hub.Fail(route, DesktopInviteHub.HubFailure.Unreachable);
        var added = await rig.Runtime.Accounts.AddFromInvitationAsync(rig.Hub.IssueText());
        added.Ok.Should().BeTrue(added.Error);
        var id = added.AccountId!;
        rig.Runtime.Accounts.GetSnapshot().Accounts.Single().Entry.Id.Should().Be(id);
        rig.Clock.Advance(TimeSpan.FromSeconds(20));

        var hold = rig.Hub.HoldNext(route);
        rig.OpenWindow();
        try { await hold.Entered.WaitAsync(Patience); }
        catch (TimeoutException)
        {
            var card = rig.Runtime.Accounts.GetSnapshot().Accounts.SingleOrDefault();
            throw new TimeoutException($"The watch never reached the hub's {route}. Hub calls: {string.Join(",", rig.Hub.Calls)}; watch running: {rig.Runtime.Watch.IsRunning}; phase: {card?.Entry.Phase}; note: {card?.Note}; problem: {card?.Problem}; now: {rig.Clock.GetUtcNow():O}");
        }
        rig.Runtime.Watch.IsRunning.Should().BeTrue("the watch is the one inside the hub");
        return (id, hold);
    }

    private static async Task ConfirmRemoveAsync(Rig rig)
    {
        await rig.Window.RefreshAsync();
        var detail = rig.Window.Detail ?? throw new InvalidOperationException("no account is shown");
        detail.RemoveCommand.Execute(null);
        detail.ConfirmRemoveCommand.Execute(null);
    }

    private static async Task NothingOfTheAccountIsLeftAsync(Rig rig, string id)
    {
        // The watch paces itself on the test's clock, which does not move: it is stopped, as hiding the window does.
        await rig.Runtime.Watch.StopAsync();
        rig.Runtime.Accounts.GetSnapshot().Accounts.Should().BeEmpty("the account was removed and is not listed again");
        rig.PathsOf(id).Should().BeEmpty("its folder, its state and its keys were deleted and not made again");
        File.ReadAllText(Path.Combine(rig.Root, "accounts.json")).Should().NotContain(id);
        await rig.Window.RefreshAsync();
        rig.Window.Rows.Should().BeEmpty();
        rig.Window.ShowAdd.Should().BeTrue("with no account the window is on the Add page");
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("redeem")]
    [InlineData("status")]
    public async Task RemovingAnAccount_WhileTheWatchIsInsideTheHub_EndsTheAsk_AndTheHubSeesNothingMoreOfIt(string route)
    {
        await using var rig = await new Rig().StartAsync();
        var (id, hold) = await AccountWhoseWatchIsInsideTheHubAsync(rig, route);
        var redemptionsBefore = rig.Hub.Redemptions;

        await ConfirmRemoveAsync(rig);
        await WaitUntil(() => rig.Runtime.Accounts.GetSnapshot().Accounts.Count == 0 && !rig.Window.IsBusy, "the removal to end");
        hold.Release();

        await NothingOfTheAccountIsLeftAsync(rig, id);
        rig.Hub.Redemptions.Should().Be(redemptionsBefore, "the redemption the cancelled call carried was not taken");
        var calls = rig.Hub.Calls;
        rig.Clock.Advance(TimeSpan.FromHours(2));
        (await rig.Runtime.Accounts.PollInvitationsAsync(force: true)).Should().Be(0);
        rig.Hub.Calls.Should().Equal(calls, "nothing asks the hub for an account that is gone");
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("redeem")]
    [InlineData("status")]
    public async Task RemovingAnAccount_WhileTheHubIgnoresTheCancel_WaitsForTheAnswer_AndThenWritesNothingForTheAccount(string route)
    {
        await using var rig = await new Rig().StartAsync();
        rig.Hub.IgnoresCancellation = true;
        var (id, hold) = await AccountWhoseWatchIsInsideTheHubAsync(rig, route);

        await ConfirmRemoveAsync(rig);
        await Task.Delay(300);
        rig.Window.IsBusy.Should().BeTrue("Remove waits for the call that is in the hub: its answer is not applied to an account that is being cleared");
        rig.PathsOf(id).Should().NotBeEmpty("nothing is deleted while the call is out");

        hold.Release();
        await WaitUntil(() => rig.Runtime.Accounts.GetSnapshot().Accounts.Count == 0 && !rig.Window.IsBusy, "the removal to end");

        await NothingOfTheAccountIsLeftAsync(rig, id);
        var calls = rig.Hub.Calls;
        rig.Clock.Advance(TimeSpan.FromHours(2));
        (await rig.Runtime.Accounts.PollInvitationsAsync(force: true)).Should().Be(0);
        rig.Hub.Calls.Should().Equal(calls, "the late answer started no second call, and nothing asks for an account that is gone");
        rig.Hub.Count("other").Should().Be(0, "nothing of the removed account reached the hub's sync routes");
    }
}
