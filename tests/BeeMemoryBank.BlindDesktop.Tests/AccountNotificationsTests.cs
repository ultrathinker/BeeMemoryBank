using BeeMemoryBank.BlindDesktop.Services;
using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>The tray lines follow the accounts: the line of a job that ended with its account does not stay in the tooltip.</summary>
public sealed class AccountNotificationsTests
{
    private const string First = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static string Tooltip(FakeAccounts accounts, AccountNotifications notifications)
    {
        var snapshot = notifications.Reconcile(accounts.GetSnapshot);
        return AccountsTooltip.For(BlindAccountsView.Build(snapshot, TimeZoneInfo.Utc, new ManualTimeProvider().GetUtcNow()), notifications.Line);
    }

    private static FakeAccounts TwoAccountsOneRunningABackup(out FakeApp backup)
    {
        var accounts = new FakeAccounts();
        (_, backup) = accounts.AddAccount("Berlin", First, status: FakeApp.StatusOf(activeJob: "Backup", progress: 0.42));
        accounts.AddAccount("Paris", Second);
        return accounts;
    }

    [Fact]
    public void AJobShowsOnTheTooltip_WhileItsAccountIsListed()
    {
        var accounts = TwoAccountsOneRunningABackup(out _);
        var notifications = new AccountNotifications();

        Tooltip(accounts, notifications).Should().Contain("Backup: 42 %");
        notifications.Line.Should().Be("Backup: 42 %");
    }

    [Fact]
    public void RemovingAnAccountWhileItsJobRuns_TakesItsLineOffTheTooltip()
    {
        var accounts = TwoAccountsOneRunningABackup(out _);
        var notifications = new AccountNotifications();
        Tooltip(accounts, notifications).Should().Contain("Backup: 42 %");

        // The account is removed with its job still reporting (the removal stopped it, and its last line was never cleared): the next refresh does not see the account at all.
        accounts.Entries.RemoveAll(e => e.Id == First);
        accounts.Apps.Remove(First);
        var after = Tooltip(accounts, notifications);

        notifications.Line.Should().BeNull("nothing runs any more");
        after.Should().NotContain("Backup").And.Contain("1 copy");
    }

    [Fact]
    public void ARemovedAccountsOldLine_DoesNotHideTheJobOfAnotherAccount()
    {
        var accounts = TwoAccountsOneRunningABackup(out _);
        var notifications = new AccountNotifications();
        Tooltip(accounts, notifications);
        accounts.Apps[Second].Status = FakeApp.StatusOf(activeJob: "First load", progress: 0.1);
        accounts.Entries.RemoveAll(e => e.Id == First);
        accounts.Apps.Remove(First);

        Tooltip(accounts, notifications).Should().Contain("First load: 10 %").And.NotContain("Backup");
    }

    [Fact]
    public void AnAccountThatIsBeingRemoved_ShowsNoLine_ButKeepsItsObject_SoAFailedRemovalShowsItAgain()
    {
        var accounts = TwoAccountsOneRunningABackup(out _);
        var notifications = new AccountNotifications();
        Tooltip(accounts, notifications);
        var line = notifications.For(First);

        var at = accounts.Entries.FindIndex(e => e.Id == First);
        accounts.Entries[at] = accounts.Entries[at] with { Phase = BlindAccountPhases.Deleting };
        Tooltip(accounts, notifications);
        notifications.Line.Should().BeNull("its job is being stopped");
        notifications.For(First).Should().BeSameAs(line, "the account's runtime holds this object: the removal may still fail and the account stay");

        accounts.Entries[at] = accounts.Entries[at] with { Phase = BlindAccountPhases.Active };
        Tooltip(accounts, notifications).Should().Contain("Backup: 42 %");
        notifications.For(First).Should().BeSameAs(line);
    }

    [Fact]
    public void AnAccountThatIsGone_IsForgotten_SoItsObjectIsNotKept()
    {
        var accounts = TwoAccountsOneRunningABackup(out _);
        var notifications = new AccountNotifications();
        Tooltip(accounts, notifications);
        var line = notifications.For(First);
        var other = notifications.For(Second);

        accounts.Entries.RemoveAll(e => e.Id == First);
        accounts.Apps.Remove(First);
        Tooltip(accounts, notifications);

        notifications.For(First).Should().NotBeSameAs(line, "the removed account's entry was dropped");
        notifications.For(Second).Should().BeSameAs(other, "the other account keeps its own");
    }

    [Fact]
    public void AnAccountMadeWhileTheListIsRead_IsNotForgottenByAReadThatCameBeforeItWasListed()
    {
        var notifications = new AccountNotifications();
        var accounts = new FakeAccounts();
        TrayNotifications? made = null;

        // The list is read, and while it is, the new account's runtime asks for its line (the entry was written just after the read was taken).
        notifications.Reconcile(() =>
        {
            var snapshot = accounts.GetSnapshot();
            made = notifications.For(Second);
            return snapshot;
        });
        made!.Show("First load", 0.3);

        notifications.For(Second).Should().BeSameAs(made, "a stale read does not make the line of a live account an orphan");
        notifications.Line.Should().Be("First load: 30 %");
    }
}
