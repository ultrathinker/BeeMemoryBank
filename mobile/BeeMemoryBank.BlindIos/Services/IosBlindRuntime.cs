using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>What iOS can be asked about background time: submit the two background requests again, or withdraw them.</summary>
public interface IIosBackgroundRequests
{
    /// <summary>Asks iOS for the next sync round and the next long-job window (a request with the same identifier replaces the pending one).</summary>
    void Submit();

    /// <summary>Withdraws both requests (Disconnect and wipe).</summary>
    void CancelAll();
}

/// <summary>What one composition of the blind copy is built from; the iOS parts are given by the app, fakes by the tests.</summary>
/// <param name="Paths">The data folder (the app's container) and the database in it.</param>
/// <param name="Secrets">The Keychain store; made once per composition.</param>
/// <param name="Background">The BGTaskScheduler requests.</param>
/// <param name="DisplayName">How this phone names itself when its identity is made.</param>
/// <param name="Scheduler">Timings of the in-app loop; null: the desktop copies' (15-minute sync, hourly long job).</param>
public sealed record IosBlindRuntimeOptions(
    IBlindPaths Paths,
    Func<IBlindSecretStore> Secrets,
    IIosBackgroundRequests Background,
    Func<string>? DisplayName = null,
    TimeProvider? Time = null,
    BlindSchedulerOptions? Scheduler = null);

/// <summary>
/// One composition of the blind copy in this process: the iOS seams, <c>AddBlindAppCore</c> and the in-app timer loop - the iOS counterpart
/// of the desktop copies' runtime. "Disconnect and wipe" ends with a NEW composition (<see cref="IosBlindHost"/>): iOS gives an app no way
/// to restart itself, and a fresh database needs a fresh start-up gate, so the first-run state is a new runtime in the same process.
/// </summary>
public sealed class IosBlindRuntime : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private IosBlindRuntime(ServiceProvider services)
    {
        _services = services;
        App = services.GetRequiredService<IBlindAppController>();
        Scheduler = services.GetRequiredService<BlindTimerScheduler>();
        Stats = services.GetRequiredService<BlindReplicaStats>();
        Activity = services.GetRequiredService<BlindActivity>();
        Paths = services.GetRequiredService<IBlindPaths>();
    }

    public IBlindAppController App { get; }

    /// <summary>The in-app loop: it runs only while the app is in front (iOS suspends the process otherwise).</summary>
    public BlindTimerScheduler Scheduler { get; }

    public BlindReplicaStats Stats { get; }
    public BlindActivity Activity { get; }
    public IBlindPaths Paths { get; }
    public IServiceProvider Services => _services;

    /// <param name="returnToFirstRun">Called by a successful wipe (on a pool thread, must not block): the host builds the next runtime.</param>
    public static IosBlindRuntime Create(IosBlindRuntimeOptions options, Action returnToFirstRun)
    {
        ArgumentNullException.ThrowIfNull(options);
        var services = new ServiceCollection();
        services.AddSingleton(options.Paths);
        services.AddSingleton(options.Background);
        services.AddSingleton(_ => options.Secrets());
        services.AddSingleton<IBlindStateStore>(_ => new MacOsBlindStateStore(Path.Combine(options.Paths.DataDirectory, MacOsBlindStateStore.FileName)));
        services.AddSingleton(sp => new BlindTimerScheduler(
            sp.GetRequiredService<IBlindAppController>(), options.Time, options.Scheduler,
            message => BlindRunReport.Record(sp.GetRequiredService<BlindPhoneLog>(), "scheduler", message)));
        services.AddSingleton<IBlindScheduler>(sp => sp.GetRequiredService<BlindTimerScheduler>());
        services.AddSingleton<IBlindLifecycle>(sp => new IosBlindLifecycle(
            sp.GetRequiredService<BlindTimerScheduler>(), options.Background, returnToFirstRun));

        BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(
            options.Paths.DataDirectory, options.Paths.DatabasePath, options.Time, options.DisplayName));
        return new IosBlindRuntime(services.BuildServiceProvider());
    }

    public async ValueTask DisposeAsync()
    {
        await Scheduler.StopAsync();
        await _services.DisposeAsync();
    }
}

/// <summary>
/// The blind copy's life around "Disconnect and wipe" on iOS. Before the wipe the in-app loop stops (a running job pauses) and the
/// background requests are withdrawn, so that nothing holds the database or the keys while they go; after a successful wipe the host
/// builds the first-run composition. There is no backup service on iOS: backups run in the same loop, which is already stopped.
/// </summary>
public sealed class IosBlindLifecycle(BlindTimerScheduler scheduler, IIosBackgroundRequests background, Action returnToFirstRun) : IBlindLifecycle
{
    public void StopBackgroundWork()
    {
        scheduler.Cancel();
        background.CancelAll();
    }

    public void StopBackupService() => scheduler.Cancel();

    /// <summary>The wipe gave up and deleted nothing: the copy goes on as before.</summary>
    public void ResumeBackgroundWork()
    {
        scheduler.EnsureScheduled();
        background.Submit();
    }

    public void RestartAfterWipe() => returnToFirstRun();
}

/// <summary>
/// Holds the current <see cref="IosBlindRuntime"/> of the app. The screen and the background handlers always ask it for the current one,
/// because a wipe replaces it.
/// </summary>
public sealed class IosBlindHost
{
    private readonly Func<Action, IosBlindRuntime> _create;
    private readonly TimeSpan _disposeDelay;
    private IosBlindRuntime _current;

    /// <param name="create">Builds a runtime; its argument is what that runtime calls after a successful wipe.</param>
    /// <param name="disposeDelay">How long the replaced runtime lives on, so that the wipe call that replaced it can return through it.</param>
    public IosBlindHost(Func<Action, IosBlindRuntime> create, TimeSpan? disposeDelay = null)
    {
        _create = create;
        _disposeDelay = disposeDelay ?? TimeSpan.FromSeconds(2);
        _current = create(ReturnToFirstRun);
    }

    public IosBlindRuntime Current => Volatile.Read(ref _current);

    /// <summary>Raised (on a pool thread) after a wipe put a new runtime in place.</summary>
    public event Action? Replaced;

    private void ReturnToFirstRun()
    {
        var next = _create(ReturnToFirstRun);
        var old = Interlocked.Exchange(ref _current, next);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_disposeDelay);
                await old.DisposeAsync();
            }
            catch (Exception)
            {
                // the old composition is gone either way; nothing of the new one depends on it
            }
        });
        try { Replaced?.Invoke(); }
        catch (Exception) { /* a screen that failed to rebind refreshes on its own timer */ }
    }
}
