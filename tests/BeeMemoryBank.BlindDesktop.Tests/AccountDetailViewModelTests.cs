using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// The selected account on the right: its card (every sentence from <see cref="BlindAccountCard"/>), Limits (the form is <see cref="BlindLimitsForm"/>), Pause, Rename and Remove (the
/// confirmation is <see cref="BlindRemoveConfirmation"/>), each a call on <see cref="IBlindAccountsController"/> whose refusal is shown as its own sentence.
/// </summary>
public sealed class AccountDetailViewModelTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    private sealed class Rig
    {
        public FakeAccounts Accounts { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public FakeWorkRequests Work { get; } = new();
        public BlindAccountEntry Entry { get; }
        public FakeApp App { get; }
        public AccountDetailViewModel Vm { get; }
        public List<string> Problems { get; } = [];

        public Rig(string phase = BlindAccountPhases.Active, BlindAppStatus? status = null, Func<string, Task<BlindStoreInfo?>>? store = null)
        {
            (Entry, App) = Accounts.AddAccount("Berlin office", phase: phase, status: status);
            Vm = new AccountDetailViewModel(Accounts, Entry.Id, Work, _ => [1], null, (what, _) => Problems.Add(what),
                store is null ? null : (id, _) => store(id), Time, Zone);
        }

        public BlindAccountSnapshot Snapshot() => Accounts.GetSnapshot().Accounts.Single(a => a.Entry.Id == Entry.Id);

        public AccountDetailViewModel Shown()
        {
            Vm.Apply(Snapshot());
            return Vm;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        condition().Should().BeTrue("the condition should have become true");
    }

    // ---- the card -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCardLines_AreTheViewRecordsSentences()
    {
        var rig = new Rig(status: FakeApp.StatusOf(lastSyncAt: new ManualTimeProvider().GetUtcNow() - TimeSpan.FromMinutes(4)));
        rig.Accounts.Usage[rig.Entry.Id] = new BlindAccountUsageView(2L << 30, 312, 30L << 20, 8L << 20, 40L << 20);
        rig.Accounts.Entries[0] = rig.Entry with { Limits = new BlindAccountLimits(4L << 30, 200L << 20, null) };

        var vm = rig.Shown();

        var card = BlindAccountCard.Build(rig.Snapshot(), Zone, rig.Time.GetUtcNow());
        vm.Name.Should().Be(card.Name);
        vm.StateLine.Should().Be(card.State).And.Be("Synced 4 min ago");
        vm.Stored.Should().Be(card.Stored).And.Be("2 GB of 4 GB");
        vm.Today.Should().Be(card.Today).And.Contain("312 requests");
        vm.Average.Should().Be(card.Average);
        vm.LastSync.Should().Be(card.LastSync);
        vm.SourceHost.Should().Be(card.SourceHost).And.NotBeEmpty();
        vm.StateKind.Should().Be(card.Kind);
    }

    [Fact]
    public void APausedAccount_SaysSoInTheCardsWords_AndTheButtonOffersResume()
    {
        var rig = new Rig();
        rig.Accounts.Entries[0] = rig.Entry with { PausedByUser = true };
        rig.Accounts.Pauses[rig.Entry.Id] = new BlindPause(BlindPauseReasons.PausedByYou);

        var vm = rig.Shown();

        vm.StateLine.Should().Be("Paused: paused by you.");
        vm.PauseText.Should().Be("Resume");
    }

    // ---- limits ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheLimitBoxes_AreFilledByTheForm_WithSizesInMbAndGb()
    {
        var rig = new Rig();
        rig.Accounts.Entries[0] = rig.Entry with { Limits = new BlindAccountLimits(2L << 30, 200L << 20, 5000), UnmeteredOnly = true, PauseOnPowerSaving = true };

        var vm = rig.Shown();

        var form = BlindLimitsForm.From(rig.Accounts.Entries[0]);
        (vm.StoredText, vm.TrafficText, vm.RequestsText, vm.UnmeteredOnly, vm.PauseOnPowerSaving)
            .Should().Be((form.Stored, form.TrafficPerDay, form.RequestsPerDay, true, true));
        vm.StoredText.Should().Be("2 GB");
        vm.TrafficText.Should().Be("200 MB");
        vm.RequestsText.Should().Be("5000");
    }

    [Fact]
    public void Saving_SendsTheFormsLimits_AndTheSwitches_ToTheController()
    {
        var rig = new Rig();
        var vm = rig.Shown();

        vm.StoredText = "3 GB";
        vm.TrafficText = "250 MB";
        vm.RequestsText = "";
        vm.UnmeteredOnly = true;
        vm.PauseOnPowerSaving = false;
        vm.ApplyLimitsCommand.Execute(null);

        rig.Accounts.CallsSoFar.Should().Contain($"SetLimits:{3L << 30}/{250L << 20}/").And.Contain("SetSwitches:True/False");
        rig.Accounts.Entries[0].EffectiveLimits.Should().Be(new BlindAccountLimits(3L << 30, 250L << 20, null));
        vm.HasLimitsProblems.Should().BeFalse();
        vm.Notice.Should().Be("Limits saved.");
        vm.StoredText.Should().Be("3 GB", "the box shows what is now stored");
    }

    [Fact]
    public void ALimitTheFormRefuses_IsShownAsTheFormsSentence_AndNothingIsSent()
    {
        var rig = new Rig();
        var vm = rig.Shown();

        vm.RequestsText = "50";
        vm.TrafficText = "1 MB";
        vm.ApplyLimitsCommand.Execute(null);

        var expected = new BlindLimitsForm("", "1 MB", "50", false, false).Validate().Problems;
        expected.Should().HaveCount(2);
        vm.LimitsProblems.Should().Be(string.Join("\n", expected));
        rig.Accounts.CallsSoFar.Should().NotContain(c => c.StartsWith("SetLimits") || c.StartsWith("SetSwitches"));
    }

    [Fact]
    public void ZeroIsAPause_NotUnlimited_AndTheFormAcceptsIt()
    {
        var rig = new Rig();
        var vm = rig.Shown();

        vm.StoredText = "0";
        vm.ApplyLimitsCommand.Execute(null);

        rig.Accounts.CallsSoFar.Should().Contain("SetLimits:0//");
        rig.Accounts.Entries[0].EffectiveLimits.MaxStoredBytes.Should().Be(0);
    }

    [Fact]
    public void ARefusalOfTheController_IsShownAsItsSentence()
    {
        var rig = new Rig();
        rig.Accounts.Refusals["SetLimits"] = "The list of accounts is damaged: nothing is changed.";
        var vm = rig.Shown();

        vm.StoredText = "3 GB";
        vm.ApplyLimitsCommand.Execute(null);

        vm.LimitsProblems.Should().Be("The list of accounts is damaged: nothing is changed.");
        vm.Notice.Should().BeNull();
    }

    [Fact]
    public void WhatThePersonTypes_IsNotTakenAwayByARefresh_UntilItIsSaved()
    {
        var rig = new Rig();
        var vm = rig.Shown();

        vm.StoredText = "7 G";
        vm.RenameText = "Bern";
        vm.Apply(rig.Snapshot());

        vm.StoredText.Should().Be("7 G");
        vm.RenameText.Should().Be("Bern");

        vm.StoredText = "7 GB";
        vm.ApplyLimitsCommand.Execute(null);
        rig.Accounts.Entries[0] = rig.Accounts.Entries[0] with { Limits = new BlindAccountLimits(9L << 30) };
        vm.Apply(rig.Snapshot());
        vm.StoredText.Should().Be("9 GB", "after a save the boxes follow the account again");
    }

    // ---- pause, rename ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Pause_AndResume_GoToTheController()
    {
        var rig = new Rig();
        var vm = rig.Shown();
        vm.PauseText.Should().Be("Pause");

        vm.TogglePauseCommand.Execute(null);
        rig.Accounts.Entries[0].PausedByUser.Should().BeTrue();
        vm.PauseText.Should().Be("Resume");

        vm.TogglePauseCommand.Execute(null);
        rig.Accounts.Entries[0].PausedByUser.Should().BeFalse();
        rig.Accounts.CallsSoFar.Should().ContainInOrder("SetPaused:True", "SetPaused:False");
    }

    [Fact]
    public void Rename_GoesToTheController_AndItsRefusalIsShown()
    {
        var rig = new Rig();
        var vm = rig.Shown();

        vm.RenameText = "Paris branch";
        vm.RenameCommand.Execute(null);
        rig.Accounts.Entries[0].Name.Should().Be("Paris branch");
        vm.Name.Should().Be("Paris branch");

        rig.Accounts.Refusals["Rename"] = "The name is longer than 64 characters.";
        vm.RenameText = new string('x', 80);
        vm.RenameCommand.Execute(null);
        vm.Notice.Should().Be("The name is longer than 64 characters.");
    }

    // ---- reconnect, repair --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhenTheOwnerRemovedTheDevice_TheCardSaysSo_AndTryAgainAsksTheSourceOnceMore()
    {
        var rig = new Rig(phase: BlindAccountPhases.RemovedByOwner);
        rig.Accounts.Entries[0] = rig.Entry with { InviteId = "i1" };
        var vm = rig.Shown();

        vm.CanReconnect.Should().BeTrue();
        vm.StateLine.Should().Be(BlindAccountCard.Build(rig.Snapshot(), Zone, rig.Time.GetUtcNow()).State).And.Contain("removed this device");
        vm.HasPanel.Should().BeFalse("the copy stays until the person removes the account; nothing to sync");

        vm.ReconnectCommand.Execute(null);

        rig.Accounts.CallsSoFar.Should().Contain("Reconnect");
        vm.CanReconnect.Should().BeFalse();
        vm.HasPanel.Should().BeTrue("the account is active again");
    }

    [Fact]
    public void DamagedUsageCounts_OfferStartNewCounts()
    {
        var rig = new Rig();
        rig.Accounts.Usage[rig.Entry.Id] = new BlindAccountUsageView(1, 0, 0, 0, null, Problem: "The usage counts cannot be read.");
        var vm = rig.Shown();
        vm.CanRepairUsage.Should().BeTrue();
        vm.Today.Should().Be("usage counts unavailable");

        vm.RepairUsageCommand.Execute(null);

        rig.Accounts.CallsSoFar.Should().Contain("StartNewUsageLedger");
    }

    // ---- today's panel ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePanel_ExistsOnlyWhileTheAccountIsActiveAndUp()
    {
        var rig = new Rig();
        var vm = rig.Shown();
        vm.Panel.Should().NotBeNull();

        rig.Accounts.Entries[0] = rig.Entry with { Phase = BlindAccountPhases.Refused };
        vm.Apply(rig.Snapshot());
        vm.Panel.Should().BeNull();
        vm.HasPanel.Should().BeFalse();

        rig.Accounts.Entries[0] = rig.Entry with { Phase = BlindAccountPhases.Active };
        rig.Accounts.Apps.Remove(rig.Entry.Id);   // the account's copy is not up
        vm.Apply(rig.Snapshot());
        vm.Panel.Should().BeNull("a copy that is not up has nothing to show");
    }

    [Fact]
    public void ThePanel_IsMadeAgain_WhenTheAccountsCopyWasRebuilt()
    {
        var rig = new Rig();
        var vm = rig.Shown();
        var first = vm.Panel;

        rig.Accounts.Apps[rig.Entry.Id] = new FakeApp { Status = FakeApp.StatusOf(name: "Berlin office") };   // a wipe makes a new runtime and a new controller
        vm.Apply(rig.Snapshot());

        vm.Panel.Should().NotBeNull().And.NotBeSameAs(first);
    }

    [Fact]
    public async Task ThePanelsStoreLine_IsAskedOfThisAccount()
    {
        var asked = new List<string>();
        var rig = new Rig(store: id =>
        {
            asked.Add(id);
            return Task.FromResult<BlindStoreInfo?>(new BlindStoreInfo(DateTime.UtcNow - TimeSpan.FromDays(1), 3, 1 << 20));
        });
        rig.Shown();

        await rig.Vm.RefreshPanelAsync();
        await WaitFor(() => asked.Count > 0);

        asked.Should().AllBe(rig.Entry.Id);
    }

    // ---- remove ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void RemoveAsks_FirstWithTheViewRecordsConfirmation_AndRemovesNothingYet()
    {
        var rig = new Rig();
        rig.Accounts.Usage[rig.Entry.Id] = new BlindAccountUsageView(1_288_490_189, 0, 0, 0, null);
        var vm = rig.Shown();

        vm.RemoveCommand.Execute(null);

        var confirmation = BlindRemoveConfirmation.Build(rig.Snapshot());
        vm.RemovePanelOpen.Should().BeTrue();
        (vm.RemoveTitle, vm.RemoveMessage, vm.RemoveConfirmText, vm.RemoveCancelText)
            .Should().Be((confirmation.Title, confirmation.Message, confirmation.ConfirmText, confirmation.CancelText));
        vm.RemoveMessage.Should().Contain("1.2 GB and its keys are deleted from this device");
        rig.Accounts.CallsSoFar.Should().NotContain(c => c.StartsWith("Remove:"));

        vm.CancelRemoveCommand.Execute(null);
        vm.RemovePanelOpen.Should().BeFalse();
        rig.Accounts.CallsSoFar.Should().NotContain(c => c.StartsWith("Remove:"));
    }

    [Fact]
    public async Task ConfirmingRemove_RemovesTheAccount_ShowsAPopUpWhileItRuns_AndTellsTheWindow()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Accounts.OnRemove = _ => gate.Task;
        var vm = rig.Shown();
        var removed = 0;
        vm.Removed += () => removed++;
        vm.RemoveCommand.Execute(null);

        vm.ConfirmRemoveCommand.Execute(null);

        await WaitFor(() => rig.Accounts.CallsSoFar.Contains("Remove:" + rig.Entry.Id));
        vm.IsBusy.Should().BeTrue();
        vm.AnyBusy.Should().BeTrue();
        vm.AnyBusyTitle.Should().Be("Removing the account");
        vm.ConfirmRemoveCommand.CanExecute(null).Should().BeFalse("no second removal while one runs");
        gate.SetResult();
        await WaitFor(() => !vm.IsBusy);
        removed.Should().Be(1);
        vm.RemovePanelOpen.Should().BeFalse();
        vm.HasPanel.Should().BeFalse();
    }

    [Fact]
    public async Task ARemovalThatCouldNotFinish_SaysWhatIsLeft_AndStaysOpenToPressAgain()
    {
        var rig = new Rig();
        rig.Accounts.OnRemove = _ => throw new AggregateException("The removal is not complete: its folder is in use.", new IOException("in use"));
        var vm = rig.Shown();
        vm.RemoveCommand.Execute(null);

        vm.ConfirmRemoveCommand.Execute(null);

        await WaitFor(() => vm.Notice?.Contains("press Remove again") == true);
        vm.Notice.Should().Contain("not removed completely").And.NotContain("in use", "the kind of failure is told, not the message of the exception");
        vm.IsBusy.Should().BeFalse();
        vm.RemovePanelOpen.Should().BeTrue("the confirmation stays so that the person can press it again");
    }
}
