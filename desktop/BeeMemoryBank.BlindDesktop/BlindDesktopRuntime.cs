using Avalonia.Controls;
using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindDesktop.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop;

/// <summary>
/// One composition of the blind app in this process: the platform's seams, the host's seams (lifecycle, backup picker, notifications),
/// <c>AddBlindAppCore</c>, and the timer scheduler. After "Disconnect and wipe" the host throws it away and builds a new one, which is
/// the first-run state (a fresh database needs a fresh <see cref="BlindStartup"/>).
/// </summary>
public sealed class BlindDesktopRuntime : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private BlindDesktopRuntime(ServiceProvider services)
    {
        _services = services;
        App = services.GetRequiredService<IBlindAppController>();
        Scheduler = services.GetRequiredService<BlindTimerScheduler>();
        Autostart = services.GetRequiredService<IBlindAutostart>();
        Notifications = services.GetRequiredService<TrayNotifications>();
    }

    public IBlindAppController App { get; }
    public BlindTimerScheduler Scheduler { get; }
    public IBlindAutostart Autostart { get; }
    public TrayNotifications Notifications { get; }
    public IServiceProvider Services => _services;

    /// <param name="window">The top level the save-file dialog opens over.</param>
    /// <param name="returnToFirstRun">Called (from the wipe, on a pool thread; must not block) after a successful wipe.</param>
    /// <param name="displayName">How this device names itself when the identity is made; the machine name by default.</param>
    /// <param name="exporter">Where "Save to..." writes; the Avalonia save-file dialog by default (checks without a window pass their own).</param>
    public static BlindDesktopRuntime Create(IBlindDesktopPlatform platform, Func<TopLevel?> window, Action returnToFirstRun,
        TimeProvider? time = null, BlindSchedulerOptions? scheduler = null, Func<string>? displayName = null,
        IBlindBackupExporter? exporter = null)
    {
        var services = new ServiceCollection();
        platform.AddSeams(services);
        services.AddSingleton(exporter ?? new AvaloniaBackupExporter(window));
        services.AddSingleton<TrayNotifications>();
        services.AddSingleton<IBlindNotifications>(sp => sp.GetRequiredService<TrayNotifications>());
        services.AddSingleton(sp => new BlindTimerScheduler(
            sp.GetRequiredService<IBlindAppController>(), time, scheduler,
            message => BlindRunReport.Record(sp.GetRequiredService<BlindPhoneLog>(), "scheduler", message)));
        services.AddSingleton<IBlindScheduler>(sp => sp.GetRequiredService<BlindTimerScheduler>());
        services.AddSingleton<IBlindWorkRequests>(sp => sp.GetRequiredService<BlindTimerScheduler>());
        services.AddSingleton<IBlindLifecycle>(sp => new HostLifecycle(sp.GetRequiredService<BlindTimerScheduler>(), returnToFirstRun));

        BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(
            platform.Paths.DataDirectory, platform.Paths.DatabasePath, time, displayName ?? DefaultDisplayName));
        return new BlindDesktopRuntime(services.BuildServiceProvider());
    }

    /// <summary>Opens the database and makes the identity if there is none, then starts the scheduler (its first pass is the check at start).</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await App.InitializeAsync(ct);
        Scheduler.EnsureScheduled();
    }

    public async ValueTask DisposeAsync()
    {
        await Scheduler.StopAsync();
        await _services.DisposeAsync();
    }

    /// <summary>The computer's name, cut to what a blind device name may hold (the pairing code carries it).</summary>
    public static string DefaultDisplayName()
    {
        var name = new string(Environment.MachineName.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) return "Blind copy";
        return name.Length <= BeeMemoryBank.Core.Models.BlindPhoneCode.MaxDisplayNameLength
            ? name
            : name[..BeeMemoryBank.Core.Models.BlindPhoneCode.MaxDisplayNameLength];
    }

}
