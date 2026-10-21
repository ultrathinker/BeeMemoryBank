using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindDesktop.Services;
using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindDesktop.Views;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop;

/// <summary>
/// The UI side of the app: owns the window, the tray icon and the current <see cref="BlindDesktopRuntime"/>, and answers the tray menu.
/// Everything that is a rule of the blind app is in AppCore; this only wires the screen to it.
/// </summary>
public sealed class BlindDesktopShell : ITrayActions
{
    private readonly Application _application;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly StartupOptions _options;
    private readonly IBlindDesktopPlatform _platform;
    private readonly IInstanceGuard? _instance;
    private MainWindow? _window;
    private TrayController? _tray;
    private BlindDesktopRuntime? _runtime;
    private MainViewModel? _viewModel;
    private bool _quitting;
    private int _trayRefreshPending;

    public BlindDesktopShell(Application application, IClassicDesktopStyleApplicationLifetime lifetime, StartupOptions options,
        IBlindDesktopPlatform platform, IInstanceGuard? instance)
    {
        _application = application;
        _lifetime = lifetime;
        _options = options;
        _platform = platform;
        _instance = instance;
    }

    /// <summary>Builds the window and the tray, opens the app and starts the device loop. A normal start shows the window; <c>--minimized</c> starts in the tray, except on the very first run.</summary>
    public async Task StartAsync()
    {
        var firstRun = !File.Exists(_platform.Paths.DatabasePath) && !File.Exists(Path.Combine(_platform.Paths.DataDirectory, BlindAccountRegistry.FileName));
        _window = new MainWindow();
        _tray = new TrayController(_application, this, _platform.TrayIconAsset, _platform.TrayIconIsTemplate);
        _instance?.Listen(() => Dispatcher.UIThread.Post(Open));

        await StartRuntimeAsync();
        if (!_options.Minimized || firstRun) Open();
    }

    private async Task StartRuntimeAsync()
    {
        var runtime = BlindDesktopRuntime.Create(_platform, () => _window);
        _runtime = runtime;
        _viewModel = new MainViewModel(runtime.Accounts, runtime.WorkFor, runtime.Autostart, QrCodePng.Create,
            action => Dispatcher.UIThread.Post(action), ErrorLog.Write, _platform.StatusAreaName, StoreOf(runtime), watch: runtime.Watch);
        _viewModel.QuitRequested += Quit;
        _window!.DataContext = _viewModel;
        runtime.Accounts.Changed += RequestTrayRefresh;

        await runtime.StartAsync();
        await _viewModel.RefreshAsync();
        RequestTrayRefresh();
    }

    /// <summary>What an account's copy holds, for the store line of its panel; nothing when the account is not up (the line stays "unknown").</summary>
    private static Func<string, CancellationToken, Task<BlindStoreInfo?>> StoreOf(BlindDesktopRuntime runtime) => async (id, ct) =>
    {
        try { return await runtime.Accounts.ServicesFor(id).GetRequiredService<BlindStoreStats>().GetAsync(ct); }
        catch (KeyNotFoundException) { return null; }
    };

    private void RequestTrayRefresh()
    {
        if (_quitting || Interlocked.Exchange(ref _trayRefreshPending, 1) == 1) return;
        _ = Task.Run(() =>
        {
            Interlocked.Exchange(ref _trayRefreshPending, 0);
            var runtime = _runtime;
            if (runtime is null) return;
            try
            {
                // The lines follow the accounts: one that was removed takes its line with it.
                var snapshot = runtime.Notifications.Reconcile(runtime.Accounts.GetSnapshot);
                var view = BlindAccountsView.Build(snapshot, TimeZoneInfo.Local, DateTimeOffset.UtcNow);
                var text = AccountsTooltip.For(view, runtime.Notifications.Line);
                Dispatcher.UIThread.Post(() => ShowToolTip(text));
            }
            catch (Exception ex)
            {
                // The tooltip is presentation only; a failure to build it never affects a job.
                ErrorLog.Write("Building the tray tooltip failed", ex);
            }
        });
    }

    /// <summary>On the UI thread: a tray icon that was already disposed must not turn into an unhandled UI exception.</summary>
    private void ShowToolTip(string text)
    {
        try
        {
            _tray?.SetToolTip(text);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Setting the tray tooltip failed", ex);
        }
    }

    // ---- tray menu ----------------------------------------------------------------------------------------------------------

    public void Open()
    {
        if (_quitting) return; // a window shown while the app is stopping would vanish with it
        _window?.ShowAndFocus();
    }

    /// <summary>"Sync now" for every active account: the device loop runs them one at a time, the one that waited longest first.</summary>
    public void SyncNow()
    {
        if (_runtime is not { } runtime) return;
        foreach (var id in ActiveAccountIds(runtime, pairedOnly: false)) runtime.Scheduler.RequestSync(id);
    }

    /// <summary>"Back up now" for every account that is paired.</summary>
    public void BackupNow()
    {
        if (_runtime is not { } runtime) return;
        foreach (var id in ActiveAccountIds(runtime, pairedOnly: true)) runtime.Scheduler.RequestBackup(id);
    }

    public void AddAccount()
    {
        Open();
        if (_viewModel?.AddAccountCommand is { } command && command.CanExecute(null)) command.Execute(null);
    }

    public void OpenSettings() => Open();

    private static IEnumerable<string> ActiveAccountIds(BlindDesktopRuntime runtime, bool pairedOnly) =>
        runtime.Accounts.GetSnapshot().Accounts
            .Where(a => a.Entry.Phase == BlindAccountPhases.Active && a.Status is not null && (!pairedOnly || a.Status.IsPaired))
            .Select(a => a.Entry.Id)
            .ToList();

    /// <summary>Explicit Quit: pauses a running job, stops the scheduler, removes the tray icon and ends the process.</summary>
    public void Quit() => _ = QuitAsync();

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        try
        {
            if (_window is not null) _window.Quitting = true;
            _viewModel?.Dispose();
            if (_runtime is not null) await _runtime.DisposeAsync();
        }
        catch (Exception ex)
        {
            // Whatever went wrong while stopping, the app still ends: Quit is the one thing that must work.
            ErrorLog.Write("Stopping the app cleanly failed", ex);
        }
        finally
        {
            try { _tray?.Dispose(); }
            catch (Exception ex) { ErrorLog.Write("Removing the tray icon failed", ex); }
            try { _window?.Close(); }
            catch (Exception ex) { ErrorLog.Write("Closing the window failed", ex); }
            // The work on the data folder has stopped: let go of the one-copy guard now, not when the process is gone, so that a start the
            // person made in the meantime (it waits for this, see Program.DefaultHandOverWait) becomes the app instead of ending.
            try { _instance?.Dispose(); }
            catch (Exception ex) { ErrorLog.Write("Releasing the one-copy guard failed", ex); }
            _lifetime.Shutdown();
        }
    }
}
