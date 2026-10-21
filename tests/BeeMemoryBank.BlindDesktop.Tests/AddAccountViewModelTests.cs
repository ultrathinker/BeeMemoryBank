using System.Text.RegularExpressions;
using BeeMemoryBank.Boundary;
using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>"Add account": the paste box and its preview (the view record's sentences), the add itself, and the old way with a pairing code.</summary>
public sealed class AddAccountViewModelTests
{
    private const string Invitation = "bmb-invite:?v=1&a=https%3A%2F%2Fmem.example.org";

    private static BlindInvitationPreview Good(ManualTimeProvider time) =>
        new("Alex", "mem.example.org", BlindAccountDefaults.ForInvited(isPhone: false), time.GetUtcNow().AddMinutes(28));

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        condition().Should().BeTrue("the condition should have become true");
    }

    [Fact]
    public void Pasting_ShowsThePreview_InTheViewRecordsWords_AndAddBecomesPossible()
    {
        var time = new ManualTimeProvider();
        var accounts = new FakeAccounts();
        var preview = Good(time);
        accounts.Previews[Invitation] = preview;
        var vm = new AddAccountViewModel(accounts, time);
        vm.AddInvitationCommand.CanExecute(null).Should().BeFalse("nothing is pasted");

        vm.InviteText = "  " + Invitation + "\n";

        vm.HasPreview.Should().BeTrue();
        vm.PreviewDescription.Should().Be(preview.Description).And.Be("A copy of Alex's Bee Memory Bank, kept encrypted: this device cannot read it. It comes from mem.example.org. This invitation is a one-time code, not an encrypted one.");
        vm.PreviewLimits.Should().Be(preview.Limits).And.Contain("2 GB").And.Contain("200 MB a day");
        vm.PreviewLimits.Should().NotContain("unmetered", "a computer starts without the two phone switches");
        vm.PreviewValidity.Should().Be(preview.Validity(time.GetUtcNow()));
        vm.HasRefusal.Should().BeFalse();
        vm.CanAddFromInvitation.Should().BeTrue();
        vm.AddInvitationCommand.CanExecute(null).Should().BeTrue();
        accounts.CallsSoFar.Should().Contain("Preview:" + Invitation, "the text is trimmed before it is looked at");
    }

    [Fact]
    public void ARefusedInvitation_ShowsTheControllersSentence_AndAddStaysOff()
    {
        var time = new ManualTimeProvider();
        var accounts = new FakeAccounts();
        var refused = new BlindInvitationPreview("Alex", "mem.example.org", BlindAccountDefaults.ForInvited(false), time.GetUtcNow() - TimeSpan.FromMinutes(1),
            "This invitation has expired.", BlindInvitationRefusals.Expired);
        accounts.Previews[Invitation] = refused;
        var vm = new AddAccountViewModel(accounts, time);

        vm.InviteText = Invitation;

        vm.HasRefusal.Should().BeTrue();
        vm.PreviewRefusal.Should().Be("This invitation has expired.");
        vm.PreviewDescription.Should().BeEmpty("a refused invitation shows no promise");
        vm.CanAddFromInvitation.Should().BeFalse();
        vm.AddInvitationCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ATextThatIsNotAnInvitation_IsRefusedInTheControllersWords()
    {
        var accounts = new FakeAccounts();
        var vm = new AddAccountViewModel(accounts);

        vm.InviteText = "hello";

        vm.PreviewRefusal.Should().Be("This is not an invitation.");
        vm.CanAddFromInvitation.Should().BeFalse();

        vm.InviteText = "";
        vm.HasPreview.Should().BeFalse("an empty box has no preview");
    }

    [Fact]
    public async Task Add_MakesTheAccount_ClearsTheText_AndTellsTheWindowWhichOne()
    {
        var time = new ManualTimeProvider();
        var accounts = new FakeAccounts();
        accounts.Previews[Invitation] = Good(time);
        var vm = new AddAccountViewModel(accounts, time);
        string? added = null;
        vm.Added += id => added = id;
        vm.InviteText = Invitation;

        vm.AddInvitationCommand.Execute(null);

        await WaitFor(() => added is not null);
        accounts.Entries.Should().ContainSingle().Which.Id.Should().Be(added);
        vm.InviteText.Should().BeEmpty("the invitation's text is not kept once it was used");
        vm.HasPreview.Should().BeFalse();
        vm.IsBusy.Should().BeFalse();
        accounts.CallsSoFar.Should().Contain("AddFromInvitation:" + Invitation);
    }

    [Fact]
    public async Task AnAddThatIsRefused_ShowsTheSentence_KeepsTheText_AndMakesNoAccount()
    {
        var time = new ManualTimeProvider();
        var accounts = new FakeAccounts();
        accounts.Previews[Invitation] = Good(time);
        accounts.OnAddFromInvitation = (_, _) => Task.FromResult(new BlindAddResult(null, "The invitation is not valid any more. Ask the owner for a new one."));
        var vm = new AddAccountViewModel(accounts, time);
        vm.InviteText = Invitation;

        vm.AddInvitationCommand.Execute(null);

        await WaitFor(() => vm.HasError);
        vm.Error.Should().Be("The invitation is not valid any more. Ask the owner for a new one.");
        vm.InviteText.Should().Be(Invitation);
        accounts.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAccountThatWasMadeButCouldNotStart_IsSelected_AndTheSentenceIsStillShown()
    {
        var time = new ManualTimeProvider();
        var accounts = new FakeAccounts();
        accounts.Previews[Invitation] = Good(time);
        accounts.OnAddFromInvitation = (_, _) =>
        {
            var (entry, _) = accounts.AddAccount("Alex", phase: BlindAccountPhases.Pending);
            return Task.FromResult(new BlindAddResult(entry.Id, "It could not be started: the key storage of this device did not answer."));
        };
        var vm = new AddAccountViewModel(accounts, time);
        string? added = null;
        vm.Added += id => added = id;
        vm.InviteText = Invitation;

        vm.AddInvitationCommand.Execute(null);

        await WaitFor(() => added is not null);
        vm.Error.Should().Be("It could not be started: the key storage of this device did not answer.", "the text box is cleared first, then the sentence is set");
        vm.InviteText.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAddThatThrows_IsAnError_NotAnException_AndIsLogged()
    {
        var time = new ManualTimeProvider();
        var accounts = new FakeAccounts();
        accounts.Previews[Invitation] = Good(time);
        accounts.OnAddFromInvitation = (_, _) => throw new InvalidOperationException("the host is closed");
        var problems = new List<string>();
        var vm = new AddAccountViewModel(accounts, time, (what, _) => problems.Add(what));
        vm.InviteText = Invitation;

        vm.AddInvitationCommand.Execute(null);

        await WaitFor(() => vm.HasError);
        vm.Error.Should().Contain("unexpected problem").And.NotContain("the host is closed");
        problems.Should().Equal("Adding an account from an invitation failed");
        vm.IsBusy.Should().BeFalse("the page is usable again");
    }

    [Fact]
    public async Task WhileTheAddRuns_NoSecondAddCanStart()
    {
        var time = new ManualTimeProvider();
        var accounts = new FakeAccounts();
        accounts.Previews[Invitation] = Good(time);
        var gate = new TaskCompletionSource<BlindAddResult>();
        var started = 0;
        accounts.OnAddFromInvitation = (_, _) =>
        {
            started++;
            return gate.Task;
        };
        var vm = new AddAccountViewModel(accounts, time);
        vm.InviteText = Invitation;

        vm.AddInvitationCommand.Execute(null);
        await WaitFor(() => vm.IsBusy);
        vm.AddInvitationCommand.CanExecute(null).Should().BeFalse();
        vm.AddWithCodeCommand.CanExecute(null).Should().BeFalse();
        vm.AddInvitationCommand.Execute(null);

        gate.SetResult(new BlindAddResult(null, "No."));
        await WaitFor(() => !vm.IsBusy);
        started.Should().Be(1);
    }

    [Fact]
    public async Task TheOldWay_AddsAnAccountWithAPairingCode_UnderTheNameGiven()
    {
        var accounts = new FakeAccounts();
        var vm = new AddAccountViewModel(accounts);
        string? added = null;
        vm.Added += id => added = id;

        vm.NameText = "  Berlin office ";
        vm.AddWithCodeCommand.Execute(null);

        await WaitFor(() => added is not null);
        accounts.CallsSoFar.Should().Contain("AddAsync:Berlin office");
        accounts.Entries.Single().Name.Should().Be("Berlin office");
        vm.NameText.Should().BeEmpty();
    }

    [Fact]
    public async Task TheOldWay_WithNoName_LetsTheControllerNameIt()
    {
        var accounts = new FakeAccounts();
        var vm = new AddAccountViewModel(accounts);

        vm.AddWithCodeCommand.Execute(null);

        await WaitFor(() => accounts.Entries.Count == 1);
        accounts.CallsSoFar.Should().Contain("AddAsync:<null>");
    }

    [Fact]
    public void Paste_AsksTheWindowToReadTheClipboard()
    {
        var vm = new AddAccountViewModel(new FakeAccounts());
        var asked = 0;
        vm.PasteRequested += () => asked++;

        vm.PasteCommand.Execute(null);

        asked.Should().Be(1);
    }

    [Fact]
    public void Clear_ForgetsTheTextTheNameAndTheError()
    {
        var accounts = new FakeAccounts();
        var vm = new AddAccountViewModel(accounts);
        vm.InviteText = "hello";
        vm.NameText = "x";

        vm.Clear();

        vm.InviteText.Should().BeEmpty();
        vm.NameText.Should().BeEmpty();
        vm.HasError.Should().BeFalse();
        vm.HasPreview.Should().BeFalse();
    }

    // ---- a hostile or careless paste is bounded before anything is done with it ---------------------------------------------------------------------

    [Fact]
    public void AVeryLargeMalformedPaste_IsRefusedBeforeItIsTrimmedOrPreviewed_AndIsNotKept()
    {
        var accounts = new FakeAccounts();
        var vm = new AddAccountViewModel(accounts);
        var huge = "bmb-invite:?" + new string('x', 8 * 1024 * 1024);

        vm.InviteText = huge;

        accounts.CallsSoFar.Should().NotContain(c => c.StartsWith("Preview:"), "a text this long is not looked at by the controller, let alone copied by a trim");
        vm.InviteText.Should().BeEmpty("it is not kept");
        vm.Error.Should().Be(AddAccountViewModel.TooLongText);
        vm.HasError.Should().BeTrue();
        vm.HasPreview.Should().BeFalse();
        vm.AddInvitationCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ATextOverTheLongestInvitation_IsRefusedWithoutAPreview_AndOneAtTheLimitIsLookedAt()
    {
        var accounts = new FakeAccounts();
        var vm = new AddAccountViewModel(accounts);

        vm.InviteText = new string('x', BlindInvitation.MaxTextLength + 1);
        vm.Error.Should().Be(AddAccountViewModel.TooLongText);
        vm.HasPreview.Should().BeFalse();

        vm.InviteText = new string('x', BlindInvitation.MaxTextLength);
        vm.HasError.Should().BeFalse("the edit clears the refusal");
        vm.HasPreview.Should().BeTrue("a text no longer than an invitation may be is the controller's to judge");
        accounts.CallsSoFar.Where(c => c.StartsWith("Preview:")).Should().ContainSingle();
    }

    [Fact]
    public void BlankSpaceAroundAnInvitation_IsNotCountedAgainstIt()
    {
        var time = new ManualTimeProvider();
        var accounts = new FakeAccounts();
        accounts.Previews[Invitation] = Good(time);
        var vm = new AddAccountViewModel(accounts, time);

        vm.InviteText = new string('\n', 1500) + Invitation + new string(' ', 1500);

        vm.HasError.Should().BeFalse();
        vm.CanAddFromInvitation.Should().BeTrue();
        accounts.CallsSoFar.Should().Contain("Preview:" + Invitation);
    }

    [Fact]
    public void ARefusedPaste_DoesNotStandInTheWayOfTheNextOne()
    {
        var time = new ManualTimeProvider();
        var accounts = new FakeAccounts();
        accounts.Previews[Invitation] = Good(time);
        var vm = new AddAccountViewModel(accounts, time);
        vm.InviteText = new string('x', 10_000);
        vm.HasError.Should().BeTrue();

        vm.InviteText = Invitation;

        vm.HasError.Should().BeFalse();
        vm.CanAddFromInvitation.Should().BeTrue();
    }

    [Fact]
    public void ThePasteBox_HasTheSameLengthLimit_ThatTheViewModelEnforces()
    {
        var window = File.ReadAllText(Path.Combine(VaultBoundary.RepoRoot(), "desktop", "BeeMemoryBank.BlindDesktop", "Views", "MainWindow.axaml"));

        var box = Regex.Match(window, @"<TextBox[^>]*Text=""\{Binding InviteText\}""[^>]*>");

        box.Success.Should().BeTrue("the paste box is the TextBox bound to InviteText");
        box.Value.Should().Contain("MaxLength=\"{x:Static vm:AddAccountViewModel.MaxPasteLength}\"", "the box itself holds no more than the view model takes");
        AddAccountViewModel.MaxPasteLength.Should().BeGreaterThan(BlindInvitation.MaxTextLength, "a cut paste still reads as too long, not as a shorter text");
    }
}
