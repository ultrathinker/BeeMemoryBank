using System.Runtime.ExceptionServices;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// A device that holds SEVERAL blind accounts, one per source mesh. Each account is a complete, isolated copy of today's single-mesh app: its own folder,
/// database, blind NodeId and key, backup key, pairing secret, state, log, backups and pinned HTTP client, built by calling <see cref="BlindMobileServices.AddBlindAppCore"/>
/// once per account over its own <see cref="IServiceProvider"/>. Nothing is shared between accounts but the registry (<c>accounts.json</c>), the device's
/// one lock for a long job (<see cref="BlindDeviceGate"/>) and the host's clock.
///
/// <para><b>Upgrade moves nothing.</b> A 2.6 install's root folder IS account 1 (id <c>legacy</c>, layout <c>root</c>), under the names, keys and aliases it always had;
/// new accounts live in <c>accounts/&lt;random id&gt;/</c>. Adopting it writes one file, <c>accounts.json</c>, in one atomic rename, so a crash at any point leaves
/// either no registry (the adoption runs again) or the whole one.</para>
///
/// <para><b>One owner of the root.</b> A host holds the OS lock on the data root (<see cref="BlindRootLock"/>) for as long as it lives. A second host (another process, or
/// another object in this one) is refused with a sentence and never composes an account the first one runs.</para>
///
/// <para><b>The root is given back only when nothing can still use it.</b> Every start, add, repair, removal and rebuild after a wipe is an operation the host counts, and a runtime's own
/// work (a sync round, a long job, the opening of its database) is counted by the runtime. <see cref="DisposeAsync"/> waits for all of it, bounded; what ends in time is closed in the order a
/// removal closes it, and the root is given back. What does NOT end in time (a thread cannot be killed) is left alone: its services are not disposed beneath it and the root lease and the lock
/// file are KEPT, so that no second host can take over a folder an old operation is still writing in. The host then says so (<see cref="ShutdownIncomplete"/>, and the sentence every
/// call answers with), and completes the shutdown by itself, releasing the root, when the last of that work has really ended. The process is about to end anyway.</para>
///
/// <para><b>Fail closed.</b> A damaged registry opens the app on a sentence and touches no account (it is never "started empty over" the folders it names). So does a root
/// or an <c>accounts</c> folder that is a link. A registry that does not agree with the disk (a 2.6 copy it omits, a folder it does not name) is a repair state: nothing starts,
/// each difference is listed, and only an explicit call (<see cref="AdoptLegacyRootAsync"/>, <see cref="AdoptFolderAsync"/>, <see cref="ForgetMissingAccountAsync"/>) settles it.
/// A listed account that was set up and has lost its folder, or whose folder is a link, is not started and never made again from nothing. An account that cannot start does
/// not stop the others. Removal marks the account <c>deleting</c> first and finishes at the next start if the process dies half way.</para>
/// </summary>
public sealed partial class BlindAccountHost : IBlindAccountsController, IAsyncDisposable
{
    public const int DefaultMaxAccounts = 8;
    private const string LegacyDefaultName = "My copy";
    private const string RecoveredDefaultName = "Recovered copy";
    private const string NeedsRepair = "Needs repair: ";

    private readonly BlindAccountHostOptions _options;
    private readonly BlindAccountRegistry _registry;
    private readonly BlindDeviceGate _gate;
    private readonly BlindDeviceUsage _deviceUsage;
    private readonly IBlindCrashPoints _crash;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, BlindAccountRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _problems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _removals = new(StringComparer.Ordinal);
    private List<BlindRepairIssue> _repairs = [];
    private BlindRootLock? _rootLock;
    private bool _started;
    private bool _disposed;
    private int _activeOps;
    private TaskCompletionSource _opsIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<BlindAccountRuntime> _retained = [];
    private volatile string? _shutdownFault;
    private string? _startProblem;
    private string? _repairBlock;

    public BlindAccountHost(BlindAccountHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxAccounts is < 1 or > BlindAccountRegistry.MaxAccountsInFile)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAccounts must be between 1 and " + BlindAccountRegistry.MaxAccountsInFile + ".");
        _options = options;
        _crash = options.CrashPoints ?? NoCrashPoints.Instance;
        _registry = new BlindAccountRegistry(options.RootDirectory, _crash, options.RootLockWait);
        _gate = options.DeviceGate ?? new BlindDeviceGate();
        _gate.UseClock(options.Time);
        // The device's usage: every account's ledger and stored bytes count towards it; the limits are whatever the registry says now.
        _deviceUsage = new BlindDeviceUsage(options.Time, Path.Combine(_registry.RootDirectory, "device-usage.json"), () => _registry.Current.DeviceLimits);
    }

    /// <summary>True after a <see cref="DisposeAsync"/> that returned with work still running: the root is still held, and the shutdown finishes by itself when that work ends.</summary>
    public bool ShutdownIncomplete => _shutdownFault is not null;

    /// <summary>The sentence that says why the shutdown is not complete, or null.</summary>
    public string? ShutdownFault => _shutdownFault;

    /// <summary>What the whole device used (the host's screens and tests read it).</summary>
    public BlindDeviceUsage DeviceUsage => _deviceUsage;

    public event Action? Changed;

    public int MaxAccounts => _options.MaxAccounts;

    /// <summary>The registry (tests read what is on disk through it).</summary>
    internal BlindAccountRegistry Registry => _registry;

    // ---- start ------------------------------------------------------------------------------------------------------------------------------

    public async Task StartAsync(CancellationToken ct = default)
    {
        using var operation = BeginOperation();
        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            ct.ThrowIfCancellationRequested();

            // First of all: the root is ours or it is not. No file below it is read, and no account composed, by a host that does not hold it.
            if (_rootLock is null)
            {
                _startProblem = null;
                var lease = BlindRootLock.TryAcquire(_registry.RootDirectory, _options.RootLockWait, out var lockProblem);
                if (lease is null)
                {
                    // Not started: the person closes the other copy, and this call can be made again.
                    _startProblem = lockProblem ?? BlindRootLock.RefusedSentence;
                    RaiseChanged();
                    return;
                }
                _rootLock = lease;
                _registry.Hold(lease);
            }

            var state = _registry.Load();
            if (state == BlindRegistryState.Damaged)
            {
                // Touch nothing: not a runtime, not a folder, not a key. The screen shows the sentence.
                _started = true;
                RaiseChanged();
                return;
            }

            if (state == BlindRegistryState.Missing && !TryAdopt())
            {
                _started = true;
                return;
            }

            await ContinueStartAsync(ct).ConfigureAwait(false);

            _started = true;
            RaiseChanged();
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// The registry against the disk, then (when they agree) the work a crash cut off and every account's runtime. Run at the start and again after each repair action; an account that
    /// is already up is left alone.
    /// </summary>
    private async Task ContinueStartAsync(CancellationToken ct)
    {
        // Every listed account counts towards the device's storage from the first moment, with or without a runtime (see BlindDeviceUsage).
        SyncDeviceAccounts();

        // A repair state: nothing is finished and nothing is started until a person has settled it.
        if (!Reconcile()) return;

        // A removal that a crash cut off is finished before anything starts: its keys, state and files are going, not to be brought back up.
        foreach (var entry in _registry.Current.Entries.Where(e => e.Phase == BlindAccountPhases.Deleting).ToList())
        {
            try
            {
                // The source is still owed its goodbye: a bounded runtime is built over what the account still has (and nothing is made for it).
                var leaving = BuildRecoveryRuntime(entry);
                await FinishRemovalAsync(entry, leaving, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Remember(entry.Id, "Its removal could not be finished: " + FailureWords(ex)); }
        }

        foreach (var entry in _registry.Current.Entries.Where(e => e.Phase != BlindAccountPhases.Deleting).ToList())
            await StartRuntimeAsync(entry, ct).ConfigureAwait(false);
    }

    /// <summary>Writes the registry the first time: the 2.6 install as account 1 when there is one, otherwise an empty list. False (and a sentence) when the legacy state cannot be read.</summary>
    private bool TryAdopt()
    {
        BlindAccountEntry? legacy;
        try
        {
            legacy = BlindLegacyAdoption.Detect(_options.LegacyDatabasePath, _options.States, _options.Time);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing is written: an unreadable state is not "no install", and an empty registry written now would hide the real one.
            _startProblem = UnreadableStateSentence(ex);
            RaiseChanged();
            return false;
        }

        _crash.Reach("adopt:detected");
        _registry.Update(_ => new BlindAccountsFile(Accounts: legacy is null ? [] : [legacy]));
        _crash.Reach("adopt:done");
        return true;
    }

    private static string UnreadableStateSentence(Exception ex) =>
        "The state of this app could not be read (" + ex.GetType().Name + "). Nothing was changed; close the app and open it again.";

    // ---- reconciling the list with the disk -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Compares the list of accounts with what is on disk: a 2.6 copy in the root that the list omits, folders under <c>accounts/</c> that it does not name, and every listed
    /// account's folder (a link, a folder that is gone from an account that was set up). Records what it found (<see cref="BlindAccountsSnapshot.Repairs"/>) and returns false
    /// when something found stops EVERYTHING from starting: data the list does not name means the list itself cannot be trusted to be the whole truth. A problem of one listed
    /// account only stops that account.
    /// </summary>
    private bool Reconcile()
    {
        var file = _registry.Current;
        var global = new List<BlindRepairIssue>();
        var perAccount = new List<BlindRepairIssue>();
        string? unreadable = null;

        // 1. A 2.6 copy in the root that the list does not name.
        if (file.Find(BlindAccountEntry.LegacyId) is null)
        {
            try
            {
                if (BlindLegacyAdoption.Detect(_options.LegacyDatabasePath, _options.States, _options.Time) is not null)
                    global.Add(new BlindRepairIssue(BlindRepairKinds.LegacyRootNotListed, null,
                        "This app's folder holds a copy from an earlier version (account 1) that the list of accounts does not name. It was not started. Adopt it as an account to bring it back."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unreadable = UnreadableStateSentence(ex);
            }
        }

        // 2. Folders under accounts/ that the list does not name. Only names this app gives accounts count: it never starts anything else from there.
        var accounts = _registry.AccountsDirectory;
        if (Directory.Exists(accounts))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(accounts).OrderBy(p => p, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (name == BlindAccountEntry.LegacyId || !BlindAccountEntry.IsValidId(name) || file.Find(name) is not null) continue;
                if (BlindPathSafety.IsLink(path))
                    global.Add(new BlindRepairIssue(BlindRepairKinds.UnsafePath, name,
                        "A link named like an account is in the accounts folder and the list of accounts does not name it. It was not opened. Remove the link by hand."));
                else if (Directory.Exists(path))
                    global.Add(new BlindRepairIssue(BlindRepairKinds.FolderNotListed, name,
                        "A folder named like an account is in the accounts folder and the list of accounts does not name it. It was not started. Adopt it as an account, or move it away by hand."));
                else
                    // Anything else that holds the name (a regular file, say) is no account and cannot be adopted, but it blocks the name and the list does not know of it: said, not skipped.
                    global.Add(new BlindRepairIssue(BlindRepairKinds.FileInPlaceOfFolder, name,
                        "A file named like an account is in the accounts folder and the list of accounts does not name it. It was not opened. Move it away by hand."));
            }
        }

        // 3. Every listed account: is its folder what it should be.
        foreach (var entry in file.Entries.Where(e => e.Phase != BlindAccountPhases.Deleting))
            if (Preflight(entry) is { } issue) perAccount.Add(issue);

        lock (_lock)
        {
            foreach (var stale in _problems.Where(p => p.Value.StartsWith(NeedsRepair, StringComparison.Ordinal)).Select(p => p.Key).ToList()) _problems.Remove(stale);
            foreach (var issue in perAccount) _problems[issue.AccountId!] = NeedsRepair + issue.Description;
            _repairs = [.. global, .. perAccount];
            _repairBlock = global.Count == 0 ? null
                : "The list of accounts and the data on this device do not agree. Nothing was started or changed: each difference is listed, and each is settled by hand or by the repair named for it.";
        }
        if (unreadable is not null) _startProblem = unreadable;
        return global.Count == 0 && unreadable is null;
    }

    /// <summary>
    /// What is wrong with a listed account's folder before anything is created or composed for it: a link or a file in its place, or (for an account that was set up) a folder
    /// that is gone. Looks only; creates nothing, opens nothing, touches no key.
    /// </summary>
    private BlindRepairIssue? Preflight(BlindAccountEntry entry)
    {
        if (BlindPathSafety.AccountFolderProblem(_registry.RootDirectory, entry) is { } unsafePath)
            return new BlindRepairIssue(BlindRepairKinds.UnsafePath, entry.Id, unsafePath);
        // What the app opens by name inside the folder is not a link either (the shallow look; see BlindPathSafety.OwnedChildProblem).
        if (BlindPathSafety.OwnedChildProblem(BlindAccountNames.DataDirectory(_registry.RootDirectory, entry),
                BlindAccountNames.DatabasePath(_registry.RootDirectory, entry, _options.LegacyDatabasePath)) is { } inner)
            return new BlindRepairIssue(BlindRepairKinds.UnsafePath, entry.Id, inner);
        // A folder that is gone from an account that was set up is never made again over what the account holds. "Set up" is the marker in its entry OR any trace of an identity in its keys or
        // state (a marker that could not be written must never be the permission to start from nothing); what cannot be read counts as a trace.
        if (!entry.IsLegacy && !Directory.Exists(BlindAccountNames.DataDirectory(_registry.RootDirectory, entry)) && (entry.Established || HoldsIdentityTrace(entry)))
            return new BlindRepairIssue(BlindRepairKinds.FolderMissing, entry.Id,
                "The folder of this account is gone, although the account had been set up. Nothing was created and its keys were not touched. If you have a backup, put the folder back and open the app again; otherwise forget this account.");
        return null;
    }

    /// <summary>
    /// True when the account's secret or state store holds anything of an identity (its node id, its backup key, its pairing secret), or cannot be read to say: the account has been set up, or may
    /// have been, and its missing folder is a repair case. Reads nothing but the presence of these.
    /// </summary>
    private bool HoldsIdentityTrace(BlindAccountEntry entry)
    {
        try
        {
            if (new BlindPhoneState(_options.States.For(entry)).NodeId is not null) return true;
            var secrets = _options.Secrets.For(entry);
            var backupKey = secrets.LoadBackupKey();
            if (backupKey is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(backupKey);
                return true;
            }
            return secrets.LoadPairingSecret() is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return true; // it cannot be said that the account holds nothing
        }
    }

    // ---- repair -----------------------------------------------------------------------------------------------------------------------------

    public async Task<string?> AdoptLegacyRootAsync(CancellationToken ct = default)
    {
        using var operation = BeginOperation();
        if (RepairRefusal() is { } refusal) return refusal;
        if (!Pending(BlindRepairKinds.LegacyRootNotListed, null))
            return "There is nothing to adopt: this app's folder holds no copy that the list of accounts omits.";

        BlindAccountEntry? legacy;
        try { legacy = BlindLegacyAdoption.Detect(_options.LegacyDatabasePath, _options.States, _options.Time); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return UnreadableStateSentence(ex); }
        if (legacy is null) return "There is nothing to adopt: this app's folder holds no copy that the list of accounts omits.";

        try
        {
            // One atomic write; the copy itself is not moved, copied or re-keyed. Account 1 goes first in the list.
            _registry.Update(file => file.Find(BlindAccountEntry.LegacyId) is not null ? file : file with { Accounts = [legacy, .. file.Entries] });
        }
        catch (BlindRegistryDamagedException ex)
        {
            return ex.Message;
        }
        _crash.Reach("repair:adopted-legacy");
        await FinishRepairAsync(ct).ConfigureAwait(false);
        return null;
    }

    public async Task<string?> AdoptFolderAsync(string accountId, string? name = null, CancellationToken ct = default)
    {
        using var operation = BeginOperation();
        if (RepairRefusal() is { } refusal) return refusal;
        if (accountId == BlindAccountEntry.LegacyId || !BlindAccountEntry.IsValidId(accountId))
            return "That is not the folder of an account.";
        if (!Pending(BlindRepairKinds.FolderNotListed, accountId))
            return "That folder is not waiting to be adopted.";

        var chosen = string.IsNullOrWhiteSpace(name) ? RecoveredDefaultName : name.Trim();
        if (BlindAccountEntry.CheckName(chosen) is { } nameProblem) return nameProblem;

        try
        {
            string? problem = null;
            _registry.Update(file =>
            {
                if (file.Find(accountId) is not null) return file;
                if (file.Entries.Count(e => e.Phase != BlindAccountPhases.Deleting) >= _options.MaxAccounts)
                {
                    problem = $"This device holds at most {_options.MaxAccounts} accounts. Remove one first.";
                    return file;
                }
                var candidate = new BlindAccountEntry(accountId, BlindAccountLayouts.Dir, chosen, BlindAccountPhases.Active,
                    AddedAt: _options.Time.GetUtcNow(), Order: file.Entries.Select(e => e.Order).DefaultIfEmpty(0).Max() + 1);
                // What is adopted is looked at first: a link is never adopted, and a folder that holds something is an account that was set up (its loss is then a repair case).
                if (BlindPathSafety.AccountFolderProblem(_registry.RootDirectory, candidate) is { } unsafePath)
                {
                    problem = unsafePath;
                    return file;
                }
                var adopted = candidate with { Established = Directory.EnumerateFileSystemEntries(BlindAccountNames.DataDirectory(_registry.RootDirectory, candidate)).Any() };
                return file with { Accounts = [.. file.Entries, adopted] };
            });
            if (problem is not null) return problem;
        }
        catch (BlindRegistryDamagedException ex)
        {
            return ex.Message;
        }
        _crash.Reach("repair:adopted-folder");
        await FinishRepairAsync(ct).ConfigureAwait(false);
        return null;
    }

    public async Task<string?> ForgetMissingAccountAsync(string accountId, CancellationToken ct = default)
    {
        using var operation = BeginOperation();
        if (RepairRefusal() is { } refusal) return refusal;
        if (!Pending(BlindRepairKinds.FolderMissing, accountId))
            return "That account is not waiting to be forgotten: its folder is there, or it is not in the list.";
        var entry = _registry.Current.Find(accountId);
        if (entry is null || entry.IsLegacy) return "That account is not in the list.";

        // Looked at again at the moment of the act: a folder that came back (a restored backup) is not forgotten, and a link is not "gone".
        if (BlindPathSafety.AccountFolderProblem(_registry.RootDirectory, entry) is { } unsafePath) return unsafePath;
        if (Directory.Exists(BlindAccountNames.DataDirectory(_registry.RootDirectory, entry)))
            return "The folder of this account is there again; nothing was forgotten. Open the app again.";

        try
        {
            _registry.Update(file => file.Find(accountId) is null ? file : file with { Accounts = file.Entries.Where(e => e.Id != accountId).ToList() });
        }
        catch (BlindRegistryDamagedException ex)
        {
            return ex.Message;
        }
        _crash.Reach("repair:forgotten");
        lock (_lock) _problems.Remove(accountId);
        _deviceUsage.Unregister(accountId);

        // The entry is gone: what it kept in the secret and state stores goes with it (nothing else is touched). Each is tried on its own; a failure leaves litter, not a half account.
        var uncleared = new List<string>();
        try { _options.Secrets.For(entry).Clear(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { uncleared.Add("the keys (" + ex.GetType().Name + ")"); }
        try { _options.States.For(entry).Erase(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { uncleared.Add("the state (" + ex.GetType().Name + ")"); }
        var leftover = uncleared.Count == 0 ? null
            : "The account was forgotten, but " + string.Join(" and ", uncleared) + " it kept could not be cleared. They stay on this device until the app is reset.";

        await FinishRepairAsync(ct).ConfigureAwait(false);
        return leftover;
    }

    /// <summary>The sentence that refuses a repair action now (not started, the registry is damaged, another copy holds the folder), or null.</summary>
    private string? RepairRefusal()
    {
        if ((_registry.Problem ?? _startProblem) is { } blocked) return blocked;
        if (!_started) return "The app is still starting. Try again in a moment.";
        return null;
    }

    private bool Pending(string kind, string? accountId)
    {
        lock (_lock) return _repairs.Any(i => i.Kind == kind && i.AccountId == accountId);
    }

    private async Task FinishRepairAsync(CancellationToken ct)
    {
        using var operation = BeginOperation();
        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ContinueStartAsync(ct).ConfigureAwait(false); }
        finally { _startLock.Release(); }
        RaiseChanged();
    }

    // ---- reading ----------------------------------------------------------------------------------------------------------------------------

    public BlindAccountsSnapshot GetSnapshot()
    {
        var file = _registry.Current;
        var cards = new List<BlindAccountSnapshot>();
        foreach (var entry in file.Entries.OrderBy(e => e.Order).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            BlindAccountRuntime? runtime;
            string? problem;
            lock (_lock)
            {
                _runtimes.TryGetValue(entry.Id, out runtime);
                _problems.TryGetValue(entry.Id, out problem);
            }

            // A long job that did not stop and was let go of still holds the device's gate, whatever else the card could say: the person must know why the other accounts' long jobs wait.
            if (_gate.CurrentHolder is { Abandoned: true } held && held.OwnerId == entry.Id)
                problem ??= "Its long job did not stop when it was asked to (" + BlindAccountCard.Ago(_options.Time.GetUtcNow() - held.AbandonedSince!.Value) + "). It is let go of: the other accounts' long jobs wait for it to end, and a new one of this account starts after it.";

            BlindAppStatus? status = null;
            BlindAccountUsageView? usage = null;
            BlindPause? pause = null;
            if (runtime is { Closing: false })
            {
                try { status = runtime.Controller.GetStatus(); }
                catch (Exception ex) when (ex is not OperationCanceledException) { problem ??= "Its state could not be read: " + FailureWords(ex); }
                (usage, pause) = UsageOf(runtime);
            }
            cards.Add(new BlindAccountSnapshot(entry, status, problem, usage, pause, entry.Phase is BlindAccountPhases.Pending or BlindAccountPhases.AwaitingConfirmation ? InviteNoteOf(entry.Id) : null));
        }

        var counted = file.Entries.Count(e => e.Phase != BlindAccountPhases.Deleting);
        var registryProblem = Blocked();
        List<BlindRepairIssue> repairs;
        lock (_lock) repairs = [.. _repairs];
        // Not while the registry is damaged or the start is blocked: reading the device's usage saves its day, and nothing may be written then.
        var device = registryProblem is null && _started
            ? DeviceUsageView()
            : null;
        return new BlindAccountsSnapshot(cards, file.DeviceLimits, registryProblem,
            CanAdd: registryProblem is null && _started && counted < _options.MaxAccounts, _options.MaxAccounts, device, repairs);
    }

    private BlindDeviceUsageView DeviceUsageView()
    {
        var stored = _deviceUsage.StoredReading();
        return new BlindDeviceUsageView(stored.Known ? stored.Bytes : null, _deviceUsage.TrafficToday(), _deviceUsage.RequestsToday(), _deviceUsage.AccountCount, _deviceUsage.UnavailableReason);
    }

    public string? StartNewUsageLedger(string accountId)
    {
        var ledger = Runtime(accountId).Services.GetService<BlindUsageLedger>();
        if (ledger is null) return "This account keeps no usage counts.";
        var problem = ledger.StartNewLedger();
        if (problem is null) RaiseChanged();
        return problem;
    }

    public string? StartNewDeviceUsageLedger()
    {
        var problem = _deviceUsage.StartNewHighWater();
        if (problem is null) RaiseChanged();
        return problem;
    }

    /// <summary>What the account holds and used today, and the pause that holds it. Reading only; a failure to read is "unknown", never an exception on the screen.</summary>
    private static (BlindAccountUsageView? Usage, BlindPause? Pause) UsageOf(BlindAccountRuntime runtime)
    {
        try
        {
            var ledger = runtime.Services.GetService<BlindUsageLedger>();
            var stored = runtime.Services.GetService<BlindStoredBytes>();
            if (ledger is null || stored is null) return (null, null);
            var today = ledger.UsageToday();
            var reading = stored.Reading();
            var usage = new BlindAccountUsageView(reading.Known ? reading.Bytes : null, today.Requests, today.BytesIn, today.BytesOut, ledger.AverageTrafficPerDay(), ledger.UnavailableReason);
            return (usage, runtime.Services.GetService<BlindLimitGuard>()?.Check());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    public BlindPause? PauseOf(string accountId) => Runtime(accountId).Services.GetService<BlindLimitGuard>()?.Check();

    public void NoteJobAbandoned(string accountId, bool longJob)
    {
        if (!longJob) return; // a sync round holds no device-wide lock
        if (_gate.Abandon(accountId)) RaiseChanged();
    }

    public IBlindAppController For(string accountId) => Runtime(accountId).Controller;

    public IServiceProvider ServicesFor(string accountId) => Runtime(accountId).Services;

    private BlindAccountRuntime Runtime(string accountId)
    {
        lock (_lock)
        {
            if (_runtimes.TryGetValue(accountId, out var runtime) && !runtime.Closing) return runtime;
        }
        throw new KeyNotFoundException("There is no such account on this device (any more).");
    }

    /// <summary>The sentence that stops every change of the registry, or null: a damaged registry, another copy holding the folder, a repair state, or a first start that could not read the 2.6 state (writing a registry then would hide the install).</summary>
    private string? Blocked() => _registry.Problem ?? _startProblem ?? _repairBlock ?? _shutdownFault;

    // ---- add and answer ---------------------------------------------------------------------------------------------------------------------

    public async Task<BlindAddResult> AddAsync(string? name, CancellationToken ct = default)
    {
        using var operation = BeginOperation();
        if (Blocked() is { } blocked) return new BlindAddResult(null, blocked);
        if (!_started) return new BlindAddResult(null, "The app is still starting. Try again in a moment.");

        BlindAccountEntry entry;
        AccountWork work;
        lock (_lock)
        {
            var counted = _registry.Current.Entries.Count(e => e.Phase != BlindAccountPhases.Deleting);
            if (counted >= _options.MaxAccounts)
                return new BlindAddResult(null, $"This device holds at most {_options.MaxAccounts} accounts. Remove one first.");

            var chosen = string.IsNullOrWhiteSpace(name) ? $"Account {counted + 1}" : name.Trim();
            if (BlindAccountEntry.CheckName(chosen) is { } problem) return new BlindAddResult(null, problem);

            var order = _registry.Current.Entries.Select(e => e.Order).DefaultIfEmpty(0).Max() + 1;
            entry = new BlindAccountEntry(BlindAccountEntry.NewId(), BlindAccountLayouts.Dir, chosen, BlindAccountPhases.Active,
                AddedAt: _options.Time.GetUtcNow(), Order: order);
            // The entry first: a crash after this leaves an account with an empty folder, which the next start brings up and gives an identity (nothing half-made to repair).
            // It is not "established" until its folder exists and the record is written (StartRuntimeAsync, before any identity), so a folder that was never made is simply made then.
            _crash.Reach("add:before-registry");
            _registry.Update(file => file with { Accounts = [.. file.Entries, entry] });
            // From here a removal of the account may come: it waits for this add to end, and cancels the start under way.
            work = BeginAddWork(entry.Id);
        }
        try
        {
            _crash.Reach("add:registered");
            SyncDeviceAccounts();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, work.Token);
            bool started;
            try { started = await StartRuntimeAsync(entry, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                work.NoteCutShort();
                return new BlindAddResult(null, "The account was removed before it was set up.");
            }
            _crash.Reach("add:started");
            RaiseChanged();
            if (!started)
            {
                work.NoteCutShort();
                return new BlindAddResult(null, "The account was removed before it was set up.");
            }

            string? failed;
            lock (_lock) failed = _problems.GetValueOrDefault(entry.Id);
            return failed is null ? new BlindAddResult(entry.Id) : new BlindAddResult(entry.Id, "The account was added, but " + char.ToLowerInvariant(failed[0]) + failed[1..]);
        }
        finally
        {
            work.EndAdd();
        }
    }

    public string? AcceptCallCode(string text) => AcceptCallCodeWithAccount(text).Error;

    public BlindAcceptResult AcceptCallCodeWithAccount(string text)
    {
        if (!BlindCallCode.TryParse(text, out _))
            return new BlindAcceptResult(null, "This is not a connection code from the computer. Copy it again.");

        List<BlindAccountRuntime> awaiting;
        lock (_lock) awaiting = _runtimes.Values.Where(r => !r.Closing).ToList();
        awaiting = awaiting.Where(r => IsAwaitingAnswer(r.Controller)).ToList();
        if (awaiting.Count == 0)
            return new BlindAcceptResult(null, "No account on this device is waiting for a connection code. Add an account, or choose Re-pair on the one that should call another computer.");

        // The code names no account: it is the pairing secret that decides. Every awaiting account checks the code's MAC under its OWN secret; one that does not
        // recognise it is not touched, so a code made for account B can never pair account A.
        foreach (var runtime in awaiting)
            if (runtime.Controller.AcceptCallCode(text) is null)
            {
                ReactivateAfterPairing(runtime.Id);
                RaiseChanged();
                return new BlindAcceptResult(runtime.Id);
            }
        return new BlindAcceptResult(null, "This code was not made for any account on this device that is waiting for one. Use the code the computer shows for the phone code of the account you are adding.");
    }

    private static bool IsAwaitingAnswer(IBlindAppController controller)
    {
        try { return controller.GetStatus().AwaitingAnswer; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    // ---- editing ----------------------------------------------------------------------------------------------------------------------------

    public string? Rename(string accountId, string name) =>
        BlindAccountEntry.CheckName(name) is { } problem ? problem : Edit(accountId, e => e with { Name = name.Trim() });

    public string? SetLimits(string accountId, BlindAccountLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits is { MaxStoredBytes: < 0 } or { MaxTrafficBytesPerDay: < 0 } or { MaxRequestsPerDay: < 0 })
            return "A limit cannot be negative. Zero pauses the account; leave a limit empty for none.";
        return Edit(accountId, e => e with { Limits = limits.IsEmpty ? null : limits });
    }

    public string? SetSwitches(string accountId, bool unmeteredOnly, bool pauseOnPowerSaving) =>
        Edit(accountId, e => e with { UnmeteredOnly = unmeteredOnly, PauseOnPowerSaving = pauseOnPowerSaving });

    public string? SetPaused(string accountId, bool paused) => Edit(accountId, e => e with { PausedByUser = paused });

    public string? SetDeviceLimits(BlindDeviceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits is { MaxStoredBytes: < 0 } or { MaxTrafficBytesPerDay: < 0 })
            return "A limit cannot be negative. Zero pauses the device; leave a limit empty for none.";
        try
        {
            if (Blocked() is { } blocked) return blocked;
            _registry.Update(file => file with { Device = limits is { MaxStoredBytes: null, MaxTrafficBytesPerDay: null } ? null : limits });
        }
        catch (BlindRegistryDamagedException ex)
        {
            return ex.Message;
        }
        RaiseChanged();
        return null;
    }

    private string? Edit(string accountId, Func<BlindAccountEntry, BlindAccountEntry> change)
    {
        const string unknown = "There is no such account on this device (any more).";
        try
        {
            if (Blocked() is { } blocked) return blocked;
            if (_registry.Current.Find(accountId) is not { Phase: not BlindAccountPhases.Deleting }) return unknown;
            _registry.Update(file =>
            {
                var entry = file.Find(accountId);
                if (entry is null || entry.Phase == BlindAccountPhases.Deleting) return file;
                var edited = change(entry);
                return file with { Accounts = file.Entries.Select(e => e.Id == accountId ? edited : e).ToList() };
            });
        }
        catch (BlindRegistryDamagedException ex)
        {
            return ex.Message;
        }
        RaiseChanged();
        return null;
    }

    // ---- removal ----------------------------------------------------------------------------------------------------------------------------

    public Task RemoveAsync(string accountId, CancellationToken ct = default) => StartRemoval(accountId, joinRunning: true, ownAdd: false, ct)!;

    /// <summary>
    /// Starts the removal of an account, or joins the one that is running (<paramref name="joinRunning"/>; otherwise null is returned for it: the caller is a part of what that removal may be
    /// waiting for). One that has ended (it failed, say) is not joined: this call tries again. <paramref name="ownAdd"/> says the caller IS the add of this account (its own cleanup).
    /// </summary>
    private Task? StartRemoval(string accountId, bool joinRunning, bool ownAdd, CancellationToken ct)
    {
        Task task;
        var begin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_removals.TryGetValue(accountId, out var running) && !running.IsCompleted) return joinRunning ? running : null;
            var entry = _registry.Current.Find(accountId) ?? throw new KeyNotFoundException("There is no such account on this device (any more).");
            var operation = BeginOperation();
            task = RemoveTrackedAsync(entry, operation, ownAdd, begin.Task, ct);
            _removals[accountId] = task;
            // Runs on the thread that completes the removal: a finished removal is not kept in the table (and with it whatever its state machine still holds) a moment longer than it takes.
            _ = task.ContinueWith(ended =>
            {
                // Only its own registration: a later attempt may already have taken the place.
                lock (_lock) { if (_removals.TryGetValue(accountId, out var current) && ReferenceEquals(current, ended)) _removals.Remove(accountId); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        // From the call, not from the mark the removal writes a moment later: no ask of the hub for this account starts any more (an add that is released now, a poll of the scheduler), so a
        // redemption is never sent for an account whose removal has been asked for. Outside the lock, because the callbacks of the token run on this thread; and before the removal's first
        // step, because a removal that fails at once puts the work token back (ReopenWork), and that must not come before this cancel.
        try { StopNewAsks(accountId); }
        finally { begin.TrySetResult(); }
        return task;
    }

    /// <summary>Cancels the token every ask of the hub for this account runs under (the account's work is made if it has none yet, so that a later ask finds it cancelled too).</summary>
    private void StopNewAsks(string accountId)
    {
        try { WorkOf(accountId).Cancel(); }
        catch (AggregateException) { /* a callback of the token threw; the token is cancelled all the same, and the removal cancels it again */ }
    }

    /// <summary>A removal counts as an operation of the host from its call to its end: the root is not given back under it.</summary>
    private async Task RemoveTrackedAsync(BlindAccountEntry entry, IDisposable operation, bool ownAdd, Task begin, CancellationToken ct)
    {
        using (operation) await RemoveCoreAsync(entry, ownAdd, begin, ct).ConfigureAwait(false);
    }

    private async Task RemoveCoreAsync(BlindAccountEntry entry, bool ownAdd, Task begin, CancellationToken ct)
    {
        // Not before the caller has registered this task and stopped the account's new asks (StartRemoval).
        await begin.ConfigureAwait(false);
        var previousPhase = entry.Phase;
        // 1. The mark: from here a crash resumes the removal at the next start, and every step of an add or a start of this account that looks at the list finds it going.
        await MarkAsync(entry, ownAdd, ct).ConfigureAwait(false);
        _crash.Reach("remove:marked");
        RaiseChanged();

        BlindAccountRuntime? runtime = null;
        try
        {
            // 2. What the account asks of the hub ends first (an invitation being redeemed, a decision being waited for, a back-off that is pending), and so does an add that is still setting
            // it up: nothing of it may write a state, a folder or a key after this point. The runtime is looked for only now: that add may have made one.
            await StopInvitationWorkAsync(entry.Id, ownAdd, ct).ConfigureAwait(false);
            lock (_lock)
            {
                _runtimes.TryGetValue(entry.Id, out runtime);
                if (runtime is not null) runtime.Closing = true;
            }

            // 3. Stop this account's work and wait for it (the others go on).
            if (runtime is not null) await DrainAsync(entry, runtime, ct).ConfigureAwait(false);
        }
        catch
        {
            // Nothing was deleted and the keys are kept: the account is what it was. Put the mark back and let the work go on.
            if (runtime is not null) lock (_lock) runtime.Closing = false;
            try { _registry.Update(file => file with { Accounts = file.Entries.Select(e => e.Id == entry.Id ? e with { Phase = previousPhase } : e).ToList() }); }
            catch (BlindRegistryDamagedException) { }
            ReopenWork(entry.Id);
            RaiseChanged();
            throw;
        }
        _crash.Reach("remove:drained");

        await FinishRemovalAsync(entry with { Phase = BlindAccountPhases.Deleting }, runtime, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The removal's first step, with what a failure of it leaves to do. Nothing was marked then: the account is what it was, and the asks that <see cref="StartRemoval"/> stopped may go on. An add
    /// that was setting the account up has already seen its cancellation and has ended without finishing: nothing is left to carry the account on, and the card must say so instead of going on
    /// saying that it connects (<see cref="AfterCutShortAddAsync"/>). Its own method so that the removal itself keeps the shape it had: with this await in a catch of the removal's own body the
    /// desktop test that watches the services of removed accounts with weak references failed every time (the reason was not pinned down; the shape below does not).
    /// </summary>
    private async Task MarkAsync(BlindAccountEntry entry, bool ownAdd, CancellationToken ct)
    {
        ExceptionDispatchInfo? notMarked = null;
        try
        {
            _crash.Reach("remove:start");
            await MarkDeletingAsync(entry.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            notMarked = ExceptionDispatchInfo.Capture(ex);
        }
        if (notMarked is null) return;

        if (!ownAdd) await AfterCutShortAddAsync(entry.Id, ct).ConfigureAwait(false);
        ReopenWork(entry.Id);
        notMarked.Throw();
    }

    /// <summary>How many times the removal tries to write its mark. The first write of a removal can meet a registry file that is held for a moment (a scanner, another process); the asks it already stopped
    /// (<see cref="StopNewAsks"/>) cannot be taken back, so the removal does not give up at the first failure.</summary>
    internal const int MarkAttempts = 3;

    private static readonly TimeSpan MarkRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The removal's first step: the account is marked <c>deleting</c> in the registry. A write that fails for a reason that can pass (the file is held, the lock is taken for a moment) is tried again,
    /// <see cref="MarkAttempts"/> times in all; a registry that is damaged or a failure of any other kind ends it at once.
    /// </summary>
    private async Task MarkDeletingAsync(string id)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _registry.Update(file => file with { Accounts = file.Entries.Select(e => e.Id == id ? e with { Phase = BlindAccountPhases.Deleting } : e).ToList() });
                return;
            }
            catch (Exception ex) when (attempt < MarkAttempts && ex is BlindRegistryBusyException or IOException or UnauthorizedAccessException)
            {
                await Task.Delay(MarkRetryDelay).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A removal could not be marked after it had cancelled an add that was setting the account up. That add has ended without finishing (it saw the cancellation, and its work token stays
    /// cancelled), so the account would stay listed as it was - pending, "connecting" - with nothing in this session to ask the hub about it. Waits for the add to end and, when it was cut
    /// short: if its runtime was already up the add had only its last steps left, and the removal finishes them (the one-time pairing secret is spent, the account is set up, and the scheduler
    /// and the screen ask the hub about it as they would have); if the runtime is not up the account says so on its card (unless it already has a problem to tell), and the person can remove
    /// it again, which now waits for nothing.
    /// </summary>
    private async Task AfterCutShortAddAsync(string id, CancellationToken ct)
    {
        AccountWork? work;
        lock (_lock) _work.TryGetValue(id, out work);
        if (work is null || !work.IsCancelled || work.IsSetUp) return;
        try { await work.AddEnded.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (!work.CutShort) return;

        if (AccountIsUp(id, out _))
        {
            if (_registry.Current.Find(id) is { InviteId: not null })
            {
                SpendPairingSecret(id);
                work.MarkSetUp();
            }
        }
        else
        {
            lock (_lock) _problems.TryAdd(id, "Setting up this account was cut short by a removal that could not be written. Remove the account again.");
        }
        RaiseChanged();
    }

    /// <summary>Stops an account's work the way every end of an account does: no new work, the running work asked to stop, the platform's schedulers told.</summary>
    private void StopWork(BlindAccountEntry entry, BlindAccountRuntime runtime)
    {
        BlindActivity? activity = null;
        try { activity = runtime.Services.GetService<BlindActivity>(); }
        catch (ObjectDisposedException) { }
        activity?.Close();
        activity?.CancelAll();
        _options.Lifecycle?.StopBackgroundWork(entry);
        _options.Lifecycle?.StopBackupService(entry);
    }

    private async Task DrainAsync(BlindAccountEntry entry, BlindAccountRuntime runtime, CancellationToken ct)
    {
        StopWork(entry, runtime);
        var activity = runtime.Services.GetService<BlindActivity>();

        if (activity is not null && !await activity.WaitIdleAsync(_options.DrainTimeout, ct).ConfigureAwait(false))
        {
            var running = string.Join(", ", activity.ActiveKinds().Distinct());
            activity.Reopen();
            _options.Lifecycle?.ResumeBackgroundWork(entry);
            throw new AggregateException(
                "The account was not removed: its work did not stop in time (" + running + "). Nothing was deleted and its keys are kept; try again.",
                new IOException("still running: " + running));
        }
    }

    /// <summary>
    /// The steps after the drain, each safe to run again (so the same code finishes a removal that a crash cut off): the courtesy call, the runtime closed, the keys,
    /// the state, the files, and the registry entry last. A step that fails does not stop the others; the failures are reported together and the entry stays
    /// <c>deleting</c> for the next try.
    /// </summary>
    private async Task FinishRemovalAsync(BlindAccountEntry entry, BlindAccountRuntime? runtime, CancellationToken ct)
    {
        var failures = new List<Exception>();
        // A removal that a crash cut off and the next start finishes has no ask in flight; this is the same wipe, so that every way of dropping an account lets go of the secret.
        DropRedemption(entry.Id);
        ForgetRhythm(entry.Id);
        async Task Step(string what, Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            // No inner exception: the removal throws these to the head, and a head that tells the whole chain would read the path of the file or the alias of the key that failed.
            catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add(new IOException($"{what}: {FailureWords(ex)}")); }
        }

        // The courtesy call. A crash after it (the entry is still "deleting") makes the next start call it AGAIN for the same account: the hook is idempotent at the source by contract
        // (IBlindAccountLeaveHook), and leaving a network one has already left is a success there.
        if (runtime is not null) await Step("telling the source", () => LeaveAsync(entry, runtime, ct));
        _crash.Reach("remove:left");

        if (runtime is not null) await Step("closing the account", () => CloseRuntimeAsync(runtime));
        lock (_lock) { if (runtime is not null && _runtimes.TryGetValue(entry.Id, out var current) && ReferenceEquals(current, runtime)) _runtimes.Remove(entry.Id); }
        // Its files are still on disk and still count towards the device, until they are gone and the entry is out of the list.
        DetachIfListed(entry);
        _crash.Reach("remove:closed");

        await Step("forgetting the keys", () => { _options.Secrets.For(entry).Clear(); return Task.CompletedTask; });
        _crash.Reach("remove:keys-cleared");

        await Step("forgetting the state", () =>
        {
            var store = _options.States.For(entry);
            new BlindPhoneState(store).Clear();
            store.Erase();
            return Task.CompletedTask;
        });
        _crash.Reach("remove:state-cleared");

        await Step("removing the files", () => { DeleteFiles(entry); return Task.CompletedTask; });
        _crash.Reach("remove:files-deleted");

        if (failures.Count > 0)
        {
            Remember(entry.Id, "Its removal is not complete: " + string.Join("; ", failures.Select(f => f.Message)));
            RaiseChanged();
            throw new AggregateException("The removal is not complete: " + string.Join("; ", failures.Select(f => f.Message)), failures);
        }

        // The entry goes last: until it does, every step above can be run again.
        _registry.Update(file => file with { Accounts = file.Entries.Where(e => e.Id != entry.Id).ToList() });
        _deviceUsage.Unregister(entry.Id);
        lock (_lock) _problems.Remove(entry.Id);
        ForgetWork(entry.Id);
        _crash.Reach("remove:done");
        RaiseChanged();
    }

    private async Task LeaveAsync(BlindAccountEntry entry, BlindAccountRuntime runtime, CancellationToken ct)
    {
        if (_options.LeaveHook is not { } hook) return;
        // Best effort, the whole of it: an account that never paired has nobody to tell, and a state that cannot be read or a hook that fails or hangs must not hold up the removal.
        try
        {
            if (runtime.Services.GetService<BlindPhoneState>()?.CallCode is null) return;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(_options.LeaveTimeout);
            await hook.LeaveAsync(new BlindAccountLeaveContext(entry, runtime.Services), limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // the source learns by this device's silence, as it always did
        }
    }

    private void DeleteFiles(BlindAccountEntry entry)
    {
        if (entry.IsLegacy)
        {
            // The app's root also holds accounts.json, accounts/ and things that are not ours: exactly what a 2.6 wipe deletes, and nothing else.
            foreach (var path in BlindPhoneReset.OwnedPaths(_registry.RootDirectory).ToList()) DeletePath(path);
            // The host's own database path, with the files SQLite keeps beside it (a path outside the pattern OwnedPaths covers).
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) DeletePath(_options.LegacyDatabasePath + suffix);
            return;
        }

        DeleteAccountFolder(_registry.RootDirectory, entry);
    }

    /// <summary>
    /// Deletes <c>accounts/&lt;id&gt;</c> after checking, on the RESOLVED path, that it sits directly under <c>accounts/</c> and that neither it nor <c>accounts/</c> is a link
    /// (a link would send the delete somewhere else). A link is refused, not followed and not removed: it needs a person. The links are looked at BEFORE the folder is
    /// asked for: a dangling link is neither a folder nor a file, and would otherwise read as "nothing to delete" and let the entry go while the link stays.
    /// </summary>
    internal static void DeleteAccountFolder(string root, BlindAccountEntry entry)
    {
        var accounts = Path.GetFullPath(Path.Combine(root, BlindAccountRegistry.AccountsFolder));
        var folder = Path.GetFullPath(BlindAccountNames.DataDirectory(root, entry)); // refuses an id that is not 32 hex characters
        var parent = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.Equals(parent?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), accounts.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison))
            throw new BlindFileSentenceException("The account's folder is not directly under the accounts folder; nothing was deleted.");
        if (BlindPathSafety.IsLink(accounts) || BlindPathSafety.IsLink(folder))
            throw new BlindFileSentenceException("The account's folder is a link; it was not followed or deleted. Remove the link by hand.");
        if (!Directory.Exists(folder))
        {
            if (File.Exists(folder)) throw new BlindFileSentenceException("The account's folder name is taken by a file; nothing was deleted.");
            return;
        }
        DeleteWithRetry(() => Directory.Delete(folder, recursive: true));
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path)) DeleteWithRetry(() => Directory.Delete(path, recursive: true));
        else if (File.Exists(path)) DeleteWithRetry(() => File.Delete(path));
    }

    /// <summary>A handle that was released a moment ago (SQLite's, a virus scanner's) may still hold a file: a few short tries before it counts as a failure.</summary>
    private static void DeleteWithRetry(Action delete)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { delete(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5) { Thread.Sleep(50 * attempt); }
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // ---- runtimes ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the account's runtime. False when it was NOT started because the account is no longer there to start: it is not listed any more, or is being removed (the entry this call was
    /// given is a snapshot; the list is asked again before anything is made, and again at the moment the runtime would be published), or the host is closing. True otherwise - the account
    /// is up, or says in <c>_problems</c> why it is not.
    /// </summary>
    private async Task<bool> StartRuntimeAsync(BlindAccountEntry entry, CancellationToken ct)
    {
        // A live runtime is never overwritten (that would leave its database, its HTTP client and its work running unreachable): one that is up stays.
        lock (_lock)
        {
            if (_disposed) return false;
            if (_runtimes.TryGetValue(entry.Id, out var live) && !live.Closing) return true;
        }

        // Before a folder, a database or a key is made: is this still the account that is listed, and not going. A removal that finished while this waited has deleted the folder; making it
        // again here would leave an account nobody lists, with an identity.
        if (!StillTheListedEntry(entry)) return false;

        // Before anything is created or composed: is the folder what it should be. A link, or a folder that an established account has lost, is a repair case: no runtime, no
        // database, no key touched.
        if (Preflight(entry) is { } issue)
        {
            Remember(entry.Id, NeedsRepair + issue.Description);
            lock (_lock) { if (!_repairs.Any(i => i.Kind == issue.Kind && i.AccountId == issue.AccountId)) _repairs = [.. _repairs, issue]; }
            return true;
        }

        BlindAccountRuntime runtime;
        try
        {
            runtime = BuildRuntime(entry, createFolder: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Remember(entry.Id, "It could not be started: " + FailureWords(ex));
            return true;
        }

        // The folder exists; from here an identity can be made in it, so that is on record first (see RecordEstablished). Nothing has been initialised yet: a record that cannot be written
        // ends this start here. (Outside the guarded start below, as the write after it always was: a crash at that point is a crash, not "the start failed".)
        if (!entry.IsLegacy && !entry.Established)
        {
            try
            {
                RecordEstablished(entry);
            }
            catch (IOException ex)
            {
                await DiscardAsync(runtime).ConfigureAwait(false);
                Remember(entry.Id, "It could not be started: " + FailureWords(ex));
                return true;
            }
        }

        var initialized = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var refused = false;
            var gone = false;
            lock (_lock)
            {
                // Looked at once more where the runtime is published: a removal marks the list BEFORE it looks for the runtime of the account (under this same lock), so either it finds this
                // one, or this finds the mark. What was composed after the account went is ended here and not kept.
                if (_disposed) refused = true;
                else if (!StillTheListedEntry(entry)) gone = true;
                else
                {
                    _runtimes[entry.Id] = runtime;
                    _problems.Remove(entry.Id);
                }
            }
            if (refused || gone)
            {
                await ShutdownOrRetainAsync(runtime).ConfigureAwait(false);
                return !gone;
            }

            // Never throws for a start problem (it is in the account's status); only for cancellation.
            await runtime.Controller.InitializeAsync(linked.Token).ConfigureAwait(false);
            initialized = true;
        }
        catch (OperationCanceledException)
        {
            // A start that did not complete leaves nothing behind: the runtime goes the way a removed one does, and the account says so.
            await DiscardAsync(runtime).ConfigureAwait(false);
            Remember(entry.Id, "Its start was cancelled. It starts again the next time the app opens.");
            throw;
        }
        catch (Exception ex)
        {
            await DiscardAsync(runtime).ConfigureAwait(false);
            Remember(entry.Id, "It could not be started: " + FailureWords(ex));
            return true;
        }

        // Outside the try above: writing it down is no part of "the start did not complete", and a failure of that write is handled in its own place.
        if (initialized) MarkEstablished(entry, runtime);
        return true;
    }

    /// <summary>True when the list still has this account and it is not being removed: the entry a caller holds is a snapshot, and the list is the truth.</summary>
    private bool StillTheListedEntry(BlindAccountEntry entry) =>
        _registry.Current.Find(entry.Id) is { } current && current.Phase != BlindAccountPhases.Deleting && current.IsLegacy == entry.IsLegacy;

    /// <summary>Takes a runtime out of the host and ends it (same lifecycle as a removal's, without deleting anything).</summary>
    private async Task DiscardAsync(BlindAccountRuntime runtime)
    {
        lock (_lock) { if (_runtimes.TryGetValue(runtime.Id, out var current) && ReferenceEquals(current, runtime)) _runtimes.Remove(runtime.Id); }
        await ShutdownOrRetainAsync(runtime).ConfigureAwait(false);
    }

    /// <summary>Ends a runtime that is not being removed. One whose work does not stop in time is KEPT (its services are not disposed beneath that work) and ended when the shutdown completes.</summary>
    private async Task ShutdownOrRetainAsync(BlindAccountRuntime runtime)
    {
        try
        {
            if (await ShutdownRuntimeAsync(runtime).ConfigureAwait(false)) return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return; // it could not be closed cleanly; there is nothing running under it to protect
        }
        lock (_lock) { if (!_retained.Contains(runtime)) _retained.Add(runtime); }
    }

    /// <summary>
    /// The account's entry says it is set up: written BEFORE its identity is made (<see cref="StartRuntimeAsync"/>), once its folder exists, so that from the first moment an identity can exist a
    /// missing folder is a repair case. Throws when it cannot be written, and the account is then not started (it says so on its card, and the next start tries again): never a runtime that
    /// holds an identity the registry does not know of.
    /// </summary>
    private void RecordEstablished(BlindAccountEntry entry)
    {
        try
        {
            _registry.Update(file => file.Find(entry.Id) is { Established: false } current
                ? file with { Accounts = file.Entries.Select(e => e.Id == entry.Id ? current with { Established = true } : e).ToList() }
                : file);
        }
        catch (Exception ex) when (ex is BlindRegistryDamagedException or ObjectDisposedException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new BlindFileSentenceException("Recording that this account is being set up failed (" + ex.GetType().Name + "). It was not started; the next start tries again.", ex);
        }
    }

    /// <summary>
    /// The account has an identity: a backstop for an account that an earlier version started without recording it. A failure to write is made visible on the account (the setup is not on
    /// record); what protects the account's keys meanwhile is that a missing folder of an account that holds any trace of an identity is a repair case whatever the entry says
    /// (<see cref="HoldsIdentityTrace"/>).
    /// </summary>
    private void MarkEstablished(BlindAccountEntry entry, BlindAccountRuntime runtime)
    {
        // Looked at in the list as it is now: BuildRuntime has usually written the record already, and then there is nothing to write.
        if (entry.Established || _registry.Current.Find(entry.Id) is not { Established: false }) return;
        try
        {
            if (!runtime.Services.GetRequiredService<BlindMobilePairing>().HasIdentity) return;
            _registry.Update(file => file.Find(entry.Id) is { Established: false } current
                ? file with { Accounts = file.Entries.Select(e => e.Id == entry.Id ? current with { Established = true } : e).ToList() }
                : file);
        }
        catch (Exception ex) when (ex is BlindRegistryDamagedException or ObjectDisposedException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Remember(entry.Id, "Its setup could not be recorded in the list of accounts (" + ex.GetType().Name + "). It runs, but if its folder were lost it would have to be repaired by hand.");
        }
    }

    /// <summary>A meter over what a listed account owns on disk, for an account that has no runtime (the same roots, and the same refusal of a link, as the runtime's own meter).</summary>
    private BlindStoredBytes DetachedMeter(BlindAccountEntry entry)
    {
        var dataDir = BlindAccountNames.DataDirectory(_registry.RootDirectory, entry);
        var dbPath = BlindAccountNames.DatabasePath(_registry.RootDirectory, entry, _options.LegacyDatabasePath);
        return new BlindStoredBytes(() => BlindMobileServices.StoredRoots(dataDir, dbPath), _options.Time, dataDir, null);
    }

    /// <summary>The device's accounts are the listed ones: registers a meter for each that has none, and lets go of what is no longer listed.</summary>
    private void SyncDeviceAccounts()
    {
        var file = _registry.Current;
        var listed = new HashSet<string>(file.Entries.Select(e => e.Id), StringComparer.Ordinal);
        foreach (var entry in file.Entries)
        {
            var current = entry;
            _deviceUsage.EnsureListed(current.Id, () => DetachedMeter(current));
        }
        foreach (var id in _deviceUsage.RegisteredIds())
            if (!listed.Contains(id)) _deviceUsage.Unregister(id);
    }

    /// <summary>A runtime ended: while its account is still listed its files keep counting, through a meter of their own.</summary>
    private void DetachIfListed(BlindAccountEntry entry)
    {
        if (_registry.Current.Find(entry.Id) is { } listed) _deviceUsage.Detach(listed.Id, () => DetachedMeter(listed));
        else _deviceUsage.Unregister(entry.Id);
    }

    private BlindAccountRuntime BuildRuntime(BlindAccountEntry entry, bool createFolder)
    {
        var root = _registry.RootDirectory;
        // Judged again at the moment of use, whoever called: nothing is created or composed through a link.
        if (BlindPathSafety.AccountFolderProblem(root, entry) is { } unsafePath) throw new BlindFileSentenceException(unsafePath);
        var dataDir = BlindAccountNames.DataDirectory(root, entry);
        var dbPath = BlindAccountNames.DatabasePath(root, entry, _options.LegacyDatabasePath);
        if (BlindPathSafety.OwnedChildProblem(dataDir, dbPath) is { } inner) throw new BlindFileSentenceException(inner);
        if (!entry.IsLegacy && createFolder) Directory.CreateDirectory(dataDir);

        var services = new ServiceCollection();
        services.AddSingleton(_options.Secrets.For(entry));
        services.AddSingleton(_options.States.For(entry));
        services.AddSingleton<IBlindLifecycle>(new AccountLifecycle(this, entry));
        services.AddSingleton<IBlindSourceRemovalSink>(sp => new SourceRemovalSink(this, entry.Id, sp.GetRequiredService<BlindPhoneState>(), _options.Time));
        // Limits: the account's policy is whatever its registry entry says now; the device's usage is the one every account shares.
        services.AddSingleton<IBlindAccountPolicySource>(new RegistryPolicySource(_registry, entry.Id));
        services.AddSingleton(_deviceUsage);
        if (_options.Conditions is { } conditions) services.AddSingleton(conditions);
        _options.ConfigureHostSeams?.Invoke(services, entry);
        BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(dataDir, dbPath, _options.Time, _options.DisplayNameFactory)
        {
            DeviceGate = _gate,
            DeviceOwnerId = entry.Id,
            DeviceOwnerName = () => _registry.Current.Find(entry.Id)?.Name ?? entry.Name,
            PrimaryHandlerFactory = _options.PrimaryHandlerFactory?.Invoke(entry),
        });
        _options.ConfigureOverrides?.Invoke(services, entry);

        var provider = services.BuildServiceProvider();
        try
        {
            var controller = provider.GetRequiredService<BlindAppController>();
            controller.Changed += RaiseChanged;
            _deviceUsage.Register(entry.Id, provider.GetRequiredService<BlindUsageLedger>(), provider.GetRequiredService<BlindStoredBytes>());
            return new BlindAccountRuntime(entry, provider, controller);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A runtime for the courtesy call of a removal that a crash cut off: over what the account still has, and nothing is made for it (no folder, no identity, no database). Null when
    /// there is no hook, nothing left to call with (the folder is gone, a link, never paired), or it cannot be built: the removal then goes on without telling the source.
    /// </summary>
    private BlindAccountRuntime? BuildRecoveryRuntime(BlindAccountEntry entry)
    {
        if (_options.LeaveHook is null) return null;
        try
        {
            if (BlindPathSafety.AccountFolderProblem(_registry.RootDirectory, entry) is not null) return null;
            if (!entry.IsLegacy && !Directory.Exists(BlindAccountNames.DataDirectory(_registry.RootDirectory, entry))) return null;
            var runtime = BuildRuntime(entry, createFolder: false);
            runtime.Closing = true;
            return runtime;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

    private async Task CloseRuntimeAsync(BlindAccountRuntime runtime)
    {
        DbConnectionFactory? factory = null;
        try { factory = runtime.Services.GetService<DbConnectionFactory>(); }
        catch (ObjectDisposedException) { }
        runtime.Controller.Changed -= RaiseChanged;
        await runtime.DisposeAsync().ConfigureAwait(false);
        // Only this account's idle connections: the others' pools are not ours.
        BlindPhoneReset.ClosePool(factory);
    }

    /// <summary>
    /// The end of a runtime that is NOT being removed (the host is disposed, a start did not complete): the same lifecycle as a removal's, in the same order. No new work, the running
    /// work asked to stop and waited for (a long job lets go of the device's gate when it ends), the opening of its database settled, and only then the services disposed, so that no job
    /// meets a disposed provider. True when the runtime was closed. FALSE when work did not stop within <paramref name="wait"/> (default: <see cref="BlindAccountHostOptions.DrainTimeout"/>)
    /// or the database is still being opened: the services are then NOT disposed (a thread cannot be killed, and what it is using must stay), and the runtime can be asked again.
    /// Safe to call again and from two places at once: attempts are taken one at a time, and one that has closed the runtime is final.
    /// </summary>
    private Task<bool> ShutdownRuntimeAsync(BlindAccountRuntime runtime, TimeSpan? wait = null) => runtime.ShutdownAsync(async () =>
    {
        var patience = wait ?? _options.DrainTimeout;
        runtime.Closing = true;
        try { StopWork(runtime.Entry, runtime); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* a platform scheduler that fails to stop must not keep the services alive */ }
        BlindActivity? activity = null;
        try { activity = runtime.Services.GetService<BlindActivity>(); }
        catch (ObjectDisposedException) { }
        if (activity is not null && !await activity.WaitIdleAsync(patience).ConfigureAwait(false)) return false;
        // The opening of the database may still be going on after a start that was cancelled (its caller stopped waiting, the run did not): it ends before the services under it do.
        BlindStartup? startup = null;
        try { startup = runtime.Services.GetService<BlindStartup>(); }
        catch (ObjectDisposedException) { }
        if (startup is not null && !await startup.SettleAsync(patience).ConfigureAwait(false)) return false;
        DetachIfListed(runtime.Entry);
        await CloseRuntimeAsync(runtime).ConfigureAwait(false);
        return true;
    });

    /// <summary>After "Disconnect and wipe" of one account: its files and keys are gone and its runtime holds a database that no longer exists. Builds a fresh runtime (a new identity, a new phone code), as a single-account copy does after a wipe.</summary>
    private void OnAccountWiped(BlindAccountEntry entry)
    {
        // A host that is closing composes nothing: checked before anything is built, and the rebuild counts as an operation, so the root is not given back under it.
        var operation = TryBeginOperation();
        if (operation is null) return;
        _ = Task.Run(async () =>
        {
            using var scope = operation;
            BlindAccountRuntime? old = null;
            BlindAccountRuntime? fresh = null;
            try
            {
                ForgetInvitation(entry.Id);
                lock (_lock) _runtimes.TryGetValue(entry.Id, out old);
                if (_registry.Current.Find(entry.Id) is not { } current || current.Phase == BlindAccountPhases.Deleting) return;
                if (_disposed || _lifetime.IsCancellationRequested) return;
                fresh = BuildRuntime(current, createFolder: true);
                var refused = false;
                lock (_lock)
                {
                    if (_disposed) refused = true;
                    else
                    {
                        _runtimes[entry.Id] = fresh;
                        _problems.Remove(entry.Id);
                    }
                }
                if (refused)
                {
                    await ShutdownOrRetainAsync(fresh).ConfigureAwait(false);
                    if (old is not null) await RetireAsync(old).ConfigureAwait(false);
                    return;
                }
                await fresh.Controller.InitializeAsync(_lifetime.Token).ConfigureAwait(false);
                MarkEstablished(current, fresh);
                if (old is not null) await RetireAsync(old).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (fresh is not null) await DiscardAsync(fresh).ConfigureAwait(false);
                if (old is not null) { try { await RetireAsync(old).ConfigureAwait(false); } catch (Exception inner) when (inner is not OutOfMemoryException) { } }
                if (ex is not OperationCanceledException) Remember(entry.Id, "It could not be set up again after the wipe: " + FailureWords(ex));
            }
            RaiseChanged();
        });
    }

    /// <summary>The runtime a wipe replaced is closed: after its work has ended and its database is settled. One whose work does not end is kept, not disposed beneath it.</summary>
    private async Task RetireAsync(BlindAccountRuntime old)
    {
        old.Closing = true;
        var closed = await old.ShutdownAsync(async () =>
        {
            BlindActivity? activity = null;
            try { activity = old.Services.GetService<BlindActivity>(); }
            catch (ObjectDisposedException) { }
            if (activity is not null && !await activity.WaitIdleAsync(_options.DrainTimeout).ConfigureAwait(false)) return false;
            BlindStartup? startup = null;
            try { startup = old.Services.GetService<BlindStartup>(); }
            catch (ObjectDisposedException) { }
            if (startup is not null && !await startup.SettleAsync(_options.DrainTimeout).ConfigureAwait(false)) return false;
            await CloseRuntimeAsync(old).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        if (!closed) lock (_lock) { if (!_retained.Contains(old)) _retained.Add(old); }
    }

    private sealed class AccountLifecycle(BlindAccountHost host, BlindAccountEntry entry) : IBlindLifecycle
    {
        public void StopBackgroundWork() => host._options.Lifecycle?.StopBackgroundWork(entry);
        public void StopBackupService() => host._options.Lifecycle?.StopBackupService(entry);
        public void ResumeBackgroundWork() => host._options.Lifecycle?.ResumeBackgroundWork(entry);
        public void RestartAfterWipe() => host.OnAccountWiped(entry);
    }

    // ----------------------------------------------------------------------------------------------------------------------------------------

    private void Remember(string accountId, string problem)
    {
        lock (_lock) _problems[accountId] = problem;
    }

    /// <summary>
    /// What a card, a start problem or a removal says about an exception: the KIND of failure (<see cref="BlindFailureText.Say"/>), or a bounded sentence this code wrote, one line. Never the
    /// message of the exception - an I/O, database or crypto message carries a path, a key alias or a library's words, and a call to the source carries the address that was dialled.
    /// </summary>
    internal static string FailureWords(Exception ex) => BlindFailureText.Say(ex);

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* a screen that is gone must not break the work that raised it */ }
    }

    // ---- the operations the host counts, and the shutdown ---------------------------------------------------------------------------------------

    /// <summary>Counts an operation that uses the root (a start, an add, a repair, a removal, the rebuild after a wipe) from now until the returned scope is disposed. Throws when the host is closing.</summary>
    private IDisposable BeginOperation() => TryBeginOperation() ?? throw new ObjectDisposedException(nameof(BlindAccountHost));

    private IDisposable? TryBeginOperation()
    {
        lock (_lock)
        {
            if (_disposed) return null;
            if (_activeOps++ == 0) _opsIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new OperationScope(this);
        }
    }

    private void EndOperation()
    {
        TaskCompletionSource? idle = null;
        lock (_lock)
        {
            if (--_activeOps == 0) idle = _opsIdle;
        }
        idle?.TrySetResult();
    }

    private sealed class OperationScope(BlindAccountHost host) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) host.EndOperation();
        }
    }

    /// <summary>Completes when no operation is under way (after the host is disposed none can begin).</summary>
    private Task OperationsIdle()
    {
        lock (_lock) return _activeOps == 0 ? Task.CompletedTask : _opsIdle.Task;
    }

    /// <summary>
    /// Waits, for at most <paramref name="patience"/>, until no operation is under way, then closes every runtime (the ones in the host and the ones kept from earlier). True when all of it
    /// has ended and every runtime is closed; false when something is still running. While an operation is under way NO runtime is touched: an operation (a removal, a start) uses
    /// the runtimes it works on, and a provider is never disposed beneath it.
    /// </summary>
    private async Task<bool> QuiesceAsync(TimeSpan patience)
    {
        var quiet = true;
        var idle = OperationsIdle();
        if (!idle.IsCompleted)
        {
            try { await idle.WaitAsync(patience).ConfigureAwait(false); }
            catch (TimeoutException) { return false; }
        }

        List<BlindAccountRuntime> pending;
        lock (_lock) pending = [.. _runtimes.Values.Concat(_retained).Distinct()];
        foreach (var runtime in pending)
        {
            var closed = false;
            try { closed = await ShutdownRuntimeAsync(runtime, patience).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { closed = true; /* it could not be closed cleanly; nothing runs under it that a wait would protect */ }
            if (!closed)
            {
                quiet = false;
                continue;
            }
            lock (_lock)
            {
                if (_runtimes.TryGetValue(runtime.Id, out var current) && ReferenceEquals(current, runtime)) _runtimes.Remove(runtime.Id);
                _retained.Remove(runtime);
            }
        }
        return quiet;
    }

    private void ReleaseRoot()
    {
        ZeroInvitationSecrets();
        _registry.Release();
        Interlocked.Exchange(ref _rootLock, null)?.Dispose();
    }

    /// <summary>
    /// Ends the host: a start that is under way is cancelled, every account is stopped the way a removal stops it (no new work, the running work asked to stop and waited for, the
    /// device's gate let go of by the jobs as they end) and only then its services are disposed, and last the root is given back so that another host may take it.
    ///
    /// <para>Bounded by <see cref="BlindAccountHostOptions.DrainTimeout"/>. If work is still running by then (a job that ignores its cancellation, a removal waiting on a hook that never
    /// answers) NOTHING is disposed under it and the root is NOT given back: the call returns, <see cref="ShutdownIncomplete"/> is true, and the shutdown is finished by the host itself, with the
    /// root released, when that work has ended.</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var runtime in _runtimes.Values) runtime.Closing = true;
        }

        try { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { }

        if (await QuiesceAsync(_options.DrainTimeout).ConfigureAwait(false))
        {
            ReleaseRoot();
            return;
        }

        _shutdownFault = "The app is closing, but work that did not stop when it was asked to is still running. This data folder stays reserved for it until it has ended; nothing was disposed beneath it.";
        RaiseChanged();
        _ = Task.Run(FinishShutdownLateAsync);
    }

    /// <summary>The rest of a shutdown that had to leave work running: waits for it without a limit, closes what it was using, and only then gives the root back.</summary>
    private async Task FinishShutdownLateAsync()
    {
        while (true)
        {
            try
            {
                if (await QuiesceAsync(Timeout.InfiniteTimeSpan).ConfigureAwait(false))
                {
                    lock (_lock)
                    {
                        // Nothing began after the host was disposed, but something that was running may have left a runtime behind just now: round again.
                        if (_activeOps != 0 || _retained.Count != 0 || _runtimes.Count != 0) continue;
                    }
                    break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
        }
        ReleaseRoot();
        _shutdownFault = null;
        RaiseChanged();
    }
}

/// <summary>One account's running composition: its own service provider (own database, HTTP client and pins, identity, state, log) and its controller.</summary>
internal sealed class BlindAccountRuntime(BlindAccountEntry entry, ServiceProvider services, BlindAppController controller) : IAsyncDisposable
{
    private readonly SemaphoreSlim _shutdownGate = new(1, 1);

    public string Id { get; } = entry.Id;
    public BlindAccountEntry Entry { get; } = entry;
    public ServiceProvider Services { get; } = services;
    public BlindAppController Controller { get; } = controller;

    /// <summary>True from the moment the account is being removed: new work is refused and <c>For(id)</c> no longer finds it.</summary>
    public bool Closing { get; set; }

    /// <summary>True once an attempt to end the runtime has closed it (its services are disposed): final.</summary>
    public bool Disposed { get; private set; }

    /// <summary>
    /// Runs one attempt to end the runtime, one at a time. An attempt that returns false (work that did not stop was left alone, and nothing was disposed) can be made again; one that
    /// returns true has closed the runtime, and every later call answers true at once.
    /// </summary>
    public async Task<bool> ShutdownAsync(Func<Task<bool>> attempt)
    {
        await _shutdownGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Disposed) return true;
            if (!await attempt().ConfigureAwait(false)) return false;
            Disposed = true;
            return true;
        }
        finally
        {
            _shutdownGate.Release();
        }
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
