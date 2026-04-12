using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;

namespace BeeMemoryBank.Mobile.Platforms.Android;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    LaunchMode = LaunchMode.SingleTop)]
[IntentFilter(
    new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "text/plain",
    Label = "Save to BeeMemoryBank")]
public class MainActivity : MauiAppCompatActivity
{
    // adb shell am start -n ...MainActivity -e bmb_init_name "MyPhone" -e bmb_init_password "secret"
    // adb shell am start -n ...MainActivity -e bmb_unlock_password "secret"
    public const string ExtraInitName = "bmb_init_name";
    public const string ExtraInitPassword = "bmb_init_password";
    public const string ExtraUnlockPassword = "bmb_unlock_password";

    protected override void OnStart()
    {
        base.OnStart();

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            if (CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
                RequestPermissions(new[] { Manifest.Permission.PostNotifications }, 0);
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleAutoUnlock(intent);
        HandleAutoInit(intent);
        HandleShareIntent(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        HandleAutoUnlock(Intent);
        HandleAutoInit(Intent);
        HandleShareIntent(Intent);
    }

    private bool _autoInitDone;
    private bool _autoUnlockDone;

    private bool _shareHandled;

    private void HandleShareIntent(Intent? intent)
    {
        if (_shareHandled || intent == null) return;
        if (intent.Action != Intent.ActionSend) return;
        if (intent.Type != "text/plain") return;

        var text = intent.GetStringExtra(Intent.ExtraText) ?? "";
        if (string.IsNullOrEmpty(text)) return;

        _shareHandled = true;
        ShareIntentHandler.PendingText = text;

        Task.Run(async () =>
        {
            await Task.Delay(3000); // Wait for MAUI shell to be ready
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try { await global::Microsoft.Maui.Controls.Shell.Current.GoToAsync("articleEdit"); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ShareIntent nav error: {ex.Message}"); }
            });
        });
    }

    private void HandleAutoUnlock(Intent? intent)
    {
        if (_autoUnlockDone || intent == null) return;

        var password = intent.GetStringExtra(ExtraUnlockPassword);
        if (string.IsNullOrEmpty(password)) return;

        _autoUnlockDone = true;

        Task.Run(async () =>
        {
            try
            {
                // Wait for MAUI DI + migrations + Shell to be ready
                await Task.Delay(5000);
                System.Diagnostics.Debug.WriteLine("AutoUnlock: starting unlock");
                var services = IPlatformApplication.Current!.Services;
                var session = services.GetRequiredService<SessionService>();
                var ok = await session.UnlockAsync(password);
                System.Diagnostics.Debug.WriteLine($"AutoUnlock: UnlockAsync returned {ok}");
                if (ok)
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        try
                        {
                            await global::Microsoft.Maui.Controls.Shell.Current.GoToAsync("//status");
                            App.StartSyncService();
                            System.Diagnostics.Debug.WriteLine("AutoUnlock: navigated to //status");
                        }
                        catch (Exception ex2)
                        {
                            System.Diagnostics.Debug.WriteLine($"AutoUnlock nav error: {ex2}");
                        }
                    });
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("AutoUnlock: wrong password");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AutoUnlock error: {ex}");
            }
        });
    }

    private void HandleAutoInit(Intent? intent)
    {
        if (_autoInitDone || intent == null) return;

        var name = intent.GetStringExtra(ExtraInitName);
        var password = intent.GetStringExtra(ExtraInitPassword);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(password)) return;

        _autoInitDone = true;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2500); // Wait for MAUI DI + migrations
                var services = IPlatformApplication.Current!.Services;
                var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
                using var scope = scopeFactory.CreateScope();
                var setupSvc = scope.ServiceProvider.GetRequiredService<NodeSetupService>();
                await setupSvc.InitAsync(name, password);
                // Navigate on main thread; StatusPage.OnAppearing will start sync service
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await global::Microsoft.Maui.Controls.Shell.Current.GoToAsync("//status");
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AutoInit error: {ex}");
            }
        });
    }
}
