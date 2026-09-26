using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private readonly Services.DesktopSettingsStore _settingsStore = new();
    private Services.AutostartService? _autostartService;
    private Services.PreventSleepService? _preventSleepService;
    private Services.DesktopUpdateController? _updates;
    // Single-instance references so re-clicking "Manage…" / "Settings…" focuses the
    // already-open window instead of stacking duplicates.
    private Avalonia.Controls.Window? _manageStoragesWindow;
    private Avalonia.Controls.Window? _settingsWindow;
    private Views.UpdateWindow? _updateWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            // Setup tray icon
            CreateTrayIcon(mainWindow, desktop);

            // Hook application exit to dispose the tray icon properly and release sleep prevention
            desktop.Exit += (s, e) =>
            {
                _trayIcon?.Dispose();
                if (OperatingSystem.IsWindows() && _preventSleepService != null)
                {
                    _preventSleepService.DisableSleepPreventionOnly();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon(MainWindow mainWindow, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://BeeMemoryBank.Desktop/Assets/icon.png"));
            var icon = new WindowIcon(stream);

            _trayIcon = new TrayIcon
            {
                ToolTipText = "BeeMemoryBank",
                Icon = icon
            };

            var menu = new NativeMenu();

            var openItem = new NativeMenuItem("Open");
            openItem.Click += (s, e) =>
            {
                Dispatcher.UIThread.Post(() => mainWindow.ShowAndFocusWindow());
            };

            // ── §4.5 Profiles submenu ────────────────────────────────────────────
            // The submenu lists every registered profile as a radio entry (checkmark on the
            // active one), plus "New...", "Add existing..." and "Manage..." commands. It is
            // rebuilt on every ActiveProfileChanged event because NativeMenu/NativeMenuItem
            // don't expose a clean way to reach into individual child items across platforms
            // to flip just their IsChecked — a full rebuild is simpler, idempotent and small
            // (single-digit items).
            var profilesItem = new NativeMenuItem("Profiles");
            var profilesMenu = new NativeMenu();
            profilesItem.Menu = profilesMenu;
            RebuildStorageMenu(profilesMenu, mainWindow);

            mainWindow.ActiveProfileChanged += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateTrayTooltip();
                    RebuildStorageMenu(profilesMenu, mainWindow);
                });
            };

            // Settings live in their own window (autostart, sleep prevention, updates); the
            // services are created here once so the window and the app share one state.
            _autostartService = new Services.AutostartService();
            if (OperatingSystem.IsWindows())
            {
                _preventSleepService = new Services.PreventSleepService(_settingsStore);
                _preventSleepService.ApplyState();
            }
            _updates = new Services.DesktopUpdateController(new Services.DesktopUpdateService(), _settingsStore);

            var settingsItem = new NativeMenuItem("Settings...");
            settingsItem.Click += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try { ShowSettingsWindow(mainWindow, desktop); }
                    catch (Exception ex) { Console.WriteLine($"Error opening settings window: {ex.Message}"); }
                });
            };

            var checkUpdatesItem = new NativeMenuItem("Check for updates...");
            checkUpdatesItem.Click += (s, e) =>
            {
                Dispatcher.UIThread.Post(() => CheckForUpdates(mainWindow, desktop));
            };

            var exitItem = new NativeMenuItem("Exit");
            exitItem.Click += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    mainWindow.RealClose();
                    desktop.Shutdown();
                });
            };

            // Update status line: "Version X" while idle (disabled), "Restart to update to Y"
            // (clickable) once a newer release has been downloaded in the background.
            var statusItem = new NativeMenuItem(_updates.StatusText) { IsEnabled = false };
            _updates.Changed += (s, e) =>
            {
                if (!_updates.IsAvailable) return;
                statusItem.Header = _updates.ReadyVersion != null
                    ? $"Restart to update to {_updates.ReadyVersion}"
                    : $"Version {_updates.CurrentVersion}";
                statusItem.IsEnabled = _updates.ReadyVersion != null;
            };
            statusItem.Click += (s, e) =>
            {
                Dispatcher.UIThread.Post(() => RestartToUpdate(mainWindow, desktop));
            };

            menu.Items.Add(openItem);
            menu.Items.Add(profilesItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(checkUpdatesItem);
            menu.Items.Add(statusItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exitItem);

            _trayIcon.Menu = menu;

            // Initial tooltip reflects the active profile (no-op when only one profile exists).
            UpdateTrayTooltip();

            // Left-clicking tray icon brings app to front
            _trayIcon.Clicked += (s, e) =>
            {
                Dispatcher.UIThread.Post(() => mainWindow.ShowAndFocusWindow());
            };

            var trayIcons = new TrayIcons { _trayIcon };
            TrayIcon.SetIcons(this, trayIcons);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating tray icon: {ex.Message}");
        }
    }

    /// <summary>Opens the update window (or brings it back) and runs a check in it.</summary>
    private void CheckForUpdates(MainWindow mainWindow, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _ = GetUpdateWindow(mainWindow, desktop).CheckAsync();
    }

    private void RestartToUpdate(MainWindow mainWindow, IClassicDesktopStyleApplicationLifetime desktop)
    {
        GetUpdateWindow(mainWindow, desktop);
        _ = RestartToUpdateAsync(mainWindow, desktop);
    }

    private Views.UpdateWindow GetUpdateWindow(MainWindow mainWindow, IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_updateWindow?.IsVisible != true)
        {
            _updateWindow = new Views.UpdateWindow(_updates!, () => RestartToUpdateAsync(mainWindow, desktop));
            var window = _updateWindow;
            window.Closed += (_, _) => { if (_updateWindow == window) _updateWindow = null; };
            // Not owned by the main window, which usually sits hidden in the tray.
            window.Show();
        }
        _updateWindow.Activate();
        return _updateWindow;
    }

    private bool _restarting;

    private async Task RestartToUpdateAsync(MainWindow mainWindow, IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_restarting || _updates?.ReadyVersion is null) return;
        _restarting = true;
        _updateWindow?.ShowApplying(_updates.ReadyVersion);
        // Let the window paint "Updating..." before the node stop blocks the UI thread.
        await Task.Delay(300);

        // Hand the open vault to the restarted app (no second login), then stop the node
        // (RealClose -> graceful stdin-EOF shutdown) so the database is closed before Velopack
        // swaps the files, then restart into the new version.
        await _updates.ApplyAndRestartAsync(
            () => Services.NodeSessionHandoff.RequestAsync(mainWindow.FrontUrl),
            mainWindow.RealClose,
            () => desktop.Shutdown());
    }

    private void ShowSettingsWindow(MainWindow mainWindow, IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_settingsWindow?.IsVisible == true)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new Views.SettingsWindow(
            mainWindow, _autostartService!, _preventSleepService, _updates!,
            () => CheckForUpdates(mainWindow, desktop),
            () => RestartToUpdate(mainWindow, desktop));
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        // Not owned by the main window: that one usually sits hidden in the tray, and an
        // owned window would be hidden together with it.
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// Rebuilds the nested "Profiles" submenu from the current profile registry. Called on
    /// app start and on every <see cref="MainWindow.ActiveProfileChanged"/> so the radio
    /// checkmark follows the live active profile.
    ///
    /// Clicking a profile item triggers <see cref="MainWindow.SwitchProfileAsync"/>; the
    /// single-flight guard inside ProfileSwitchService makes a double-click safe (the second
    /// call returns "another switch in progress").
    /// </summary>
    private void RebuildStorageMenu(NativeMenu storageMenu, MainWindow mainWindow)
    {
        storageMenu.Items.Clear();

        var profiles = mainWindow.Profiles.GetAll();
        var activeId = mainWindow.ActiveProfileId;

        foreach (var p in profiles)
        {
            var isActive = !string.IsNullOrEmpty(activeId)
                && string.Equals(p.Id, activeId, System.StringComparison.Ordinal);
            var item = new NativeMenuItem(p.Name)
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = isActive,
            };
            if (isActive)
            {
                // Disable re-selecting the active profile: SwitchProfileAsync would no-op
                // anyway, but greying it out makes the radio's "you are here" state obvious.
                item.IsEnabled = false;
            }

            // Capture locally — closures over the loop variable must take its current value.
            var targetId = p.Id;
            item.Click += (s, e) =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await mainWindow.SwitchProfileAsync(targetId);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error switching to profile '{targetId}': {ex.Message}");
                    }
                });
            };
            storageMenu.Items.Add(item);
        }

        storageMenu.Items.Add(new NativeMenuItemSeparator());

        var createItem = new NativeMenuItem("New profile...");
        createItem.Click += (s, e) =>
        {
            Dispatcher.UIThread.Post(async () =>
            {
                mainWindow.ShowAndFocusWindow();
                try { await Views.ProfileCommands.NewProfileAsync(mainWindow, mainWindow); }
                catch (Exception ex) { Console.WriteLine($"Error in new-profile dialog: {ex.Message}"); }
            });
        };
        storageMenu.Items.Add(createItem);

        var addExistingItem = new NativeMenuItem("Add existing profile...");
        addExistingItem.Click += (s, e) =>
        {
            Dispatcher.UIThread.Post(async () =>
            {
                mainWindow.ShowAndFocusWindow();
                try { await Views.ProfileCommands.AddExistingAsync(mainWindow, mainWindow); }
                catch (Exception ex) { Console.WriteLine($"Error in add-existing-profile dialog: {ex.Message}"); }
            });
        };
        storageMenu.Items.Add(addExistingItem);

        var manageItem = new NativeMenuItem("Manage profiles...");
        manageItem.Click += (s, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { ShowManageStoragesWindow(mainWindow); }
                catch (Exception ex) { Console.WriteLine($"Error opening manage-profiles window: {ex.Message}"); }
            });
        };
        storageMenu.Items.Add(manageItem);
    }

    private void ShowManageStoragesWindow(MainWindow mainWindow)
    {
        mainWindow.ShowAndFocusWindow();

        // Modal-ish but non-blocking: the user may want to keep the manage window open while
        // clicking around the tray. We track a single instance so re-opening focuses the
        // existing one rather than stacking duplicates.
        if (_manageStoragesWindow?.IsVisible == true)
        {
            _manageStoragesWindow.Activate();
            return;
        }

        _manageStoragesWindow = new Views.ManageStoragesWindow(mainWindow, mainWindow.Profiles);
        _manageStoragesWindow.Closed += (_, _) => _manageStoragesWindow = null;
        // Show(owner) sets Owner internally; we pass it positionally.
        _manageStoragesWindow.Show(mainWindow);
    }

    /// <summary>
    /// Updates <see cref="TrayIcon.ToolTipText"/> to follow §4.5: bare product name when ≤ 1
    /// profile, "BeeMemoryBank — &lt;active profile name&gt;" when ≥ 2.
    /// </summary>
    private void UpdateTrayTooltip()
    {
        if (_trayIcon == null) return;
        // mainWindow is captured via closure of CreateTrayIcon callers; but this helper can
        // also be invoked from ActiveProfileChanged which has the mainWindow in scope. We
        // resolve MainWindow through the application lifetime to avoid juggling references.
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.MainWindow is not MainWindow mw) return;

        _trayIcon.ToolTipText = Services.StorageDisplayLogic.FormatShellTitle(mw.Profiles, mw.ActiveProfileId);
    }
}