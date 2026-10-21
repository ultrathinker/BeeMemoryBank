using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>What iOS can be asked about background time: submit the two background requests again, or withdraw them.</summary>
public interface IIosBackgroundRequests
{
    /// <summary>Asks iOS for the next sync round and the next long-job window (a request with the same identifier replaces the pending one).</summary>
    void Submit();

    /// <summary>Withdraws both requests (the device holds no account that needs them).</summary>
    void CancelAll();
}

/// <summary>What the iPhone app is built from; the iOS parts are given by the app, fakes by the tests.</summary>
/// <param name="Paths">The app's root folder (the app's container) and the database of account 1 in it: the 2.6 layout, which is account 1 and stays where it is.</param>
/// <param name="Secrets">The Keychain items of each account (<see cref="IosKeychainSecretStoreFactory"/> on the phone).</param>
/// <param name="Background">The BGTaskScheduler requests.</param>
/// <param name="Conditions">What the phone says about its network and its power (<see cref="IosDeviceConditions"/> on the phone).</param>
/// <param name="Files">The file attributes put on every account's folder (<c>IosFileAttributes</c> on the phone).</param>
/// <param name="DisplayName">How this phone names itself when an account's identity is made.</param>
/// <param name="Scheduler">Timings of the in-app loop; null: <see cref="IosBlindRuntime.DefaultScheduler"/>.</param>
public sealed record IosBlindRuntimeOptions(
    IosBlindPaths Paths,
    IBlindSecretStoreFactory Secrets,
    IIosBackgroundRequests Background,
    IBlindDeviceConditions Conditions,
    IIosFileProtection Files,
    Func<string>? DisplayName = null,
    TimeProvider? Time = null,
    BlindDeviceSchedulerOptions? Scheduler = null)
{
    // Init properties, for the tests only: the network side of every account behind fakes, and the hooks the host would otherwise take from AppCore.

    /// <summary>The handler at the bottom of an account's HTTP pipeline (see <see cref="BlindAccountHostOptions.PrimaryHandlerFactory"/>).</summary>
    public Func<BlindAccountEntry, Func<HttpMessageHandler>?>? PrimaryHandlerFactory { get; init; }

    /// <summary>Services that replace what <c>AddBlindAppCore</c> registered for one account.</summary>
    public Action<IServiceCollection, BlindAccountEntry>? ConfigureOverrides { get; init; }

    /// <summary>The courtesy call to the source on removal; null: AppCore's. A function that returns null turns the call off.</summary>
    public Func<IBlindAccountLeaveHook?>? LeaveHook { get; init; }

    /// <summary>The most accounts; null: AppCore's 8.</summary>
    public int? MaxAccounts { get; init; }
}

/// <summary>
/// The iPhone blind app in this process: ONE <see cref="BlindAccountHost"/> that holds a complete blind copy per account (its own folder, database, identity, Keychain items,
/// state file and pinned HTTP client), the ONE device scheduler that serves them in turn while the app is in front, and the phone's answers about its network and its power.
/// It lives as long as the process. Adding or removing an account builds or ends that account's runtime inside the host and nothing else: the screens, the scheduler and the other
/// accounts' controllers stay as they are, and "Disconnect and wipe" of one account makes that account a fresh one in place (<see cref="BlindAccountHost"/> does it); iOS gives an app no way
/// to restart itself, and none is needed.
///
/// <para>The 2.6 install is account 1: the host adopts it where it is (the app's root, its database, its <c>blind-state.json</c>, its Keychain items), moving and renaming nothing.</para>
/// </summary>
public sealed class IosBlindRuntime : IAsyncDisposable
{
    /// <summary>
    /// How the in-app loop is timed on a phone: the AppCore defaults (a round every 15 minutes, the long job every hour), but the loop is asked to stop for the few seconds iOS grants when the
    /// app goes to the background, so a round that will not stop is let go of after 5 seconds rather than 15, and the stop itself waits 25 seconds rather than a minute.
    /// </summary>
    public static BlindDeviceSchedulerOptions DefaultScheduler { get; } = new() { StopGrace = TimeSpan.FromSeconds(5), StopTimeout = TimeSpan.FromSeconds(25) };

    /// <summary>The file in the app's root that holds the DEVICE's own log: what the background wakes did in numbers, and why one did nothing. No account's name or fate is ever written to it.</summary>
    public const string DeviceLogFileName = "blind-device-log.jsonl";

    private readonly TimeProvider _time;
    private readonly IIosBackgroundRequests _background;
    private readonly IosLifecycle _lifecycle;
    private int _requestsWanted = -1;

    private IosBlindRuntime(IosBlindRuntimeOptions options)
    {
        Paths = options.Paths;
        Conditions = options.Conditions;
        _time = options.Time ?? TimeProvider.System;
        _background = options.Background;
        DeviceLog = new BlindPhoneLog(Path.Combine(options.Paths.DataDirectory, DeviceLogFileName), _time);

        var root = options.Paths.DataDirectory;
        var hostOptions = new BlindAccountHostOptions
        {
            RootDirectory = root,
            LegacyDatabasePath = options.Paths.DatabasePath,
            Secrets = options.Secrets,
            States = new IosBlindStateStoreFactory(root),
            Time = _time,
            DisplayNameFactory = options.DisplayName,
            Conditions = options.Conditions,
            IsPhone = true,
            // After the host made an account's folder and before the account is composed: EVERY account folder gets the file protection and the no-backup mark of the 2.6 root.
            ConfigureHostSeams = (_, account) => IosAccountFolders.PrepareAccount(options.Files, root, account),
            ConfigureOverrides = options.ConfigureOverrides,
            PrimaryHandlerFactory = options.PrimaryHandlerFactory,
            LeaveHook = options.LeaveHook is { } leave ? leave() : BlindSourceLeaveHook.Instance,
            MaxAccounts = options.MaxAccounts ?? BlindAccountHost.DefaultMaxAccounts,
        };
        Host = new BlindAccountHost(hostOptions);
        Scheduler = new BlindDeviceScheduler(Host, _time, options.Scheduler ?? DefaultScheduler);
        _lifecycle = new IosLifecycle(new LoopControl(this));
    }

    /// <summary>The app's root folder and the database of account 1.</summary>
    public IosBlindPaths Paths { get; }

    /// <summary>What the phone says about its network and its power.</summary>
    public IBlindDeviceConditions Conditions { get; }

    /// <summary>The device's own log (<see cref="DeviceLogFileName"/>): outside every account, account-free text only.</summary>
    public BlindPhoneLog DeviceLog { get; }

    /// <summary>The host with the accounts. Screens and the background tasks use <see cref="Accounts"/>.</summary>
    public BlindAccountHost Host { get; }

    /// <summary>The accounts of this phone: list, add (from an invitation or with a pairing code), rename, limits, pause, remove, repair, and each account's own controller.</summary>
    public IBlindAccountsController Accounts => Host;

    /// <summary>The ONE loop of the device: it runs only while the app is in front (iOS suspends the process otherwise) and takes the accounts one at a time.</summary>
    public BlindDeviceScheduler Scheduler { get; }

    public static IosBlindRuntime Create(IosBlindRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new IosBlindRuntime(options);
    }

    /// <summary>Takes the app's folder, adopts a 2.6 copy as account 1 (once), compares the list of accounts with the disk and starts every account. Safe to call again; see <see cref="IBlindAccountsController.StartAsync"/>.</summary>
    public Task StartAsync(CancellationToken ct = default) => Host.StartAsync(ct);

    /// <summary>True from the moment the app was asked to come to the front until it was asked to leave: its own loop serves the accounts, and a background wake has nothing to do.</summary>
    public bool InForeground => _lifecycle.InForeground;

    /// <summary>
    /// The app came to the front (start, resume): the accounts are started and the loop of the device runs. A background round that is on is asked to stop at once, and the loop starts only after it
    /// has really ended, so the loop and a background round are never inside two accounts at once. It is one transition of <see cref="IosLifecycle"/>: it runs after every older one.
    /// </summary>
    public Task StartForegroundAsync(CancellationToken ct = default) => _lifecycle.EnterForegroundAsync(ct);

    /// <summary>The app goes to the background: after every older transition the loop is stopped (a job that does not stop in time is let go of, as the loop does), and a background wake may run.</summary>
    public Task StopForegroundAsync() => _lifecycle.EnterBackgroundAsync();

    /// <summary>
    /// ONE background round at a time for the whole device, whichever of the two task identifiers iOS woke the app for (<see cref="IosLifecycle.RunBackgroundAsync"/>). A second round that begins while
    /// one is queued or on, and a round that begins while the app is open, do nothing and say so (<see cref="IosRoundOutcome.Skipped"/>; the task is completed as "nothing to do"): they never enter
    /// another account beside the one that is running, so the documented one-account-at-a-time order, the slices and the shared device budget hold.
    /// </summary>
    public Task<IosRoundOutcome> RunBackgroundAsync(bool work, CancellationToken ct, TimeProvider? time = null) =>
        _lifecycle.RunBackgroundAsync(token => work
            ? IosBackgroundRounds.RunWorkAsync(Accounts, token, time ?? _time)
            : IosBackgroundRounds.RunSyncAsync(Accounts, token, time ?? _time), ct);

    /// <summary>The watch the accounts screen runs while it is open (every 15 seconds for at most 10 minutes) over an invitation that waits for its owner.</summary>
    public BlindInvitationWatch CreateInvitationWatch() => new(Accounts, _time);

    /// <summary>
    /// Submits the two background requests again when the device holds an account that needs them (<see cref="IosBackgroundPlan"/>), withdraws them when it holds none. Called when the app
    /// goes to the background and at the start of every background wake, so that one is always pending exactly when it is wanted.
    /// </summary>
    public void RefreshBackgroundRequests()
    {
        var wanted = IosBackgroundPlan.Wanted(Accounts.GetSnapshot());
        Volatile.Write(ref _requestsWanted, wanted ? 1 : 0);
        if (wanted) _background.Submit();
        else _background.CancelAll();
    }

    /// <summary>True when the last <see cref="RefreshBackgroundRequests"/> left the requests pending.</summary>
    public bool BackgroundRequestsWanted => Volatile.Read(ref _requestsWanted) == 1;

    /// <summary>The end of the process: the lifecycle finishes what it is doing, cancels the round that is on and stops the loop; then the accounts are closed.</summary>
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.DisposeAsync().ConfigureAwait(false);
        await Scheduler.StopAsync().ConfigureAwait(false);
        await Host.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>What the lifecycle drives: this runtime's host and scheduler.</summary>
    private sealed class LoopControl(IosBlindRuntime runtime) : IIosLoop
    {
        public Task StartAccountsAsync(CancellationToken ct) => runtime.Host.StartAsync(ct);

        public void EnsureLoop() => runtime.Scheduler.EnsureScheduled();

        public Task StopLoopAsync() => runtime.Scheduler.StopAsync();

        public bool LoopRunning => runtime.Scheduler.IsRunning;
    }
}

/// <summary>Which devices need the two background requests at all.</summary>
public static class IosBackgroundPlan
{
    /// <summary>
    /// True when at least one account has something a wake could do: an invitation that waits for the owner's decision (learned at a wake), or an active account that is not held for good. A phone
    /// with no account, or only with accounts that are refused, expired, removed by their owner, being removed or paused until a person acts, asks iOS for nothing: a wake would find nothing to do.
    /// The requests are asked for again when the app goes to the background and at the end of every wake, so a pause the person lifts (the app is open then) is back in the next request.
    /// </summary>
    public static bool Wanted(BlindAccountsSnapshot snapshot) =>
        snapshot.RegistryProblem is null && snapshot.Accounts.Any(NeedsWake);

    /// <summary>
    /// An invitation that waits needs a wake. An active account does, unless it is held for good: paused by its person, or by a limit that has no end of its own (a storage limit, a limit of 0). A
    /// pause that ends by itself - a daily limit at midnight, a wait for Wi-Fi or for the battery - keeps its wake: the wake after it ends is what resumes the account.
    /// </summary>
    public static bool NeedsWake(BlindAccountSnapshot account)
    {
        var entry = account.Entry;
        if (entry.Phase is BlindAccountPhases.Pending or BlindAccountPhases.AwaitingConfirmation) return true;
        if (entry.Phase != BlindAccountPhases.Active) return false;
        if (entry.PausedByUser) return false;
        return account.Pause is not { Until: null } pause || BlindPauseText.IsWaiting(pause) || BlindPauseText.IsFault(pause);
    }
}
