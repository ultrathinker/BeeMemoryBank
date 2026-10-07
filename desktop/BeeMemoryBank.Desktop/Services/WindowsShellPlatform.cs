using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>The Windows set: the registry Run key, SetThreadExecutionState, the hidden power-broadcast window, the balloon notifier and Explorer.</summary>
internal sealed class WindowsShellPlatform : IShellPlatform
{
    public ShellOs Os => ShellOs.Windows;

    public IAutostartService CreateAutostart() => new AutostartService();

    public IPreventSleepService? CreatePreventSleep(DesktopSettingsStore settings)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return new PreventSleepService(settings);
    }

    public IPowerEventsService? CreatePowerEvents(SleepLockRequest lockNode, Func<bool> lockOnSleepEnabled)
    {
        if (!OperatingSystem.IsWindows()) return null;
        // The Windows monitor shows its own balloon and does not wait for the callback: the lock request goes out in the background.
        return new PowerEventsService(() => { _ = Task.Run(() => lockNode(CancellationToken.None)); }, lockOnSleepEnabled);
    }

    /// <summary>A balloon on a hidden window of the notifier's own (the power-events service keeps its own one for the sleep notice).</summary>
    public IUserNotifier? CreateNotifier() => OperatingSystem.IsWindows() ? new WindowsBalloonNotifier() : null;

    public IFileManagerReveal FileManager { get; } = new WindowsFileManager();

    public string TrayIconAsset => "avares://BeeMemoryBank.Desktop/Assets/icon.png";

    public bool TrayIconIsTemplate => false;

    public string QuitMenuText => "Exit";

    public bool InterceptsApplicationQuit => false;

    public string AutostartCheckText => "Start Bee Memory Bank when I sign in to Windows";

    public string AutostartProfileNote => "Which profile opens when Windows starts is set in Settings.";
}

/// <summary>"Open folder" in Explorer, as the Profiles window did it before the shell had an abstraction for it.</summary>
public sealed class WindowsFileManager : IFileManagerReveal
{
    private readonly Action<ProcessStartInfo> _start;

    public WindowsFileManager() : this(info => Process.Start(info)) { }

    internal WindowsFileManager(Action<ProcessStartInfo> start) => _start = start;

    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Ensure the dir exists so Explorer does not error out on a vault that was never
        // started (ProfileService.AddProfile creates the auto path, but an explicit path or
        // a forgotten-but-not-deleted profile might not).
        try { Directory.CreateDirectory(path); }
        catch { /* best-effort; let explorer.exe handle whatever it can */ }

        try
        {
            _start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open folder '{path}': {ex.Message}");
        }
    }

    public void Reveal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            _start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to reveal '{path}': {ex.Message}");
        }
    }
}
