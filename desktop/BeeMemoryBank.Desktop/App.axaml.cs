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
    private Services.PreventSleepService? _preventSleepService;

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

            var checkItem = new NativeMenuItem("Check for updates");
            var statusItem = new NativeMenuItem("Updates: Unknown") { IsEnabled = false };

            checkItem.Click += (s, e) =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    checkItem.IsEnabled = false;
                    statusItem.Header = "Updates: Checking...";

                    var frontUrl = mainWindow.FrontUrl;
                    if (string.IsNullOrEmpty(frontUrl))
                    {
                        statusItem.Header = "Updates: Node not ready";
                        checkItem.IsEnabled = true;
                        return;
                    }

                    try
                    {
                        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                        var dataDir = BeeMemoryBank.AppPaths.BmbPaths.DefaultVaultDir;
                        var keyFile = Path.Combine(dataDir, ".internal-key");
                        var key = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
                        if (string.IsNullOrEmpty(key) && File.Exists(keyFile))
                        {
                            key = File.ReadAllText(keyFile).Trim();
                        }

                        var request = new HttpRequestMessage(HttpMethod.Post, $"{frontUrl.TrimEnd('/')}/node/update/check");
                        if (!string.IsNullOrEmpty(key))
                        {
                            request.Headers.TryAddWithoutValidation("X-Internal-Key", key);
                        }
                        request.Headers.TryAddWithoutValidation("X-User-Role", "superadmin");

                        var reqObj = new
                        {
                            manifestJson = "{}",
                            manifestSignatureBase64 = "AAAA"
                        };
                        var json = JsonSerializer.Serialize(reqObj);
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                        var response = await client.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"{frontUrl.TrimEnd('/')}/node/update/status");
                            if (!string.IsNullOrEmpty(key))
                            {
                                statusRequest.Headers.TryAddWithoutValidation("X-Internal-Key", key);
                            }
                            statusRequest.Headers.TryAddWithoutValidation("X-User-Role", "superadmin");

                            var statusResponse = await client.SendAsync(statusRequest);
                            if (statusResponse.IsSuccessStatusCode)
                            {
                                var body = await statusResponse.Content.ReadAsStringAsync();
                                using var doc = JsonDocument.Parse(body);
                                var step = doc.RootElement.GetProperty("currentStep").GetString();
                                if (step == "Failed")
                                {
                                    statusItem.Header = "Updates: Failed (signature mismatch)";
                                }
                                else
                                {
                                    statusItem.Header = $"Updates: {step}";
                                }
                            }
                            else
                            {
                                statusItem.Header = "Updates: Check succeeded";
                            }
                        }
                        else
                        {
                            statusItem.Header = $"Updates: Error {(int)response.StatusCode}";
                        }
                    }
                    catch (Exception ex)
                    {
                        statusItem.Header = "Updates: Check failed";
                        Console.WriteLine($"Error checking updates: {ex.Message}");
                    }
                    finally
                    {
                        checkItem.IsEnabled = true;
                    }
                });
            };

            menu.Items.Add(openItem);
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
}