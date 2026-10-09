using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindDesktop.Services;
using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindDesktop.Views;
using BeeMemoryBank.BlindMobile.Services.Blind;

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

    /// <summary>Builds the window and the tray, opens the app and starts the scheduler. A normal start shows the window; <c>--minimized</c> starts in the tray, except on the very first run.</summary>
    public async Task StartAsync()
    {
        var firstRun = !File.Exists(_platform.Paths.DatabasePath);
        _window = new MainWindow();
        _tray = new TrayController(_application, this, _platform.TrayIconAsset, _platform.TrayIconIsTemplate);
        _instance?.Listen(() => Dispatcher.UIThread.Post(Open));

        await StartRuntimeAsync();
        if (!_options.Minimized || firstRun) Open();
    }

    private async Task StartRuntimeAsync()
    {
        var runtime = BlindDesktopRuntime.Create(_platform, () => _window, () => Dispatcher.UIThread.Post(ReturnToFirstRun));
        _runtime = runtime;
        _viewModel = new MainViewModel(runtime.App, runtime.Scheduler, runtime.Autostart, QrCodePng.Create,
            action => Dispatcher.UIThread.Post(action), ErrorLog.Write, _platform.StatusAreaName);
        _viewModel.QuitRequested += Quit;
        _window!.DataContext = _viewModel;
        runtime.App.Changed += RequestTrayRefresh;

        await runtime.StartAsync();
        await _viewModel.RefreshAsync();
        RequestTrayRefresh();
    }

    /// <summary>After a successful wipe: a new composition over the now empty folder, which is the first-run state.</summary>
    private void ReturnToFirstRun()
    {
        if (_quitting) return;
        _ = ReturnToFirstRunAsync();
    }

    private async Task ReturnToFirstRunAsync()
    {
        try
        {
            var oldRuntime = _runtime;
            _viewModel?.Dispose();
            _window!.DataContext = null;
            if (oldRuntime is not null) await oldRuntime.DisposeAsync();
            await StartRuntimeAsync();
            Open();
        }
        catch (Exception ex)
        {
            // The wipe itself succeeded; say that the fresh start did not, and let the next start of the app do it.
            _tray?.SetToolTip("Wiped. Start the app again to set it up.");
            ErrorLog.Write("Could not return to the first-run state", ex);
        }
    }

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
                var status = runtime.App.GetStatus();
                if (status.ActiveJob is { } job) runtime.Notifications.Show(job, status.JobProgress ?? 0);
                else runtime.Notifications.Clear();
                var text = TooltipFor(status, runtime.Notifications.Line);
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

    internal static string TooltipFor(BlindAppStatus status, string? jobLine)
    {
        const string name = "Bee Memory Bank - blind copy";
        if (jobLine is not null) return $"{name}: {jobLine}";
        if (status.StartError is not null) return $"{name}: needs attention - open to see";
        if (!status.IsPaired) return $"{name}: not paired yet - open to pair";
        return status.LastSyncAt is { } at ? $"{name}: last sync {at.ToLocalTime():dd.MM HH:mm}" : $"{name}: paired";
    }

    // ---- tray menu ----------------------------------------------------------------------------------------------------------

    public void Open()
    {
        if (_quitting) return; // a window shown while the app is stopping would vanish with it
        _window?.ShowAndFocus();
    }

    public void SyncNow() => _runtime?.Scheduler.RequestSync();

    public void BackupNow() => _runtime?.Scheduler.RequestBackup();

    public void RePair()
    {
        Open();
        if (_viewModel?.RePairCommand is { } command && command.CanExecute(null)) command.Execute(null);
    }

    public void OpenSettings() => Open();

    public void Wipe()
    {
        Open();
        _viewModel?.OpenWipeCommand.Execute(null);
    }

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
