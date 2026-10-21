using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.ViewModels;

/// <summary>One account in the list on the left: the fields of its <see cref="BlindAccountCard"/>, which the card builds and this row only shows.</summary>
public sealed class AccountRow : ObservableObject
{
    private string _name = "";
    private string _sourceHost = "";
    private string _state = "";
    private BlindCardKind _kind;
    private string _stored = "";

    public AccountRow(string id) => Id = id;

    public string Id { get; }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string SourceHost { get => _sourceHost; private set => Set(ref _sourceHost, value); }

    /// <summary>The one state line: "Synced 4 min ago", "Paused: daily traffic limit reached (200 MB). Resumes at 00:00." ...</summary>
    public string State { get => _state; private set => Set(ref _state, value); }

    /// <summary>For a colour or an icon; never the words.</summary>
    public BlindCardKind Kind { get => _kind; private set => Set(ref _kind, value); }

    public string Stored { get => _stored; private set => Set(ref _stored, value); }

    internal void Update(BlindAccountCard card)
    {
        Name = card.Name;
        SourceHost = card.SourceHost;
        State = card.State;
        Kind = card.Kind;
        Stored = card.Stored;
    }
}

/// <summary>One thing the list of accounts and the disk disagree on, with the one explicit action that settles it (never taken on its own).</summary>
public sealed class RepairRow(string description, string actionText, ICommand command)
{
    public string Description { get; } = description;
    public string ActionText { get; } = actionText;
    public ICommand Command { get; } = command;
}

/// <summary>
/// What the window shows of the DEVICE: the list of accounts on the left (cards from <see cref="BlindAccountsView"/>), the account that is selected on the right
/// (<see cref="AccountDetailViewModel"/>: today's panel plus Limits), "Add account" with its paste box (<see cref="AddAccountViewModel"/>), the device's own limits, the repair
/// states with their explicit actions, and the autostart choice. It is a thin adapter: the sentences are the view records' (<see cref="BlindAccountsView"/>,
/// <see cref="BlindAccountCard"/>, <see cref="BlindInvitationPreview"/>, <see cref="BlindLimitsForm"/>, <see cref="BlindRemoveConfirmation"/>), every action is a call on
/// <see cref="IBlindAccountsController"/> or on the device loop's requests, and nothing here decides a rule.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IBlindAccountsController _accounts;
    private readonly Func<string, IBlindWorkRequests> _workFor;
    private readonly IBlindAutostart _autostart;
    private readonly Func<string, byte[]> _qr;
    private readonly Action<Action> _post;
    private readonly Action<string, Exception>? _problem;
    private readonly Func<string, CancellationToken, Task<BlindStoreInfo?>>? _store;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly BlindInvitationWatch? _watch;
    private int _refreshPending;
    private readonly object _readGate = new();
    private readonly object _applyGate = new();
    private long _readSeq;
    private long _shownSeq;
    private bool _disposed;
    private readonly object _watchGate = new();
    private bool _windowHidden = true;
    private bool _applying;

    private string? _selectedId;
    private AccountRow? _selectedRow;
    private AccountDetailViewModel? _detail;
    private bool _addOpen;
    private string _deviceLine = "";
    private bool _canAdd;
    private string _addHint = "";
    private string? _registryProblem;
    private string? _deviceUsageProblem;
    private IReadOnlyList<RepairRow> _repairs = [];
    private string _repairKey = "";
    private string _deviceStoredText = "";
    private string _deviceTrafficText = "";
    private string? _deviceLimitsProblems;
    private bool _deviceLimitsDirty;
    private string? _notice;
    private bool _autostartOn;
    private bool _autostartKnown = true;

    /// <param name="workFor">The work requests of one account (<c>BlindDesktopRuntime.WorkFor</c>): "Sync now", "Back up now", the first load.</param>
    /// <param name="qr">Makes the PNG of a pairing code; in the app it is the QR library, in tests anything.</param>
    /// <param name="post">Runs an action on the UI thread; the default runs it at once (tests).</param>
    /// <param name="problem">Where a failed command or refresh is written besides the notice on the screen (the error log); tests pass nothing.</param>
    /// <param name="statusArea">What the platform calls the place of the app's icon in the window's own words: "tray" or "menu bar".</param>
    /// <param name="store">What an account's copy holds, asked now and then off the UI thread for the store line of its panel.</param>
    /// <param name="watch">Asks about invitations that wait for their owner every 15 seconds while the window is open; none in a test that has no hub.</param>
    public MainViewModel(IBlindAccountsController accounts, Func<string, IBlindWorkRequests> workFor, IBlindAutostart autostart,
        Func<string, byte[]> qr, Action<Action>? post = null, Action<string, Exception>? problem = null, string statusArea = "tray",
        Func<string, CancellationToken, Task<BlindStoreInfo?>>? store = null, TimeProvider? time = null, TimeZoneInfo? zone = null,
        BlindInvitationWatch? watch = null)
    {
        _accounts = accounts;
        _workFor = workFor;
        _autostart = autostart;
        _qr = qr;
        _post = post ?? (action => action());
        _problem = problem;
        _store = store;
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        _watch = watch;
        StatusArea = statusArea;

        Add = new AddAccountViewModel(accounts, _time, problem);
        Add.Added += OnAdded;
        Add.PropertyChanged += OnChildChanged;

        Action<Exception> failed = CommandFailed;
        // busy-exempt: only opens the Add page, nothing is sent and nothing waits
        AddAccountCommand = new RelayCommand(OpenAdd, () => _canAdd, failed);
        // busy-exempt: only closes the Add page
        CloseAddCommand = new RelayCommand(() => { AddOpen = false; Add.Clear(); }, onError: failed);
        RepairDeviceUsageCommand = new RelayCommand(RepairDeviceUsage, () => _deviceUsageProblem is not null && !IsBusy, failed);
        ApplyDeviceLimitsCommand = new RelayCommand(ApplyDeviceLimits, () => _registryProblem is null && !IsBusy, failed);
        // busy-exempt: leaving the app is the person's own way out and starts nothing
        QuitCommand = new RelayCommand(() => QuitRequested?.Invoke(), onError: failed);

        _accounts.Changed += RequestRefresh;
    }

    /// <summary>"tray" on Windows, "menu bar" on macOS.</summary>
    public string StatusArea { get; }

    /// <summary>The autostart toggle's own words.</summary>
    public string AutostartText => $"Start in the {StatusArea} when I sign in";

    /// <summary>What closing the window does.</summary>
    public string CloseHintText => $"Closing this window keeps the app running in the {StatusArea}.";

    /// <summary>Raised by the Quit button; the shell ends the app (closing the window only hides it).</summary>
    public event Action? QuitRequested;

    /// <summary>Raised when the person asks to copy an account's pairing code; the window puts it on the clipboard.</summary>
    public event Action<string>? CopyRequested;

    public ICommand AddAccountCommand { get; }
    public ICommand CloseAddCommand { get; }
    public ICommand RepairDeviceUsageCommand { get; }
    public ICommand ApplyDeviceLimitsCommand { get; }
    public ICommand QuitCommand { get; }

    /// <summary>The "Add account" page; its paste box is where an invitation goes.</summary>
    public AddAccountViewModel Add { get; }

    public ObservableCollection<AccountRow> Rows { get; } = [];

    /// <summary>The account selected in the list; setting it shows that account on the right.</summary>
    public AccountRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (_applying || value is null || value.Id == _selectedId) return;
            AddOpen = false;
            Select(value.Id);
        }
    }

    /// <summary>The selected account; null when there is none.</summary>
    public AccountDetailViewModel? Detail { get => _detail; private set { if (Set(ref _detail, value)) RaisePages(); } }

    /// <summary>The person pressed "Add account" (or there is nothing else to show).</summary>
    public bool AddOpen { get => _addOpen; private set { if (Set(ref _addOpen, value)) RaisePages(); } }

    /// <summary>The right-hand side is the Add page: the person asked for it, or there is no account to show.</summary>
    public bool ShowAdd => _registryProblem is null && (_addOpen || _detail is null);

    public bool ShowDetail => _registryProblem is null && !ShowAdd && _detail is not null;

    /// <summary>"Back" on the Add page is only offered when there is an account to go back to.</summary>
    public bool CanCloseAdd => _detail is not null;

    /// <summary>"3 accounts - 3.1 GB stored - today 41 MB".</summary>
    public string DeviceLine { get => _deviceLine; private set => Set(ref _deviceLine, value); }

    public bool CanAdd { get => _canAdd; private set { if (Set(ref _canAdd, value)) ((RelayCommand)AddAccountCommand).RaiseCanExecuteChanged(); } }

    /// <summary>Why "Add account" is off, or empty.</summary>
    public string AddHint { get => _addHint; private set { if (Set(ref _addHint, value)) Raise(nameof(HasAddHint)); } }

    public bool HasAddHint => !string.IsNullOrEmpty(_addHint);

    /// <summary>The sentence the app opens on when the list of accounts is damaged: nothing is touched then.</summary>
    public string? RegistryProblem
    {
        get => _registryProblem;
        private set
        {
            if (!Set(ref _registryProblem, value)) return;
            Raise(nameof(HasRegistryProblem));
            RaisePages();
        }
    }

    public bool HasRegistryProblem => _registryProblem is not null;

    /// <summary>What the list of accounts and the disk disagree on, each with the action that settles it.</summary>
    public IReadOnlyList<RepairRow> Repairs { get => _repairs; private set { if (Set(ref _repairs, value)) Raise(nameof(HasRepairs)); } }

    public bool HasRepairs => _repairs.Count > 0;

    /// <summary>Set when the device's own usage counts cannot be read or trusted.</summary>
    public string? DeviceUsageProblem
    {
        get => _deviceUsageProblem;
        private set
        {
            if (!Set(ref _deviceUsageProblem, value)) return;
            Raise(nameof(HasDeviceUsageProblem));
            ((RelayCommand)RepairDeviceUsageCommand).RaiseCanExecuteChanged();
        }
    }

    public bool HasDeviceUsageProblem => _deviceUsageProblem is not null;

    public string DeviceStoredText { get => _deviceStoredText; set { if (Set(ref _deviceStoredText, value) && !_applying) _deviceLimitsDirty = true; } }
    public string DeviceTrafficText { get => _deviceTrafficText; set { if (Set(ref _deviceTrafficText, value) && !_applying) _deviceLimitsDirty = true; } }

    /// <summary>The sentences that say what to change in the device limits, one per line; null when the form is fine.</summary>
    public string? DeviceLimitsProblems { get => _deviceLimitsProblems; private set { if (Set(ref _deviceLimitsProblems, value)) Raise(nameof(HasDeviceLimitsProblems)); } }

    public bool HasDeviceLimitsProblems => !string.IsNullOrEmpty(_deviceLimitsProblems);

    /// <summary>The line under the list: what the last device-wide action did.</summary>
    public string? Notice { get => _notice; private set { if (Set(ref _notice, value)) Raise(nameof(HasNotice)); } }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    /// <summary>The "start with the computer" choice; setting it writes the OS entry (and reports a failure in <see cref="Notice"/>).</summary>
    public bool AutostartOn
    {
        get => _autostartOn;
        set
        {
            if (_autostartOn == value) return;
            if (!_applying)
            {
                try { _autostart.SetEnabled(value); }
                catch (Exception ex)
                {
                    Notice = FailureNotice.Say("Could not change the start-up setting", ex);
                    _problem?.Invoke("Changing the start-up setting failed", ex);
                    Raise(); // the box goes back to what it was
                    return;
                }
            }
            Set(ref _autostartOn, value);
        }
    }

    public bool AutostartKnown { get => _autostartKnown; private set => Set(ref _autostartKnown, value); }

    // ---- the pop-up of a long operation (the add, the removal, a job the person asked for) --------------------------------------------------------

    /// <summary>A long operation is running (BMB-311): the window shows a "please wait" pop-up over everything, and no command can be started a second time until it ends.</summary>
    public bool IsBusy => Add.IsBusy || _detail?.AnyBusy == true;

    public string BusyTitle => Add.IsBusy ? "Adding the account" : _detail?.AnyBusyTitle ?? "";

    public string BusyText => Add.IsBusy ? "Please wait. The invitation is checked with the owner's computer; this can take a moment." : _detail?.AnyBusyText ?? "";

    // ---- window life -------------------------------------------------------------------------------------------------------------------------

    /// <summary>The window appeared: asks about waiting invitations every 15 seconds while it stays open, and reads the accounts at once.</summary>
    public void OnWindowShown()
    {
        if (_disposed) return;
        lock (_watchGate) _windowHidden = false;
        StartWatch();
        _ = RefreshAsync();
    }

    /// <summary>The window was hidden: the watch ends (the device loop asks at its own passes), and nothing that is still on its way (a read of the accounts, an add) starts it again.</summary>
    public void OnWindowHidden()
    {
        if (_disposed || _watch is null) return;
        lock (_watchGate)
        {
            _windowHidden = true;
            _ = _watch.StopAsync();
        }
    }

    /// <summary>
    /// The one place the watch is started. The window starts out hidden (an app launched into the tray has never shown it) and the watch runs only while it is shown, so that no ask of the hub is made on
    /// behalf of a screen nobody sees - the device loop asks at its own passes.
    /// </summary>
    private void StartWatch()
    {
        if (_watch is null) return;
        lock (_watchGate)
        {
            if (!_windowHidden) _watch.Start();
        }
    }

    // ---- refresh -----------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the accounts again (off the UI thread) and shows them. Reads are numbered in the order they are made, and a read is not shown over one that was made after it: refreshes overlap (the host
    /// raises a change for every step of an add or a removal), and the one that read first may be the one that comes back last - it would bring back an account that was removed since.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_disposed) return;
        BlindAccountsSnapshot snapshot;
        long read = 0;
        try
        {
            snapshot = await Task.Run(() =>
            {
                lock (_readGate)
                {
                    read = ++_readSeq;
                    return _accounts.GetSnapshot();
                }
            });
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            Notice = FailureNotice.Say("Could not read the accounts", ex);
            _problem?.Invoke("Reading the accounts failed", ex);
            return;
        }
        if (_disposed) return;
        try
        {
            lock (_applyGate)
            {
                if (read > _shownSeq)
                {
                    _shownSeq = read;
                    Apply(snapshot);
                }
            }
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            // Showing the accounts is not worth ending the app for: say so, and show the next read.
            Notice = FailureNotice.Say("Could not show the accounts", ex);
            _problem?.Invoke("Showing the accounts failed", ex);
        }
        if (_detail is { } detail && !_disposed) await detail.RefreshPanelAsync();
    }

    private void Apply(BlindAccountsSnapshot snapshot)
    {
        var view = BlindAccountsView.Build(snapshot, _zone, _time.GetUtcNow());
        _applying = true;
        try
        {
            DeviceLine = view.DeviceLine;
            CanAdd = view.CanAdd;
            AddHint = view.AddHint;
            RegistryProblem = view.Problem;
            DeviceUsageProblem = view.DeviceUsageProblem;
            ApplyRepairs(view.Repairs ?? []);

            // The list: the same row objects for the same account (a selection must survive a refresh), in the order of the cards.
            var byId = Rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
            var ordered = new List<AccountRow>();
            foreach (var card in view.Cards)
            {
                if (!byId.Remove(card.Id, out var row)) row = new AccountRow(card.Id);
                row.Update(card);
                ordered.Add(row);
            }
            if (!Rows.SequenceEqual(ordered))
            {
                Rows.Clear();
                foreach (var row in ordered) Rows.Add(row);
            }

            if (!_deviceLimitsDirty)
            {
                var form = BlindDeviceLimitsForm.From(snapshot.Device);
                DeviceStoredText = form.Stored;
                DeviceTrafficText = form.TrafficPerDay;
            }

            SettleSelection(snapshot);
            ReadAutostart();
        }
        finally
        {
            _applying = false;
        }
        RaisePages();
        StartWatchIfSomethingWaits(snapshot);
    }

    /// <summary>Keeps the selected account when it is still there, otherwise the first one; builds the detail for it and shows its snapshot.</summary>
    private void SettleSelection(BlindAccountsSnapshot snapshot)
    {
        var ids = snapshot.Accounts.Select(a => a.Entry.Id).ToList();
        if (_selectedId is null || !ids.Contains(_selectedId)) _selectedId = ids.FirstOrDefault();
        SelectedRowSilently(Rows.FirstOrDefault(r => r.Id == _selectedId));

        if (_selectedId is null)
        {
            DropDetail();
            return;
        }
        if (_detail is null || _detail.Id != _selectedId) MakeDetail(_selectedId);
        _detail!.Apply(snapshot.Accounts.First(a => a.Entry.Id == _selectedId));
    }

    private void SelectedRowSilently(AccountRow? row)
    {
        if (ReferenceEquals(_selectedRow, row)) return;
        _selectedRow = row;
        Raise(nameof(SelectedRow));
    }

    private void Select(string id)
    {
        _selectedId = id;
        _ = RefreshAsync();
    }

    private void MakeDetail(string id)
    {
        DropDetail();
        var detail = new AccountDetailViewModel(_accounts, id, _workFor(id), _qr, _post, _problem, _store, _time, _zone);
        detail.Removed += OnRemoved;
        detail.CopyRequested += OnCopyRequested;
        detail.PropertyChanged += OnChildChanged;
        Detail = detail;
    }

    private void DropDetail()
    {
        if (_detail is not { } old) return;
        old.Removed -= OnRemoved;
        old.CopyRequested -= OnCopyRequested;
        old.PropertyChanged -= OnChildChanged;
        Detail = null;
        old.Dispose();
    }

    private void OnAdded(string id)
    {
        AddOpen = false;
        _selectedId = id;
        StartWatch();
        _ = RefreshAsync();
    }

    private void OnCopyRequested(string text) => CopyRequested?.Invoke(text);

    private void OnRemoved()
    {
        // The removed account is no longer in the list: the next read selects another (or the Add page, when it was the last).
        _selectedId = null;
        _ = RefreshAsync();
    }

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The busy state of the add page, or of the selected account (its own, or its panel's): the pop-up follows them.
        if (e.PropertyName is nameof(AddAccountViewModel.IsBusy) or nameof(AccountDetailViewModel.AnyBusy) or nameof(AccountDetailViewModel.AnyBusyTitle)
            or nameof(AccountDetailViewModel.AnyBusyText))
            RaiseBusy();
    }

    private void RaiseBusy()
    {
        Raise(nameof(IsBusy));
        Raise(nameof(BusyTitle));
        Raise(nameof(BusyText));
    }

    private void RaisePages()
    {
        Raise(nameof(ShowAdd));
        Raise(nameof(ShowDetail));
        Raise(nameof(CanCloseAdd));
        RaiseBusy();
    }

    private void StartWatchIfSomethingWaits(BlindAccountsSnapshot snapshot)
    {
        if (_watch is null || _watch.IsRunning) return;
        if (snapshot.Accounts.Any(a => a.Entry.Phase is BlindAccountPhases.Pending or BlindAccountPhases.AwaitingConfirmation)) StartWatch();
    }

    // ---- repairs -----------------------------------------------------------------------------------------------------------------------------

    private void ApplyRepairs(IReadOnlyList<BlindRepairIssue> issues)
    {
        var key = string.Join("\n", issues.Select(i => i.Kind + "|" + i.AccountId + "|" + i.Description));
        if (key == _repairKey) return;
        _repairKey = key;
        var rows = new List<RepairRow>();
        foreach (var issue in issues)
        {
            var captured = issue;
            if (issue.CanAdopt)
                rows.Add(new RepairRow(issue.Description, "Add it to the list", new RelayCommand(() => _ = RepairAsync(captured), () => !IsBusy, CommandFailed)));
            else if (issue.CanForget)
                rows.Add(new RepairRow(issue.Description, "Forget it", new RelayCommand(() => _ = RepairAsync(captured), () => !IsBusy, CommandFailed)));
            else
                // busy-exempt: no action at all, a link needs a person
                rows.Add(new RepairRow(issue.Description, "", new RelayCommand(() => { }, () => false)));
        }
        Repairs = rows;
    }

    private async Task RepairAsync(BlindRepairIssue issue)
    {
        try
        {
            string? refusal = issue.Kind switch
            {
                BlindRepairKinds.LegacyRootNotListed => await _accounts.AdoptLegacyRootAsync(),
                BlindRepairKinds.FolderNotListed => await _accounts.AdoptFolderAsync(issue.AccountId ?? ""),
                BlindRepairKinds.FolderMissing => await _accounts.ForgetMissingAccountAsync(issue.AccountId ?? ""),
                _ => "Nothing the app can do settles this one: it needs a person.",
            };
            Notice = refusal ?? "Done.";
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            CommandFailed(ex);
        }
        await RefreshAsync();
    }

    // ---- device-wide commands ----------------------------------------------------------------------------------------------------------------

    private void OpenAdd()
    {
        Add.Clear();
        AddOpen = true;
    }

    private void RepairDeviceUsage()
    {
        Notice = _accounts.StartNewDeviceUsageLedger() ?? "New counts started. The damaged file is kept aside.";
        _ = RefreshAsync();
    }

    private void ApplyDeviceLimits()
    {
        var (limits, problems) = new BlindDeviceLimitsForm(DeviceStoredText, DeviceTrafficText).Validate();
        if (limits is null)
        {
            DeviceLimitsProblems = string.Join("\n", problems);
            return;
        }
        var refusal = _accounts.SetDeviceLimits(limits);
        if (refusal is not null)
        {
            DeviceLimitsProblems = refusal;
            return;
        }
        DeviceLimitsProblems = null;
        _deviceLimitsDirty = false;
        Notice = "Device limits saved.";
        _ = RefreshAsync();
    }

    private void ReadAutostart()
    {
        bool? autostart;
        try { autostart = _autostart.IsEnabled; }
        catch (Exception) { autostart = null; }
        AutostartKnown = autostart is not null;
        AutostartOn = autostart == true;
    }

    private void CommandFailed(Exception ex)
    {
        Notice = FailureNotice.Say("That did not work", ex);
        _problem?.Invoke("A command failed", ex);
    }

    private void RequestRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshPending, 1) == 1) return;
        _post(() =>
        {
            Interlocked.Exchange(ref _refreshPending, 0);
            _ = RefreshAsync();
        });
    }

    private static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or AccessViolationException or StackOverflowException or ThreadAbortException;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accounts.Changed -= RequestRefresh;
        Add.Added -= OnAdded;
        Add.PropertyChanged -= OnChildChanged;
        DropDetail();
    }
}
