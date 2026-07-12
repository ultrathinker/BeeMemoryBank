using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop;

public partial class MainWindow : Window
{
    private Process? _nodeProcess;
    private bool _isRealClose;
    private CancellationTokenSource? _initCts;
    private bool _startMinimized = Program.StartMinimized;
    private string? _frontUrl;
    private Services.PowerEventsService? _powerEventsService;

    public MainWindow()
    {
        InitializeComponent();
        Opened += MainWindow_Opened;
    }

    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        if (_startMinimized)
        {
            _startMinimized = false;
            Hide();
        }
        StartHostOrAttach();
    }

    private void StartHostOrAttach()
    {
        _initCts?.Cancel();
        _initCts = new CancellationTokenSource();

        SplashPanel.IsVisible = true;
        ErrorPanel.IsVisible = false;
        WebPanel.IsVisible = false;
        StatusText.Text = "Initializing...";

        var token = _initCts.Token;
        Task.Run(() => HostOrAttachAsync(token), token);
    }

    private async Task HostOrAttachAsync(CancellationToken token)
    {
        try
        {
            UpdateStatus("Resolving data directory...");
            var dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data"));
            Directory.CreateDirectory(dataDir);

            UpdateStatus("Probing existing node instance...");
            var runtimeJsonPath = Path.Combine(dataDir, ".runtime.json");
            
            bool attached = false;
            string? frontUrl = null;

            if (File.Exists(runtimeJsonPath))
            {
                RuntimeDescriptor? descriptor = null;
                try
                {
                    var json = await File.ReadAllTextAsync(runtimeJsonPath, token);
                    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                    descriptor = JsonSerializer.Deserialize<RuntimeDescriptor>(json, options);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to parse existing runtime json: {ex.Message}");
                }

                if (descriptor != null && descriptor.Pid > 0 && !string.IsNullOrEmpty(descriptor.FrontUrl))
                {
                    // Check if PID is running
                    bool isProcessRunning = false;
                    try
                    {
                        using var proc = Process.GetProcessById(descriptor.Pid);
                        isProcessRunning = !proc.HasExited;
                    }
                    catch (ArgumentException) { }

                    if (isProcessRunning)
                    {
                        UpdateStatus("Probing existing node status endpoint...");
                        bool probeOk = false;
                        try
                        {
                            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                            var response = await client.GetAsync($"{descriptor.FrontUrl.TrimEnd('/')}/node/status", token);
                            if (response.IsSuccessStatusCode)
                            {
                                probeOk = true;
                            }
                        }
                        catch { }

                        if (probeOk)
                        {
                            attached = true;
                            frontUrl = descriptor.FrontUrl;
                            UpdateStatus("Attached to running node!");
                        }
                    }
                }
            }

            if (!attached)
            {
                UpdateStatus("Locating BeeMemoryBank.Node executable...");
                var nodeExePath = ResolveNodeExePath();

                UpdateStatus("Starting background node service...");

                // Clean up any stale runtime.json to avoid parsing old files
                if (File.Exists(runtimeJsonPath))
                {
                    try { File.Delete(runtimeJsonPath); } catch { }
                }

                // bmbd's own --auto mode discovers the sibling api/ and web/ folders and
                // wires up ready-files, stdin-lifeline (graceful shutdown + session-lock
                // hygiene on stop) and forwarded-headers itself - no need to hand-author a
                // node.config.json here, and no risk of drifting from bmbd's own conventions
                // (e.g. hardcoding ports that collide with the standalone/Docker defaults).
                var startInfo = new ProcessStartInfo
                {
                    FileName = nodeExePath,
                    Arguments = $"--auto --data \"{dataDir}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    WorkingDirectory = Path.GetDirectoryName(nodeExePath)
                };

                var proc = Process.Start(startInfo);
                if (proc == null)
                {
                    throw new Exception("Failed to start BeeMemoryBank.Node process.");
                }

                _nodeProcess = proc;
                
                // Start polling for .runtime.json
                UpdateStatus("Waiting for node services to start (up to 60s)...");
                var stopwatch = Stopwatch.StartNew();
                
                while (stopwatch.Elapsed.TotalSeconds < 60 && !token.IsCancellationRequested)
                {
                    if (proc.HasExited)
                    {
                        throw new Exception($"Node process exited prematurely with code {proc.ExitCode}. Check bmbd logs.");
                    }

                    if (File.Exists(runtimeJsonPath))
                    {
                        try
                        {
                            var json = await File.ReadAllTextAsync(runtimeJsonPath, token);
                            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                            var newDescriptor = JsonSerializer.Deserialize<RuntimeDescriptor>(json, options);
                            
                            if (newDescriptor != null && !string.IsNullOrEmpty(newDescriptor.FrontUrl))
                            {
                                // Verify it is actually responding
                                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                                var response = await client.GetAsync($"{newDescriptor.FrontUrl.TrimEnd('/')}/node/status", token);
                                if (response.IsSuccessStatusCode)
                                {
                                    frontUrl = newDescriptor.FrontUrl;
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            // File might be in the middle of being written, ignore and try again
                        }
                    }

                    UpdateStatus($"Waiting for node services to start... ({Math.Round(stopwatch.Elapsed.TotalSeconds)}s)");
                    await Task.Delay(500, token);
                }

                if (frontUrl == null)
                {
                    throw new TimeoutException("Timed out waiting for BeeMemoryBank.Node services to become ready.");
                }
            }

            // Success! Load the URL in WebView
            var targetUrl = frontUrl;
            Dispatcher.UIThread.Post(() =>
            {
                if (!string.IsNullOrEmpty(targetUrl))
                {
                    _frontUrl = targetUrl;
                    BmbWebView.Source = new Uri(targetUrl);
                    StartPowerEventsMonitoring();
                }
                SplashPanel.IsVisible = false;
                WebPanel.IsVisible = true;
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ShowError(ex.Message));
        }
    }

    private string ResolveNodeExePath()
    {
        var baseDir = AppContext.BaseDirectory;

        // 1. Production published layout: sibling to desktop
        var prodPath = Path.GetFullPath(Path.Combine(baseDir, "..", "bmbd", "BeeMemoryBank.Node.exe"));
        if (File.Exists(prodPath))
        {
            return prodPath;
        }

        // 1b. Packaged (Velopack) layout: vpk requires the main exe at the root of
        // --packDir, so bmbd/api/web/cli ship as subfolders alongside Desktop.exe
        // itself rather than as siblings of a desktop/ folder one level up.
        var packagedPath = Path.GetFullPath(Path.Combine(baseDir, "bmbd", "BeeMemoryBank.Node.exe"));
        if (File.Exists(packagedPath))
        {
            return packagedPath;
        }

        // 2. Development tree: search up for solution file then down
        var currentDir = new DirectoryInfo(baseDir);
        while (currentDir != null)
        {
            var slnxFile = Path.Combine(currentDir.FullName, "BeeMemoryBank.slnx");
            if (File.Exists(slnxFile))
            {
                var devPath = Path.Combine(currentDir.FullName, "desktop", "BeeMemoryBank.Node", "bin", "Debug", "net10.0", "BeeMemoryBank.Node.exe");
                if (File.Exists(devPath))
                {
                    return Path.GetFullPath(devPath);
                }
                break;
            }
            currentDir = currentDir.Parent;
        }

        // 3. Current folder fallback
        var siblingPath = Path.GetFullPath(Path.Combine(baseDir, "BeeMemoryBank.Node.exe"));
        if (File.Exists(siblingPath))
        {
            return siblingPath;
        }

        throw new FileNotFoundException($"Could not locate BeeMemoryBank.Node.exe. Looked in:\n- {prodPath}\n- (development layout root)\n- {siblingPath}");
    }

    private void UpdateStatus(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = message;
        });
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        SplashPanel.IsVisible = false;
        ErrorPanel.IsVisible = true;
    }

    private void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        StartHostOrAttach();
    }

    public void ShowAndFocusWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void RealClose()
    {
        _isRealClose = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isRealClose)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            StopNodeProcess();
            base.OnClosing(e);
        }
    }

    private void StopNodeProcess()
    {
        if (_nodeProcess != null && !_nodeProcess.HasExited)
        {
            try
            {
                _nodeProcess.Kill(entireProcessTree: true);
                _nodeProcess.Dispose();
                _nodeProcess = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error killing node process: {ex.Message}");
            }
        }

        try
        {
            _powerEventsService?.Dispose();
            _powerEventsService = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error disposing power events service: {ex.Message}");
        }
    }

    private void StartPowerEventsMonitoring()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            _powerEventsService?.Dispose();
            _powerEventsService = new Services.PowerEventsService(HandleSystemSleep);
            _powerEventsService.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start power events monitoring: {ex.Message}");
        }
    }

    private void HandleSystemSleep()
    {
        var url = _frontUrl;
        if (string.IsNullOrEmpty(url)) return;

        // Fire-and-forget: /node/lock is currently a 501 stub pending the internal-key
        // client wiring, so failures here are expected for now - don't crash the app.
        Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                await client.PostAsync($"{url.TrimEnd('/')}/node/lock", null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to POST /node/lock on sleep: {ex.Message}");
            }
        });
    }
}

public record RuntimeDescriptor(
    int Pid,
    string? FrontUrl,
    string Version,
    string Mode
);