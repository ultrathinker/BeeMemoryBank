using Avalonia;
using Avalonia.Controls;
using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindDesktop.Services;

namespace BeeMemoryBank.BlindDesktop;

internal static class Program
{
    /// <summary>What <see cref="Run"/> decided before the UI starts; the application object reads it.</summary>
    internal sealed record StartupState(StartupOptions Options, IBlindDesktopPlatform Platform, IInstanceGuard? Instance);

    internal static StartupState? Startup { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Last resort, for the record: an exception that reaches a thread without a handler ends the process in .NET, and this is the only
        // place that can still say what it was. (Every timer callback and every discarded task in the app has its own guard.)
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ErrorLog.Write("Unhandled exception (the process ends)", e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Write("A task failed and nobody awaited it", e.Exception);
            e.SetObserved();
        };

        return Run(args, Console.Out, Console.Error, PlatformSelector.Create, StartAvalonia);
    }

    /// <summary>
    /// Everything <see cref="Main"/> does except the process-wide handlers and the UI itself, with the three things a test must replace
    /// (the platform, the UI, the output) passed in. Returns the exit code: 0 for a normal end or for a second start that handed over to
    /// the running copy, 1 for a start that cannot go on (written to the error log), 2 for a command line that is refused (written to
    /// <paramref name="stderr"/>).
    /// </summary>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, Func<string?, IBlindDesktopPlatform> createPlatform,
        Func<string[], int> startUi)
    {
        var options = StartupOptions.Parse(args);
        if (options.SelfCheck)
        {
            // Before the platform exists: it would make the default data folder, and the check writes into its folder. A check never
            // touches the data of a blind app: it needs a folder of its own that is new or empty.
            if (options.SelfCheckWithoutFolder)
            {
                stderr.WriteLine("--self-check needs --data-dir <a scratch folder>: it must not run on the real data folder.");
                return 2;
            }
            if (SelfCheckFolder.Refusal(options.DataDirectory!, DefaultDataFolderOrNull()) is { } reason)
            {
                stderr.WriteLine($"--self-check refuses '{options.DataDirectory}': {reason}. A check needs a new or empty scratch folder; " +
                                 "it must never run on the data of a blind app.");
                return 2;
            }
        }

        IBlindDesktopPlatform platform;
        try
        {
            platform = createPlatform(options.DataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportFatal("The data folder cannot be used.", ex);
            return 1;
        }
        catch (Exception ex)
        {
            ReportFatal("The app cannot run on this computer.", ex);
            return 1;
        }

        ErrorLog.UsePath(platform.ErrorLogPath);

        // The data folder is made here (the platform's paths may have made it already): a path that is a file, or a folder that cannot be
        // made, ends the start with a line in the error log instead of an unhandled exception in the middle of the start.
        try
        {
            Directory.CreateDirectory(platform.Paths.DataDirectory);
        }
        catch (Exception ex)
        {
            ReportFatal("The data folder cannot be used.", ex);
            return 1;
        }

        // A headless check of the composition: no window, no tray icon, and it touches no key store (see HeadlessSelfCheck).
        if (options.SelfCheck) return HeadlessSelfCheck.Run(options, platform, stdout);

        IInstanceGuard? instance;
        try
        {
            instance = platform.TryAcquireInstance();
        }
        catch (Exception ex)
        {
            ReportFatal("The data folder cannot be used.", ex);
            return 1;
        }

        // One blind app per user and data folder. A second start asks the first one to show its window and ends at once.
        using (instance)
        {
            if (instance is null)
            {
                platform.SignalRunningInstance();
                return 0;
            }

            Startup = new StartupState(options, platform, instance);
            try
            {
                return startUi(args);
            }
            catch (Exception ex)
            {
                ReportFatal("The app stopped unexpectedly.", ex);
                return 1;
            }
        }
    }

    private static int StartAvalonia(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

    private static string? DefaultDataFolderOrNull()
    {
        try
        {
            return PlatformSelector.DefaultDataDirectory();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();

    /// <summary>A windowed app has no console: a start-up failure goes to a small text file (the platform's log folder, else the temp folder).</summary>
    internal static void ReportFatal(string what, Exception ex) => ErrorLog.Write(what, ex);
}
