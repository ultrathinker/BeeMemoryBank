using Avalonia;
using System;
using System.IO;
using System.Threading;
using Velopack;
using BeeMemoryBank.AppPaths;

namespace BeeMemoryBank.Desktop;

class Program
{
    public static bool StartMinimized { get; private set; }

    private static Mutex? _singleInstanceMutex;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var app = VelopackApp.Build();

        if (OperatingSystem.IsWindows())
        {
            // A fresh install starts with Windows (the tray toggle can turn it off). Updates leave
            // the user's choice alone; uninstall removes the Run entry so it never points at a
            // deleted exe.
            app.OnAfterInstallFastCallback(_ => TryAutostart(enable: true));
            app.OnBeforeUninstallFastCallback(_ => TryAutostart(enable: false));
            app.OnAfterUpdateFastCallback(v =>
            {
                var legacyPath = Path.Combine(AppContext.BaseDirectory, "data");
                var targetPath = BmbPaths.DefaultVaultDir;
                var result = LegacyDataRescue.TryRescue(legacyPath, targetPath);
                try
                {
                    var logMsg = $"[{DateTime.UtcNow:O}] Velopack post-update hook: TryRescue from '{legacyPath}' to '{targetPath}'. Version: {v}. Outcome: {result.Outcome}, Message: {result.Message ?? "none"}{Environment.NewLine}";
                    File.AppendAllText(Path.Combine(BmbPaths.LogsDir, "velopack.log"), logMsg);
                }
                catch { }
            });
        }

        app.Run();

        foreach (var arg in args)
        {
            if (arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
            {
                StartMinimized = true;
            }
        }

        const string mutexName = "BeeMemoryBank.Desktop.Mutex";
        _singleInstanceMutex = new Mutex(true, mutexName, out bool isNewInstance);

        if (!isNewInstance)
        {
            // Another instance is already running; exit immediately.
            return;
        }

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch { }
            _singleInstanceMutex.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    // Velopack hooks must never throw: a failure here would abort install/uninstall.
    private static void TryAutostart(bool enable)
    {
        try
        {
            var autostart = new Services.AutostartService();
            if (enable) autostart.Enable(); else autostart.Disable();
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(BmbPaths.LogsDir);
                File.AppendAllText(Path.Combine(BmbPaths.LogsDir, "velopack.log"),
                    $"[{DateTime.UtcNow:O}] Autostart {(enable ? "enable" : "disable")} failed: {ex.Message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
