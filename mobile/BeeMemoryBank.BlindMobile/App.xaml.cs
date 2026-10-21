using BeeMemoryBank.BlindMobile.Pages;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile;

public partial class App : Application
{
    private readonly IServiceProvider _services;
    private bool _ready;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        // Resolved only now: a page taken as a constructor parameter is built by the container before this
        // body runs, i.e. before InitializeComponent() has loaded App.xaml's resources, and its XAML dies on
        // the first {StaticResource ...} (BlindStartupTests). The start page is the list of accounts.
        MainPage = new NavigationPage(services.GetRequiredService<BlindAccountsPage>())
        {
            BarBackgroundColor = (Color)Resources["SurfaceColor"],
            BarTextColor = (Color)Resources["TextColor"],
        };
        BlindInviteLinks.Offered += () => MainThread.BeginInvokeOnMainThread(OpenOfferedInvitation);
    }

    protected override async void OnStart()
    {
        base.OnStart();

        var device = _services.GetRequiredService<BlindDevice>();
        try
        {
            // Opens the registry and every account's database; an account that cannot start says so on its card, it does not stop the others.
            await device.StartAsync();
        }
        catch (Exception ex)
        {
            // Release builds drop Debug output: the device log is where the owner can read it.
            device.Log.Add("start", $"Starting the accounts failed: {BlindRunReport.Reason(ex)}");
        }

        StartBlindWork(device);
        _ready = true;
        OpenOfferedInvitation();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_ready) OpenOfferedInvitation();
    }

    /// <summary>Schedules the blind copies' sync and long jobs (WorkManager) and keeps them in step with the accounts' settings.</summary>
    public static void StartBlindWork(BlindDevice device)
    {
#if ANDROID
        device.Watch(
            requirements => Platforms.Android.BlindWorkScheduler.Ensure(Platform.AppContext, requirements),
            () => Platforms.Android.BlindWorkScheduler.RunHeavyNow(Platform.AppContext));
#endif
    }

    /// <summary>A <c>bmb-invite:</c> link the system handed over opens the Add screen with its preview; nothing is added until the person presses Add.</summary>
    private void OpenOfferedInvitation()
    {
        try
        {
            if (!_ready || MainPage is not NavigationPage navigation) return;
            if (BlindInviteLinks.Take() is not { } link) return;
            if (navigation.CurrentPage is BlindAddPage open)
            {
                open.Offer(link);
                return;
            }
            var page = _services.GetRequiredService<BlindAddPage>();
            page.Offer(link);
            _ = navigation.PushAsync(page);
        }
        catch (Exception ex)
        {
            _services.GetService<BlindDevice>()?.Log.Add("start", $"Opening the invitation failed: {BlindRunReport.Reason(ex)}");
        }
    }
}
