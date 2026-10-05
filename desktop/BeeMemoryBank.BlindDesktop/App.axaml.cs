using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace BeeMemoryBank.BlindDesktop;

public sealed partial class App : Application
{
    private BlindDesktopShell? _shell;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Program.Startup is { } startup)
        {
            // The window is hidden when it is closed and shown from the tray, so closing it must not end the app.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // A tray app must not die of an exception in one click handler or one UI callback: it is written to the error log and the
            // app goes on (the controls that raised it show nothing new).
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                ErrorLog.Write("A UI callback failed", e.Exception);
                e.Handled = true;
            };
            _shell = new BlindDesktopShell(this, desktop, startup.Options, startup.Platform, startup.Instance);
            _ = StartAsync(_shell);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartAsync(BlindDesktopShell shell)
    {
        try
        {
            await shell.StartAsync();
        }
        catch (Exception ex)
        {
            Program.ReportFatal("The app could not start.", ex);
            shell.Quit();
        }
    }
}
