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
    private DispatcherTimer? _updateTimer;
    private Services.PreventSleepService? _preventSleepService;
    // Single-instance reference for the manage-storages window so re-clicking "Manage…"
    // focuses the already-open window instead of stacking duplicates.
    private Avalonia.Controls.Window? _manageStoragesWindow;

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

            // ── §4.5 Storage submenu ─────────────────────────────────────────────
            // The submenu lists every registered profile as a radio entry (checkmark on the
            // active one), plus "Create..." and "Manage..." commands. It is rebuilt on every
            // ActiveProfileChanged event because NativeMenu/NativeMenuItem don't expose a
            // clean way to reach into individual child items across platforms to flip just
            // their IsChecked — a full rebuild is simpler, idempotent and small (single-digit
            // items).
            var storageItem = new NativeMenuItem("Хранилище");
            var storageMenu = new NativeMenu();
            storageItem.Menu = storageMenu;
            RebuildStorageMenu(storageMenu, mainWindow);

            mainWindow.ActiveProfileChanged += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateTrayTooltip();
                    RebuildStorageMenu(storageMenu, mainWindow);
                });
            };

            var autostartService = new Services.AutostartService();
            var autostartItem = new NativeMenuItem("Autostart")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = autostartService.IsEnabled
            };

            NativeMenuItem? preventSleepItem = null;
            if (OperatingSystem.IsWindows())
            {
                _preventSleepService = new Services.PreventSleepService();
                _preventSleepService.ApplyState();

                preventSleepItem = new NativeMenuItem("Prevent sleep")
                {
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = _preventSleepService.IsEnabled
                };
                preventSleepItem.Click += (s, e) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (OperatingSystem.IsWindows() && _preventSleepService != null)
                            {
                                _preventSleepService.IsEnabled = !_preventSleepService.IsEnabled;
                                preventSleepItem.IsChecked = _preventSleepService.IsEnabled;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error toggling prevent sleep: {ex.Message}");
                        }
                    });
                };
            }
            autostartItem.Click += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (autostartService.IsEnabled)
                        {
                            autostartService.Disable();
                        }
                        else
                        {
                            autostartService.Enable();
                        }
                        autostartItem.IsChecked = autostartService.IsEnabled;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error toggling autostart: {ex.Message}");
                    }
                });
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

            // Desktop self-update from published GitHub releases (see DesktopUpdateService).
            var updates = new Services.DesktopUpdateService();
            var checkItem = new NativeMenuItem("Check for updates") { IsEnabled = updates.IsAvailable };
            var statusItem = new NativeMenuItem(updates.IsAvailable
                ? $"Version {updates.CurrentVersion}"
                : "Updates: not an installed build") { IsEnabled = false };

            async Task RunUpdateCheckAsync(bool interactive)
            {
                if (!updates.IsAvailable) return;
                checkItem.IsEnabled = false;
                if (interactive) statusItem.Header = "Updates: checking...";
                try
                {
                    var ready = await updates.CheckAndDownloadAsync();
                    if (ready is not null)
                    {
                        statusItem.Header = $"Restart to update to {ready}";
                        statusItem.IsEnabled = true;
                    }
                    else if (interactive)
                    {
                        statusItem.Header = $"Up to date ({updates.CurrentVersion})";
                    }
                }
                catch (Exception ex)
                {
                    if (interactive) statusItem.Header = "Updates: check failed";
                    Console.WriteLine($"Update check failed: {ex.Message}");
                }
                finally
                {
                    checkItem.IsEnabled = true;
                }
            }

            checkItem.Click += (s, e) => Dispatcher.UIThread.Post(async () => await RunUpdateCheckAsync(interactive: true));

            statusItem.Click += (s, e) =>
            {
                if (updates.ReadyVersion is null) return;
                Dispatcher.UIThread.Post(() =>
                {
                    // Stop the node first (RealClose -> graceful stdin-EOF shutdown) so the
                    // database is closed before Velopack swaps the files, then restart into it.
                    mainWindow.RealClose();
                    try
                    {
                        updates.ApplyAndRestart();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Applying update failed: {ex.Message}");
                        desktop.Shutdown();
                    }
                });
            };

            // Quiet background check: shortly after start, then daily. Only downloads; the user
            // chooses when to restart.
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            _updateTimer.Tick += async (s, e) =>
            {
                _updateTimer.Interval = TimeSpan.FromHours(24);
                await RunUpdateCheckAsync(interactive: false);
            };
            if (updates.IsAvailable) _updateTimer.Start();

            menu.Items.Add(openItem);
            menu.Items.Add(storageItem);
            menu.Items.Add(autostartItem);
            if (preventSleepItem != null)
            {
                menu.Items.Add(preventSleepItem);
            }
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(checkItem);
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

    /// <summary>
    /// Rebuilds the nested "Хранилище" submenu from the current profile registry. Called on
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

        var createItem = new NativeMenuItem("Создать хранилище…");
        createItem.Click += (s, e) =>
        {
            Dispatcher.UIThread.Post(async () =>
            {
                try { await ShowCreateStorageDialogAsync(mainWindow); }
                catch (Exception ex) { Console.WriteLine($"Error in create-storage dialog: {ex.Message}"); }
            });
        };
        storageMenu.Items.Add(createItem);

        var manageItem = new NativeMenuItem("Управление хранилищами…");
        manageItem.Click += (s, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { ShowManageStoragesWindow(mainWindow); }
                catch (Exception ex) { Console.WriteLine($"Error opening manage-storages window: {ex.Message}"); }
            });
        };
        storageMenu.Items.Add(manageItem);
    }

    private async System.Threading.Tasks.Task ShowCreateStorageDialogAsync(MainWindow mainWindow)
    {
        mainWindow.ShowAndFocusWindow();

        // ShowDialog(owner) sets Owner internally; no direct assignment here.
        var dialog = new Views.CreateStorageDialog(mainWindow.Profiles);
        var ok = await dialog.ShowDialog<bool?>(mainWindow);
        if (ok != true || dialog.CreatedProfile == null) return;

        // Newly-created profile → switch to it like a normal target. The empty data dir will
        // surface the existing /Setup wizard, nothing else to do here.
        try
        {
            await mainWindow.SwitchProfileAsync(dialog.CreatedProfile.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error switching to newly created profile: {ex.Message}");
        }
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