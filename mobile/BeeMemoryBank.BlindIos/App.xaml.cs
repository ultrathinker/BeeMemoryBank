using BeeMemoryBank.BlindIos.Pages;
using BeeMemoryBank.BlindIos.Platforms.iOS;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using UIKit;

namespace BeeMemoryBank.BlindIos;

/// <summary>
/// The app's life on iOS. In front: the in-app loop runs (a check at once, a sync every 15 minutes, the long job every hour, like the
/// desktop copies). Going to the background: the loop pauses within the few seconds iOS grants, the two background requests are submitted
/// and the "silent node" notification is moved; from then on only the rounds iOS grants run (<see cref="IosBlindBackground"/>). Nothing
/// listens and no connection is kept open while the app is not in front.
/// </summary>
public partial class App : Application
{
    private static App? _running;
    private static string? _pendingLink;

    private readonly IServiceProvider _services;
    private readonly IosBlindHost _host;
    private readonly IosBlindBackground _background;
    private readonly IosSilenceNotifier _silence;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _host = services.GetRequiredService<IosBlindHost>();
        _background = services.GetRequiredService<IosBlindBackground>();
        _silence = services.GetRequiredService<IosSilenceNotifier>();
        _running = this;
    }

    // Resolved only here: a page built before InitializeComponent() has loaded App.xaml's resources dies on its first {StaticResource}.
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(_services.GetRequiredService<BlindHomePage>()) { Title = "Bee Memory Bank Blind" };

    protected override void OnStart()
    {
        base.OnStart();
        _ = StartLoopAsync();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _ = StartLoopAsync();
    }

    protected override void OnSleep()
    {
        base.OnSleep();
        var runtime = _host.Current;
        try
        {
            _background.Submit();
            _silence.Update(runtime.App.GetStatus());
        }
        catch (Exception ex)
        {
            Log(runtime, $"Going to the background: {ex.Message}");
        }

        // iOS suspends the process a few seconds after this: the running job is paused (it resumes where it stopped) inside the time a
        // background-task assertion grants, so that it never stops halfway through a write.
        var app = UIApplication.SharedApplication;
        var assertion = UIApplication.BackgroundTaskInvalid;
        assertion = app.BeginBackgroundTask("bmb-blind-pause", () => End(app, ref assertion));
        _ = Task.Run(async () =>
        {
            try { await runtime.Scheduler.StopAsync(); }
            catch (Exception) { /* the loop stops either way when the process is suspended */ }
            finally { MainThread.BeginInvokeOnMainThread(() => End(app, ref assertion)); }
        });
    }

    /// <summary>
    /// A <c>bmb-blind-call:</c> link: the Camera scanned the computer's QR code, or the person tapped the code. It is handed to the screen,
    /// which accepts it exactly as if it had been pasted and Connect pressed (only a code made with this phone's own pairing secret is
    /// accepted). A link that arrives before the screen exists waits for it.
    /// </summary>
    public static bool TryHandleLink(string? url)
    {
        if (url is null || !url.StartsWith("bmb-blind-call:", StringComparison.OrdinalIgnoreCase)) return false;
        if (_running?.Windows.FirstOrDefault()?.Page is BlindHomePage page) MainThread.BeginInvokeOnMainThread(() => page.AcceptLink(url));
        else _pendingLink = url;
        return true;
    }

    /// <summary>The link that arrived before the screen, once.</summary>
    internal static string? TakePendingLink() => Interlocked.Exchange(ref _pendingLink, null);

    private async Task StartLoopAsync()
    {
        var runtime = _host.Current;
        try
        {
            // The database and the identity first (the scheduler's sync does not open them), then the loop: its first pass is the check at once.
            await runtime.App.InitializeAsync();
            runtime.Scheduler.EnsureScheduled();
            _silence.Refresh();
        }
        catch (Exception ex)
        {
            Log(runtime, $"Starting the copy failed: {ex.Message}");
        }
    }

    private static void End(UIApplication app, ref nint assertion)
    {
        if (assertion == UIApplication.BackgroundTaskInvalid) return;
        app.EndBackgroundTask(assertion);
        assertion = UIApplication.BackgroundTaskInvalid;
    }

    private static void Log(IosBlindRuntime runtime, string message)
    {
        try { runtime.Services.GetRequiredService<BlindPhoneLog>().Add("app", message); }
        catch (Exception) { /* nowhere left to tell it */ }
    }
}
