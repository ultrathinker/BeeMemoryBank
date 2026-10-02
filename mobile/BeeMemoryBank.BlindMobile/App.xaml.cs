using BeeMemoryBank.BlindMobile.Pages;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile;

public partial class App : Application
{
    private readonly BlindStartup _startup;
    private readonly IServiceProvider _services;

    public App(BlindStartup startup, IServiceProvider services)
    {
        InitializeComponent();
        _startup = startup;
        _services = services;
        // Resolved only now: a page taken as a constructor parameter is built by the container before this
        // body runs, i.e. before InitializeComponent() has loaded App.xaml's resources, and its XAML dies on
        // the first {StaticResource ...} (BlindStartupTests).
        MainPage = services.GetRequiredService<BlindHomePage>();
    }

    protected override async void OnStart()
    {
        base.OnStart();

        try
        {
            await _startup.EnsureReadyAsync();
        }
        catch (Exception ex)
        {
            // Release builds drop Debug output: the screen's log is where the owner can read it.
            _services.GetRequiredService<BlindPhoneLog>().Add("start", $"Opening the database failed: {ex.Message}");
        }

        StartBlindWork();
    }

    /// <summary>Schedules the blind copy's sync and long jobs (WorkManager).</summary>
    public static void StartBlindWork()
    {
#if ANDROID
        Platforms.Android.BlindWorkScheduler.Ensure(Platform.AppContext);
#endif
    }
}
