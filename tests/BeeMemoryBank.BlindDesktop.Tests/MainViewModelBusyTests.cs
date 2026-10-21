using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// BMB-311, the blind desktop copy with several accounts: a long operation puts up a "please wait" pop-up over the whole window whoever started it - the Add page (the identity probe and the
/// redemption go over the network), the selected account's removal, or a job its panel asked for (those are <see cref="AccountPanelBusyTests"/>) - and the window's own
/// <see cref="MainViewModel.IsBusy"/> follows them.
/// </summary>
public sealed class MainViewModelBusyTests
{
    private const string Invitation = "bmb-invite:?v=1&a=https%3A%2F%2Fmem.example.org";

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        condition().Should().BeTrue("the condition should have become true");
    }

    private static MainViewModel NewWindow(FakeAccounts accounts) =>
        new(accounts, _ => new FakeWorkRequests(), new FakeAutostart(), _ => [], zone: TimeZoneInfo.Utc);

    [Fact]
    public async Task WhileAnInvitationIsBeingAdded_TheWindowShowsAPopUp_AndItGoesWhenTheAddEnds()
    {
        var accounts = new FakeAccounts();
        var time = new ManualTimeProvider();
        accounts.Previews[Invitation] = new BlindInvitationPreview("Alex", "mem.example.org", BlindAccountDefaults.ForInvited(false), time.GetUtcNow().AddMinutes(20));
        var gate = new TaskCompletionSource<BlindAddResult>();
        accounts.OnAddFromInvitation = (_, _) => gate.Task;
        var vm = NewWindow(accounts);
        await vm.RefreshAsync();
        vm.IsBusy.Should().BeFalse();
        vm.Add.InviteText = Invitation;

        vm.Add.AddInvitationCommand.Execute(null);

        await WaitFor(() => vm.IsBusy);
        vm.BusyTitle.Should().Be("Adding the account");
        vm.BusyText.Should().Contain("Please wait");
        var (entry, _) = accounts.AddAccount("Alex", phase: BlindAccountPhases.AwaitingConfirmation);
        gate.SetResult(new BlindAddResult(entry.Id));
        await WaitFor(() => !vm.IsBusy);
        await WaitFor(() => vm.Detail?.Id == entry.Id);
        vm.ShowAdd.Should().BeFalse("the new account is shown, waiting for its owner");
        vm.Detail!.AwaitingOwner.Should().BeTrue();
        vm.Dispose();
    }

    [Fact]
    public async Task WhileTheSelectedAccountIsBeingRemoved_TheWindowShowsAPopUp_AndThenTheNextAccount()
    {
        var accounts = new FakeAccounts();
        var (first, _) = accounts.AddAccount("Berlin office");
        var (second, _) = accounts.AddAccount("Paris");
        var gate = new TaskCompletionSource();
        accounts.OnRemove = _ => gate.Task;
        var vm = NewWindow(accounts);
        await vm.RefreshAsync();
        vm.Detail!.Id.Should().Be(first.Id);
        vm.Detail.RemoveCommand.Execute(null);

        vm.Detail.ConfirmRemoveCommand.Execute(null);

        await WaitFor(() => vm.IsBusy);
        vm.BusyTitle.Should().Be("Removing the account");
        gate.SetResult();
        await WaitFor(() => !vm.IsBusy);
        await WaitFor(() => vm.Detail?.Id == second.Id);
        vm.Rows.Select(r => r.Id).Should().Equal(second.Id);
        vm.Dispose();
    }

    [Fact]
    public async Task RemovingTheLastAccount_LeavesTheAddPage()
    {
        var accounts = new FakeAccounts();
        accounts.AddAccount("Berlin office");
        var vm = NewWindow(accounts);
        await vm.RefreshAsync();
        vm.Detail!.RemoveCommand.Execute(null);

        vm.Detail.ConfirmRemoveCommand.Execute(null);

        await WaitFor(() => vm.Detail is null);
        vm.ShowAdd.Should().BeTrue();
        vm.Rows.Should().BeEmpty();
        vm.Dispose();
    }
}
