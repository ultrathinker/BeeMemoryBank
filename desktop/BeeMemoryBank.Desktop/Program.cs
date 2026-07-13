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
}
