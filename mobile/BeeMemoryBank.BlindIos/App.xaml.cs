using BeeMemoryBank.BlindIos.Pages;
using BeeMemoryBank.BlindIos.Platforms.iOS;
using BeeMemoryBank.BlindIos.Services;
using Microsoft.Extensions.DependencyInjection;
using UIKit;

namespace BeeMemoryBank.BlindIos;

/// <summary>
/// The app's life on iOS. In front: the ONE in-app loop of the phone runs (a check at once, then for each account in turn a sync every 15 minutes and the long job every hour). Going to the
/// background: the loop pauses within the few seconds iOS grants, the two background requests are submitted (when an account needs them) and the "silent node" notifications are moved; from
/// then on only the rounds iOS grants run (<see cref="IosBlindBackground"/>), serving the accounts in turn. Nothing listens and no connection is kept open while the app is not in front.
/// The start page is the list of accounts; the system Camera opens a <c>bmb-invite:</c> link on the Add page and a <c>bmb-blind-call:</c> link on the account that waits for it.
/// </summary>
public partial class App : Application
{
    private static App? _running;
    private static string? _pendingLink;
    private static string? _pendingInvitation;

    private readonly IServiceProvider _services;
    private readonly IosBlindRuntime _runtime;
    private readonly IosBlindBackground _background;
    private readonly IosSilenceNotifier _silence;
    private NavigationPage? _navigation;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _runtime = services.GetRequiredService<IosBlindRuntime>();
        _background = services.GetRequiredService<IosBlindBackground>();
        _silence = services.GetRequiredService<IosSilenceNotifier>();
        // The network path arrives a moment after the monitor starts: start it now, so that the first question about a metered network has an answer.
        services.GetRequiredService<IIosNetworkPathSource>().Start();
        _running = this;
    }

    // Resolved only here: a page built before InitializeComponent() has loaded App.xaml's resources dies on its first {StaticResource}.
    protected override Window CreateWindow(IActivationState? activationState)
    {
        _navigation = new NavigationPage(_services.GetRequiredService<AccountsPage>())
        {
            BarBackgroundColor = (Color)Resources["SurfaceColor"],
            BarTextColor = (Color)Resources["TextColor"],
        };
        return new Window(_navigation) { Title = "Bee Memory Bank Blind" };
    }

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
        try
        {
            // One request pending exactly while an account needs it; the notifications are moved for every account.
            _runtime.RefreshBackgroundRequests();
            _silence.Update(_runtime.Accounts.GetSnapshot());
        }
        catch (Exception ex)
        {
            Report("Going to the background", ex);
        }

        // iOS suspends the process a few seconds after this: the running job is paused (it resumes where it stopped) inside the time a
        // background-task assertion grants, so that it never stops halfway through a write.
        var app = UIApplication.SharedApplication;
        var assertion = UIApplication.BackgroundTaskInvalid;
        assertion = app.BeginBackgroundTask("bmb-blind-pause", () => End(app, ref assertion));
        // Asked for HERE, on the main thread, in the order OnResume and OnSleep come in: the app is no longer in front from this line on, and the lifecycle stops the loop after every older
        // transition (a background wake may run once it has). The task is only awaited, never started on another thread, so a quick OnResume cannot be overtaken by this stop.
        var stopping = _runtime.StopForegroundAsync();
        _ = Task.Run(async () =>
        {
            try { await stopping; }
            catch (Exception) { /* the loop stops either way when the process is suspended */ }
            finally { MainThread.BeginInvokeOnMainThread(() => End(app, ref assertion)); }
        });
    }

    /// <summary>
    /// A link the system Camera (or a tap on pasted text) opened the app with. A <c>bmb-invite:</c> link goes to the Add page with the invitation read and previewed - nothing is added until the
    /// person presses Add. A <c>bmb-blind-call:</c> link is the computer's answer to a pairing code: it is handed to the account that waits for it, which accepts it exactly as if it had been pasted
    /// and Connect pressed (only a code made with that phone's own pairing secret is accepted). A link that arrives before the screen exists waits for it.
    /// </summary>
    public static bool TryHandleLink(string? url)
    {
        var link = IosLinks.Parse(url);
        if (!link.IsOurs) return false;
        if (link.Kind == IosLinkKind.Invitation) _pendingInvitation = link.Text;
        else _pendingLink = link.Text;

        if (_running is { _navigation: { } navigation } running)
            _ = MainThread.InvokeOnMainThreadAsync(() => running.OpenPendingAsync(navigation));
        return true;
    }

    /// <summary>The link that arrived before the screen, once: the computer's answer.</summary>
    internal static string? TakePendingLink() => Interlocked.Exchange(ref _pendingLink, null);

    /// <summary>The invitation that arrived before the screen, once.</summary>
    internal static string? TakePendingInvitation() => Interlocked.Exchange(ref _pendingInvitation, null);

    /// <summary>True while the computer's answer waits for a screen to take it.</summary>
    internal static bool HasPendingCallCode => Volatile.Read(ref _pendingLink) is not null;

    /// <summary>Opens what waits: the Add page for an invitation, the page of the account that waits for the answer for a call code.</summary>
    private async Task OpenPendingAsync(NavigationPage navigation)
    {
        try
        {
            if (TakePendingInvitation() is { } invitation)
            {
                await navigation.PushAsync(new AddAccountPage(_runtime, _silence, invitation));
                return;
            }

            var url = TakePendingLink();
            if (url is null) return;
            var waiting = _runtime.Accounts.GetSnapshot().Accounts.Where(a => a.Status is { AwaitingAnswer: true }).Select(a => a.Entry.Id).ToList();
            if (navigation.CurrentPage is BlindHomePage open && waiting.Contains(open.AccountId))
            {
                _ = open.AcceptLinkAsync(url);
                return;
            }
            if (waiting.Count == 0)
            {
                await navigation.CurrentPage.DisplayAlertAsync("Connect",
                    "No account is waiting for a computer's answer. Add an account with a pairing code first, then scan the code the computer shows back.", "OK");
                return;
            }

            // The page of the first account that waits; when the answer lands in another, the page moves over to that one (BlindHomePage.Accept).
            var page = new BlindHomePage(_runtime, _silence, waiting[0]);
            await navigation.PushAsync(page);
            _ = MainThread.InvokeOnMainThreadAsync(() => page.AcceptLinkAsync(url));
        }
        catch (Exception ex)
        {
            Report("Opening a link failed", ex);
        }
    }

    private async Task StartLoopAsync()
    {
        try
        {
            // The accounts first (the folder is taken, a 2.6 copy is adopted as account 1, every account is started), then the loop: its first pass is the check at once. A background round
            // that is still on is asked to stop and waited for first: the loop and a background round are never inside two accounts at once. The request itself is made before the first
            // await, on the main thread, so it keeps its place in the lifecycle's queue behind an OnSleep that came before it.
            await _runtime.StartForegroundAsync();
            _silence.Refresh();
        }
        catch (Exception ex)
        {
            Report("Starting the copy failed", ex);
        }
    }

    private static void End(UIApplication app, ref nint assertion)
    {
        if (assertion == UIApplication.BackgroundTaskInvalid) return;
        app.EndBackgroundTask(assertion);
        assertion = UIApplication.BackgroundTaskInvalid;
    }

    /// <summary>An error of the app as a whole goes to the device's log, by its kind - never to the log of one of the accounts (<see cref="IosAppErrors"/>).</summary>
    private void Report(string what, Exception error) => IosAppErrors.Report(_runtime, what, error);
}
