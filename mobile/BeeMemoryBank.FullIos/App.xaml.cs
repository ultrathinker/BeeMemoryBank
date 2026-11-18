using BeeMemoryBank.FullIos.Pages;
using BeeMemoryBank.FullIos.Services;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.FullIos;

/// <summary>The app's window and its life: everything the vault's state decides is <see cref="AppFlow"/>'s.</summary>
public partial class App : Application
{
    private static string? _pendingJoinCode;
    private readonly IServiceProvider _services;
    private readonly AppFlow _flow;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _flow = services.GetRequiredService<AppFlow>();
        // The saved appearance (dark / light / system) before the first page is shown.
        ThemeManager.ApplySaved();
        // A failure of the one busy helper is said in the app's own dialog, not the grey system
        // alert (the same headline and text, only the skin changes; BusyAction.cs itself is shared).
        BeeMemoryBank.Busy.BusyAction.FailureDialog = (page, title, text) => Controls.AppDialog.AlertAsync(page, title, text);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new StartPage()) { Title = "Bee Memory Bank" };
        window.Deactivated += (_, _) => _flow.Deactivated();
        window.Activated += (_, _) => _flow.Activated();
        window.Stopped += (_, _) => _flow.Stopped();
        window.Resumed += (_, _) => _flow.Resumed();
        return window;
    }

    /// <summary>
    /// The cold start. An async void that throws ends the app before anything is on the screen, so a failure is logged and the
    /// start page says the app's own calm sentence; AppFlow.StartAsync already catches what it knows.
    /// </summary>
    protected override async void OnStart()
    {
        base.OnStart();
        try
        {
#if DEBUG || BMB_E2E
            if (await FullE2E.RunAsync(_services, _flow)) return;
#endif
            await _flow.StartAsync();
            if (Interlocked.Exchange(ref _pendingJoinCode, null) is { } code) ShowJoinCode(code);
        }
        catch (Exception ex)
        {
            _services.GetService<ILogger<App>>()?.LogError(ex, "The start failed");
            (Windows.FirstOrDefault()?.Page as StartPage)?.ShowProblem(Localization.AppStrings.Get("OpenPrepareError"));
        }
    }

    /// <summary>
    /// A <c>bmb-join:</c> link (the Camera scanned a computer's join QR code, or the code was tapped): on a phone without a vault the join
    /// page opens with the code filled in - nothing is sent before the person types the master password and taps Join. On a phone that
    /// already holds a vault it is ignored.
    /// </summary>
    public static bool TryHandleLink(string? url)
    {
        if (url is null || !url.StartsWith("bmb-join:", StringComparison.OrdinalIgnoreCase)) return false;
        if (Current is App { _flow: not null } app && app.Windows.FirstOrDefault()?.Page is not StartPage and not null)
            MainThread.BeginInvokeOnMainThread(() => app.ShowJoinCode(url));
        else
            _pendingJoinCode = url;
        return true;
    }

    /// <summary>A join link. On a phone that holds a vault (locked or not) it changes nothing; a failure is logged, never an end of the app.</summary>
    private async void ShowJoinCode(string code)
    {
        try
        {
            if (await _services.GetRequiredService<FullVault>().GetStateAsync() != VaultState.NoVault) return;
            _flow.ShowWelcome();
            var join = _services.GetRequiredService<JoinPage>();
            join.UseCode(code);
            if (Windows.FirstOrDefault()?.Page is NavigationPage nav) await nav.PushAsync(join);
        }
        catch (Exception ex)
        {
            _services.GetService<ILogger<App>>()?.LogError(ex, "Opening a join link failed");
        }
    }
}
