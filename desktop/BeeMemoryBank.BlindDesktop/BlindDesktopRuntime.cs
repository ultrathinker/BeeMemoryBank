using Avalonia.Controls;
using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindDesktop.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop;

/// <summary>
/// One composition of the blind app in this process: the platform's per-account stores and conditions, the host's seams (backup picker, notifications) and ONE
/// <see cref="BlindAccountHost"/> that builds the single-mesh blind app once for every account on the device (<c>AddBlindAppCore</c> per account), plus the one
/// device loop (<see cref="BlindDeviceScheduler"/>) that syncs them in turn. An install of 2.6 is account 1 in the root folder, with its names unchanged; an empty root has
/// no account and the window opens on "Add account".
/// </summary>
public sealed class BlindDesktopRuntime : IAsyncDisposable
{
    private readonly BlindAccountHost _host;

    private BlindDesktopRuntime(BlindAccountHost host, BlindDeviceScheduler scheduler, IBlindDesktopPlatform platform, AccountNotifications notifications,
        BlindInvitationWatch watch, TimeProvider time)
    {
        _host = host;
        Scheduler = scheduler;
        Autostart = platform.Autostart;
        Conditions = platform.DeviceConditions;
        Notifications = notifications;
        Watch = watch;
        Time = time;
    }

    /// <summary>The accounts of this device: list them, add one, set limits, remove one, reach one account's own controller.</summary>
    public IBlindAccountsController Accounts => _host;

    /// <summary>The one loop of the device: one account at a time, the one that synced longest ago first.</summary>
    public BlindDeviceScheduler Scheduler { get; }

    public IBlindAutostart Autostart { get; }

    /// <summary>What the device says about its network and its power; the account switches ask it.</summary>
    public IBlindDeviceConditions Conditions { get; }

    /// <summary>The line of the tray tooltip while an account runs a long job.</summary>
    public AccountNotifications Notifications { get; }

    /// <summary>Asks about invitations that wait for their owner while the window is open (every 15 seconds for 10 minutes); the scheduler asks at its own passes.</summary>
    public BlindInvitationWatch Watch { get; }

    public TimeProvider Time { get; }

    /// <summary>The work an account's panel asks for ("Sync now", "Back up now", the first load after pairing), served by the device loop.</summary>
    public IBlindWorkRequests WorkFor(string accountId) => new AccountWorkRequests(Scheduler, accountId, SchedulerOptions);

    internal BlindDeviceSchedulerOptions SchedulerOptions { get; private init; } = new();

    /// <param name="window">The top level the save-file dialog opens over.</param>
    /// <param name="displayName">How this device names itself when an account's identity is made; the machine name by default.</param>
    /// <param name="exporter">Where "Save to..." writes; the Avalonia save-file dialog by default (checks without a window pass their own).</param>
    /// <param name="primaryHandler">Tests only: the handler at the bottom of an account's HTTP pipeline, in place of the pinned or public-CA socket handler (see <see cref="BlindAccountHostOptions.PrimaryHandlerFactory"/>).</param>
    public static BlindDesktopRuntime Create(IBlindDesktopPlatform platform, Func<TopLevel?> window, TimeProvider? time = null,
        BlindDeviceSchedulerOptions? scheduler = null, Func<string>? displayName = null, IBlindBackupExporter? exporter = null,
        Func<BlindAccountEntry, Func<HttpMessageHandler>?>? primaryHandler = null)
    {
        time ??= TimeProvider.System;
        var notifications = new AccountNotifications();
        var backupExporter = exporter ?? new AvaloniaBackupExporter(window);
        var options = new BlindAccountHostOptions
        {
            RootDirectory = platform.Paths.DataDirectory,
            LegacyDatabasePath = platform.Paths.DatabasePath,
            Secrets = platform.SecretStores,
            States = platform.StateStores,
            Time = time,
            DisplayNameFactory = displayName ?? DefaultDisplayName,
            Conditions = platform.DeviceConditions,
            // A computer: an invited account starts with its limits but without the two phone switches (a PC on Ethernet is allowed; a person who wants them turns them on).
            IsPhone = false,
            PrimaryHandlerFactory = primaryHandler,
            ConfigureHostSeams = (services, account) =>
            {
                services.AddSingleton(backupExporter);
                services.AddSingleton(notifications.For(account.Id));
                services.AddSingleton<IBlindNotifications>(sp => sp.GetRequiredService<TrayNotifications>());
            },
        };

        var host = new BlindAccountHost(options);
        var schedulerOptions = scheduler ?? new BlindDeviceSchedulerOptions();
        var loop = new BlindDeviceScheduler(host, time, schedulerOptions, message => ErrorLog.Note("The device loop: " + message));
        return new BlindDesktopRuntime(host, loop, platform, notifications, new BlindInvitationWatch(host, time), time) { SchedulerOptions = schedulerOptions };
    }

    /// <summary>Opens the accounts (a damaged list or a repair case is not an exception: the window says so) and starts the device loop, whose first pass is the check at start.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _host.StartAsync(ct);
        Scheduler.EnsureScheduled();
    }

    public async ValueTask DisposeAsync()
    {
        await Watch.DisposeAsync();
        await Scheduler.StopAsync();
        await _host.DisposeAsync();
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
