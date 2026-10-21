using BeeMemoryBank.BlindMobile.Platforms.Android;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// The Android head of the blind app as ONE object per process: the account host (every account's own runtime) and the device's two schedulers. The WorkManager workers, the pages and the
/// backup service all reach the accounts through this and nothing else, so there is never a second host on the app's folder.
///
/// <para>Two schedulers over one host because Android gives sync rounds and long jobs separate jobs: the short periodic worker runs <see cref="SyncScheduler"/> (a round for every account, in
/// the order the device scheduler chooses), the foreground worker runs <see cref="HeavyScheduler"/> (a first load, a due backup). Their rhythm is the WorkManager period, so both look at every
/// account at every pass (<see cref="BlindDeviceSchedulerOptions.SyncEvery"/> zero); what the guard says about a limit, a switch or a pause is the same for both.</para>
/// </summary>
public sealed class BlindDevice : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    private Action<BlindWorkRequirements>? _ensure;
    private Action? _heavyNow;
    private TimeSpan _quiet = TimeSpan.FromSeconds(1);
    private bool _watching;
    private int _queued;

    public BlindDevice(BlindAccountHostOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        Host = new BlindAccountHost(options);
        Log = new BlindPhoneLog(Path.Combine(options.RootDirectory, DeviceLogFile), time ?? TimeProvider.System);
        SyncScheduler = new BlindDeviceScheduler(Host, time, SyncOptions, Report);
        HeavyScheduler = new BlindDeviceScheduler(Host, time, HeavyOptions, Report);
    }

    /// <summary>The scheduler's own problems (never an account's data) go here, in the root next to the registry; the Accounts page shows the last one.</summary>
    public const string DeviceLogFile = "device-log.jsonl";

    public static BlindDeviceSchedulerOptions SyncOptions { get; } = new() { RunHeavy = false, SyncEvery = TimeSpan.Zero };

    public static BlindDeviceSchedulerOptions HeavyOptions { get; } = new() { RunSync = false, HeavyEvery = TimeSpan.Zero };

    public BlindAccountHost Host { get; }

    public IBlindAccountsController Accounts => Host;

    public BlindDeviceScheduler SyncScheduler { get; }

    public BlindDeviceScheduler HeavyScheduler { get; }

    public BlindPhoneLog Log { get; }

    /// <summary>The newest problem of the device's schedulers, or null.</summary>
    public string? LastProblem => SyncScheduler.LastError ?? HeavyScheduler.LastError;

    /// <summary>Opens the registry and starts every account (once; later calls return at once). Any entry point that uses an account's database calls this first: it is what makes each account open its database before use.</summary>
    public Task StartAsync(CancellationToken ct = default) => Host.StartAsync(ct);

    /// <summary>What the periodic requests should wait for, from the accounts as they are now.</summary>
    public BlindWorkRequirements Requirements() => BlindWorkPlan.For(Host.GetSnapshot().Accounts.Select(a => a.Entry));

    /// <summary>
    /// Asks the long-job scheduler for the first load of every account that has been paired and has none (an invitation that was just confirmed; a call code just accepted). Each account is asked once
    /// until it is seen without the need; returns true when a new ask was made (the caller then enqueues the long job at once, instead of waiting for the hourly slot).
    /// </summary>
    public bool RequestFirstLoads()
    {
        var needing = Host.GetSnapshot().Accounts
            .Where(a => a.Entry.Phase == BlindAccountPhases.Active && a.Status is { IsPaired: true, InitialLoadDone: false, ActiveJob: null, StartError: null })
            .Select(a => a.Entry.Id)
            .ToList();
        var asked = false;
        lock (_gate)
        {
            _asked.IntersectWith(needing);
            foreach (var id in needing)
            {
                if (!_asked.Add(id)) continue;
                HeavyScheduler.RequestHeavy(id);
                asked = true;
            }
        }
        return asked;
    }

    /// <summary>
    /// Keeps the platform's jobs in step with the accounts: calls <paramref name="ensure"/> with what the periodic requests should wait for, and <paramref name="heavyNow"/> when an account needs its first load at
    /// once, now and (after a short quiet time, because a running job raises changes all the time) after every change of the accounts. Both are called off the UI thread and may be called with nothing new:
    /// the platform decides whether anything is to be done.
    /// </summary>
    public void Watch(Action<BlindWorkRequirements> ensure, Action heavyNow, TimeSpan? quiet = null)
    {
        lock (_gate)
        {
            _ensure = ensure;
            _heavyNow = heavyNow;
            _quiet = quiet ?? TimeSpan.FromSeconds(1);
            if (!_watching)
            {
                _watching = true;
                Host.Changed += OnChanged;
            }
        }
        Apply();
    }

    private void OnChanged()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(_quiet); }
            finally { Volatile.Write(ref _queued, 0); }
            Apply();
        });
    }

    private void Apply()
    {
        Action<BlindWorkRequirements>? ensure;
        Action? heavyNow;
        lock (_gate)
        {
            ensure = _ensure;
            heavyNow = _heavyNow;
        }
        if (ensure is null || heavyNow is null) return;
        try
        {
            // A damaged registry starts no account: the periodic jobs are still queued (they find nothing to do) and no long job is asked for.
            if (Host.GetSnapshot().RegistryProblem is not null) ensure(BlindWorkRequirements.Default);
            else
            {
                ensure(Requirements());
                if (RequestFirstLoads()) heavyNow();
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            Report($"Keeping the background jobs in step failed ({ex.GetType().Name}).");
        }
    }

    private void Report(string sentence)
    {
        try { Log.Add("device", sentence); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the log is a convenience; the scheduler's LastError still holds it */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_watching) Host.Changed -= OnChanged;
        await SyncScheduler.DisposeAsync();
        await HeavyScheduler.DisposeAsync();
        await Host.DisposeAsync();
    }
}

/// <summary>How the Android head composes the account host: the names of its keys and state, the phone's switches, the courtesy to the source. No Android type in it, so the tests build the same thing over software keys.</summary>
public static class BlindAndroidHead
{
    /// <param name="root">The app's private root (LocalApplicationData): <c>accounts.json</c>, <c>accounts/</c> and the 2.6 install's files.</param>
    /// <param name="overrides">Tests only: services that replace what an account's composition registered (the network side behind a fake).</param>
    public static BlindAccountHostOptions HostOptions(string root, IBlindKeyWrap keys, Func<string, PreferencesBlindStore> preferences, IBlindDeviceConditions conditions,
        Func<string> displayNameFactory, IBlindAccountLifecycle? lifecycle = null, TimeProvider? time = null,
        Action<Microsoft.Extensions.DependencyInjection.IServiceCollection, BlindAccountEntry>? overrides = null) => new()
    {
        RootDirectory = root,
        // An install of 2.6 keeps its database where it was: that is the first account.
        LegacyDatabasePath = Path.Combine(root, "beememorybank.db"),
        Secrets = new KeystoreBlindSecretStoreFactory(root, keys),
        States = new PreferencesBlindStateStoreFactory(preferences),
        Conditions = conditions,
        DisplayNameFactory = displayNameFactory,
        Lifecycle = lifecycle,
        Time = time ?? TimeProvider.System,
        ConfigureOverrides = overrides,
        // A phone: an invited account starts on unmetered networks only and pauses on battery saver.
        IsPhone = true,
    };
}
