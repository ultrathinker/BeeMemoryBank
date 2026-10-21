using System.Windows.Input;
using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.ViewModels;

/// <summary>
/// The right-hand side of the window for ONE account: its card (every sentence from <see cref="BlindAccountCard"/>), today's panel (<see cref="AccountPanelViewModel"/>, only while the
/// account is active and its copy is up), and the account's own controls - Limits (the form is <see cref="BlindLimitsForm"/>: sizes with MB/GB, the two switches), Pause, Rename and
/// Remove (the confirmation is <see cref="BlindRemoveConfirmation"/>) - each a call on <see cref="IBlindAccountsController"/>. It holds no rule: a refusal comes back as the sentence of the
/// controller or of the form and is shown as it is.
///
/// <para>The text boxes are filled from the account when the account is first shown and again after a change was applied, but never while the person is typing: a refresh that arrives
/// between two key strokes must not take the text away.</para>
/// </summary>
public sealed class AccountDetailViewModel : ObservableObject, IDisposable
{
    private readonly IBlindAccountsController _accounts;
    private readonly IBlindWorkRequests _work;
    private readonly Func<string, byte[]> _qr;
    private readonly Action<Action> _post;
    private readonly Action<string, Exception>? _problem;
    private readonly Func<string, CancellationToken, Task<BlindStoreInfo?>>? _store;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private IBlindAppController? _controller;
    private BlindAccountSnapshot? _snapshot;
    private bool _applying;
    private bool _limitsDirty;
    private bool _renameDirty;
    private bool _disposed;

    private BlindAccountCard? _card;
    private AccountPanelViewModel? _panel;
    private string _storedText = "";
    private string _trafficText = "";
    private string _requestsText = "";
    private bool _unmeteredOnly;
    private bool _pauseOnPowerSaving;
    private string? _limitsProblems;
    private string _renameText = "";
    private string? _notice;
    private bool _removePanelOpen;
    private string _removeTitle = "";
    private string _removeMessage = "";
    private string _removeConfirmText = "Remove";
    private string _removeCancelText = "Keep it";
    private bool _busy;
    private string _busyTitle = "";
    private string _busyText = "";
    private bool _canReconnect;
    private bool _awaitingOwner;

    public AccountDetailViewModel(IBlindAccountsController accounts, string accountId, IBlindWorkRequests work, Func<string, byte[]> qr,
        Action<Action>? post = null, Action<string, Exception>? problem = null, Func<string, CancellationToken, Task<BlindStoreInfo?>>? store = null,
        TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        _accounts = accounts;
        Id = accountId;
        _work = work;
        _qr = qr;
        _post = post ?? (action => action());
        _problem = problem;
        _store = store;
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;

        Action<Exception> failed = CommandFailed;
        ApplyLimitsCommand = new RelayCommand(ApplyLimits, () => !_busy && _card is { CanEdit: true }, failed);
        TogglePauseCommand = new RelayCommand(TogglePause, () => !_busy && _card is { CanEdit: true }, failed);
        RenameCommand = new RelayCommand(Rename, () => !_busy && _card is { CanEdit: true }, failed);
        ReconnectCommand = new RelayCommand(Reconnect, () => !_busy && _canReconnect, failed);
        RepairUsageCommand = new RelayCommand(RepairUsage, () => !_busy && _card is { CanRepairUsage: true }, failed);
        // busy-exempt: only opens the confirmation, nothing is sent
        RemoveCommand = new RelayCommand(OpenRemove, () => _snapshot is not null && !_busy, failed);
        CancelRemoveCommand = new RelayCommand(() => RemovePanelOpen = false, () => !_busy, failed);
        ConfirmRemoveCommand = new RelayCommand(() => _ = RemoveAsync(), () => !_busy && _removePanelOpen, failed);
    }

    /// <summary>The local id of the account (not the node id the source knows).</summary>
    public string Id { get; }

    public ICommand ApplyLimitsCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand ReconnectCommand { get; }
    public ICommand RepairUsageCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand CancelRemoveCommand { get; }
    public ICommand ConfirmRemoveCommand { get; }

    /// <summary>Raised when the account has been removed (the window selects another).</summary>
    public event Action? Removed;

    /// <summary>Raised when the person asks to copy the pairing code of this account; the window puts it on the clipboard.</summary>
    public event Action<string>? CopyRequested;

    /// <summary>The card as the accounts view built it (null before the first <see cref="Apply"/>).</summary>
    public BlindAccountCard? Card => _card;

    public string Name => _card?.Name ?? "";
    public string SourceHost => _card?.SourceHost ?? "";
    public string StateLine => _card?.State ?? "";
    public BlindCardKind StateKind => _card?.Kind ?? BlindCardKind.Working;
    public string Stored => _card?.Stored ?? "";
    public string Today => _card?.Today ?? "";
    public string Average => _card?.Average ?? "";
    public string LastSync => _card?.LastSync ?? "";
    public bool HasSourceHost => !string.IsNullOrEmpty(SourceHost);
    public bool HasStored => !string.IsNullOrEmpty(Stored);
    public bool HasToday => !string.IsNullOrEmpty(Today);

    /// <summary>The "Pause" / "Resume" button's own word.</summary>
    public string PauseText => _snapshot?.Entry.PausedByUser == true ? "Resume" : "Pause";

    /// <summary>Today's panel, while the account is active and its copy is running; null otherwise (the state line says why).</summary>
    public AccountPanelViewModel? Panel
    {
        get => _panel;
        private set
        {
            var old = _panel;
            if (!Set(ref _panel, value)) return;
            if (old is not null) old.PropertyChanged -= OnPanelChanged;
            if (value is not null) value.PropertyChanged += OnPanelChanged;
            Raise(nameof(HasPanel));
            RaiseAnyBusy();
        }
    }

    public bool HasPanel => _panel is not null;

    /// <summary>True when the owner of the source removed this device: the copy stays until the person removes the account, and "Try again" asks the source once more.</summary>
    public bool CanReconnect { get => _canReconnect; private set { if (Set(ref _canReconnect, value)) ((RelayCommand)ReconnectCommand).RaiseCanExecuteChanged(); } }

    /// <summary>True while the account waits for the owner's decision on an invitation (the state line carries the code to read to him).</summary>
    public bool AwaitingOwner { get => _awaitingOwner; private set => Set(ref _awaitingOwner, value); }

    public bool CanRepairUsage => _card?.CanRepairUsage == true;

    // ---- limits ------------------------------------------------------------------------------------------------------------------------------

    public string StoredText { get => _storedText; set { if (Set(ref _storedText, value)) MarkLimitsDirty(); } }
    public string TrafficText { get => _trafficText; set { if (Set(ref _trafficText, value)) MarkLimitsDirty(); } }
    public string RequestsText { get => _requestsText; set { if (Set(ref _requestsText, value)) MarkLimitsDirty(); } }
    public bool UnmeteredOnly { get => _unmeteredOnly; set { if (Set(ref _unmeteredOnly, value)) MarkLimitsDirty(); } }
    public bool PauseOnPowerSaving { get => _pauseOnPowerSaving; set { if (Set(ref _pauseOnPowerSaving, value)) MarkLimitsDirty(); } }

    /// <summary>The sentences that say what to change (from <see cref="BlindLimitsForm.Validate"/> or the controller), one per line; null when the form is fine.</summary>
    public string? LimitsProblems { get => _limitsProblems; private set { if (Set(ref _limitsProblems, value)) Raise(nameof(HasLimitsProblems)); } }

    public bool HasLimitsProblems => !string.IsNullOrEmpty(_limitsProblems);

    public string RenameText { get => _renameText; set { if (Set(ref _renameText, value) && !_applying) _renameDirty = true; } }

    /// <summary>What the last action of this account did (a refusal included), in the sentence of the controller.</summary>
    public string? Notice { get => _notice; private set { if (Set(ref _notice, value)) Raise(nameof(HasNotice)); } }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    // ---- removal -----------------------------------------------------------------------------------------------------------------------------

    public bool RemovePanelOpen { get => _removePanelOpen; private set { if (Set(ref _removePanelOpen, value)) RaiseRemoveState(); } }
    public string RemoveTitle { get => _removeTitle; private set => Set(ref _removeTitle, value); }
    public string RemoveMessage { get => _removeMessage; private set => Set(ref _removeMessage, value); }
    public string RemoveConfirmText { get => _removeConfirmText; private set => Set(ref _removeConfirmText, value); }
    public string RemoveCancelText { get => _removeCancelText; private set => Set(ref _removeCancelText, value); }

    /// <summary>A slow operation of this account (the removal: its work is stopped, the source is told, the keys and files go) is running; the window shows a pop-up.</summary>
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { RaiseAllCommands(); RaiseAnyBusy(); } } }

    public string BusyTitle { get => _busyTitle; private set { if (Set(ref _busyTitle, value)) RaiseAnyBusy(); } }
    public string BusyText { get => _busyText; private set { if (Set(ref _busyText, value)) RaiseAnyBusy(); } }

    /// <summary>This account's own slow operation, or one of its panel's (a job the person asked for, the save of a backup): the window's pop-up follows it.</summary>
    public bool AnyBusy => _busy || _panel?.IsBusy == true;

    public string AnyBusyTitle => _busy ? _busyTitle : _panel?.BusyTitle ?? "";

    public string AnyBusyText => _busy ? _busyText : _panel?.BusyText ?? "";

    private void RaiseAnyBusy()
    {
        Raise(nameof(AnyBusy));
        Raise(nameof(AnyBusyTitle));
        Raise(nameof(AnyBusyText));
    }

    private void OnPanelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountPanelViewModel.IsBusy) or nameof(AccountPanelViewModel.BusyTitle) or nameof(AccountPanelViewModel.BusyText)) RaiseAnyBusy();
    }

    // ---- refresh -----------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Shows the account's current snapshot: the card, the form (unless the person is typing), the confirmation text, and today's panel (made when the account is active and its
    /// copy is up, dropped when it is not, made again when the account's copy was rebuilt). Runs on the UI thread.
    /// </summary>
    public void Apply(BlindAccountSnapshot snapshot)
    {
        if (_disposed) return;
        _snapshot = snapshot;
        _applying = true;
        try
        {
            _card = BlindAccountCard.Build(snapshot, _zone, _time.GetUtcNow());
            foreach (var name in new[] { nameof(Card), nameof(Name), nameof(SourceHost), nameof(StateLine), nameof(StateKind), nameof(Stored), nameof(Today), nameof(Average),
                nameof(LastSync), nameof(HasSourceHost), nameof(HasStored), nameof(HasToday), nameof(PauseText), nameof(CanRepairUsage) })
                Raise(name);

            var entry = snapshot.Entry;
            CanReconnect = entry.Phase == BlindAccountPhases.RemovedByOwner;
            AwaitingOwner = entry.Phase == BlindAccountPhases.AwaitingConfirmation;

            if (!_limitsDirty)
            {
                var form = BlindLimitsForm.From(entry);
                StoredText = form.Stored;
                TrafficText = form.TrafficPerDay;
                RequestsText = form.RequestsPerDay;
                UnmeteredOnly = form.UnmeteredOnly;
                PauseOnPowerSaving = form.PauseOnPowerSaving;
            }
            if (!_renameDirty) RenameText = entry.Name;

            var confirmation = BlindRemoveConfirmation.Build(snapshot);
            RemoveTitle = confirmation.Title;
            RemoveMessage = confirmation.Message;
            RemoveConfirmText = confirmation.ConfirmText;
            RemoveCancelText = confirmation.CancelText;

            ApplyPanel(snapshot);
            RaiseAllCommands();
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>Reads the panel's status again (off the UI thread), if there is a panel.</summary>
    public async Task RefreshPanelAsync()
    {
        if (_panel is { } panel) await panel.RefreshAsync();
    }

    private void ApplyPanel(BlindAccountSnapshot snapshot)
    {
        var wanted = snapshot.Entry.Phase == BlindAccountPhases.Active && snapshot.Status is not null;
        IBlindAppController? controller = null;
        if (wanted)
        {
            try { controller = _accounts.For(Id); }
            catch (KeyNotFoundException) { wanted = false; }
        }

        if (!wanted)
        {
            DropPanel();
            return;
        }
        // The copy was rebuilt (a wipe of this account makes a new runtime): a panel over the old controller would show a copy that is gone.
        if (_panel is not null && !ReferenceEquals(controller, _controller)) DropPanel();
        if (_panel is not null) return;

        _controller = controller;
        var panel = new AccountPanelViewModel(controller!, _work, _qr, _post, _problem,
            _store is null ? null : ct => _store(Id, ct), _time);
        panel.CopyRequested += text => CopyRequested?.Invoke(text);
        Panel = panel;
        _ = panel.RefreshAsync();
    }

    private void DropPanel()
    {
        if (_panel is null) return;
        var old = _panel;
        _controller = null;
        Panel = null;
        old.Dispose();
    }

    // ---- commands ----------------------------------------------------------------------------------------------------------------------------

    private void MarkLimitsDirty()
    {
        if (!_applying) _limitsDirty = true;
    }

    private void ApplyLimits()
    {
        var form = new BlindLimitsForm(StoredText, TrafficText, RequestsText, UnmeteredOnly, PauseOnPowerSaving);
        var result = form.Validate();
        if (!result.Ok)
        {
            LimitsProblems = string.Join("\n", result.Problems);
            return;
        }
        var refusal = _accounts.SetLimits(Id, result.Limits!) ?? _accounts.SetSwitches(Id, UnmeteredOnly, PauseOnPowerSaving);
        if (refusal is not null)
        {
            LimitsProblems = refusal;
            return;
        }
        LimitsProblems = null;
        _limitsDirty = false;
        Notice = "Limits saved.";
        Reapply();
    }

    private void TogglePause()
    {
        var pause = _snapshot?.Entry.PausedByUser != true;
        var refusal = _accounts.SetPaused(Id, pause);
        Notice = refusal ?? (pause ? "Paused. This account does no sync work until you resume it." : "Resumed.");
        Reapply();
    }

    private void Rename()
    {
        var refusal = _accounts.Rename(Id, RenameText);
        Notice = refusal ?? "Renamed.";
        if (refusal is null) _renameDirty = false;
        Reapply();
    }

    private void Reconnect()
    {
        var refusal = _accounts.Reconnect(Id);
        Notice = refusal ?? "Trying again: the next round decides.";
        Reapply();
    }

    private void RepairUsage()
    {
        var refusal = _accounts.StartNewUsageLedger(Id);
        Notice = refusal ?? "New counts started. The damaged file is kept aside.";
        Reapply();
    }

    private void OpenRemove()
    {
        // The text is built from the snapshot of the moment: what is held, and whether the source is told.
        if (_snapshot is { } snapshot)
        {
            var confirmation = BlindRemoveConfirmation.Build(snapshot);
            RemoveTitle = confirmation.Title;
            RemoveMessage = confirmation.Message;
            RemoveConfirmText = confirmation.ConfirmText;
            RemoveCancelText = confirmation.CancelText;
        }
        RemovePanelOpen = true;
    }

    private async Task RemoveAsync()
    {
        if (_busy) return;
        BeginBusy("Removing the account", "Please wait. Its work is stopped, the source is told if this device is online, then its keys and its data are deleted.");
        try
        {
            await Task.Run(() => _accounts.RemoveAsync(Id));
            RemovePanelOpen = false;
            DropPanel();
            Removed?.Invoke();
        }
        catch (AggregateException ex)
        {
            // Not half: the account stays "deleting" and the next try (or the next start) finishes it.
            Notice = FailureNotice.Say("The account was not removed completely", ex) + " Nothing is left half done: press Remove again to finish.";
            _problem?.Invoke("Removing an account did not complete", ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Notice = FailureNotice.Say("Could not remove the account", ex);
            _problem?.Invoke("Removing an account failed", ex);
        }
        finally
        {
            EndBusy();
        }
    }

    private void BeginBusy(string title, string text)
    {
        BusyTitle = title;
        BusyText = text;
        IsBusy = true;
    }

    private void EndBusy() => IsBusy = false;

    /// <summary>After a change: the card and the form are built again from the controller's new snapshot, on the spot.</summary>
    private void Reapply()
    {
        try
        {
            var snapshot = _accounts.GetSnapshot().Accounts.FirstOrDefault(a => a.Entry.Id == Id);
            if (snapshot is not null) Apply(snapshot);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CommandFailed(ex);
        }
    }

    private void RaiseRemoveState()
    {
        ((RelayCommand)ConfirmRemoveCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CancelRemoveCommand).RaiseCanExecuteChanged();
    }

    private void RaiseAllCommands()
    {
        foreach (var command in new[] { ApplyLimitsCommand, TogglePauseCommand, RenameCommand, ReconnectCommand, RepairUsageCommand, RemoveCommand, CancelRemoveCommand, ConfirmRemoveCommand })
            ((RelayCommand)command).RaiseCanExecuteChanged();
    }

    private void CommandFailed(Exception ex)
    {
        Notice = FailureNotice.Say("That did not work", ex);
        _problem?.Invoke("A command failed", ex);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DropPanel();
    }
}
