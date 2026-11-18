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
    // ⚠ DEBUG-ONLY auto-unlock/auto-init via Intent extras for adb scripting:
    //   adb shell am start -n ...MainActivity -e bmb_init_name "MyPhone" -e bmb_init_password "secret" -e bmb_init_hint "the blue folder by the window"
    //   adb shell am start -n ...MainActivity -e bmb_unlock_password "secret"
    // Compiled out of Release builds because any app on the device, or anyone
    // with adb access, can otherwise bypass the unlock screen by sending an
    // intent with these extras. The plaintext password also lands in adb shell
    // history. Maestro tests use the normal UI flow (inputText), so they do
    // not depend on these.
#if DEBUG
    public const string ExtraInitName = "bmb_init_name";
    public const string ExtraInitPassword = "bmb_init_password";
    /// <summary>Optional: the password hint a person would have written (the headless path has no person, so it may be absent).</summary>
    public const string ExtraInitHint = "bmb_init_hint";
    public const string ExtraUnlockPassword = "bmb_unlock_password";
#endif

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // We intentionally do NOT set FLAG_SECURE. Blocking screenshots/screen-recording
        // is user-hostile (breaks debugging, support screenshots, accessibility tools)
        // and gives little real security — the data lives on the device anyway, and a
        // determined attacker with physical access has many other paths. We do not want
        // to support this restriction.

        // The system bars follow the app appearance (the background token), not a fixed theme colour.
        HookSystemBars();

        // A modal page lives in its own dialog window that the activity's colours do not reach; repaint
        // each one with the appearance as it is shown. MAUI shows its modal DialogFragments on this
        // activity's SupportFragmentManager.
        if (SupportFragmentManager is { } fragments)
            fragments.RegisterFragmentLifecycleCallbacks(new ModalSystemBars(), recursive: true);
    }

    private bool _systemBarsHooked;

    // Every modal page (AppDialog, BottomSheet, the input popup, the change-master-password dialog) is a
    // DialogFragment. Its window takes bar colours from the theme and icon appearance from colorPrimary,
    // so apply the app appearance to it when it resumes — after MAUI has configured the window.
    private sealed class ModalSystemBars : global::AndroidX.Fragment.App.FragmentManager.FragmentLifecycleCallbacks
    {
        public override void OnFragmentResumed(global::AndroidX.Fragment.App.FragmentManager? fm, global::AndroidX.Fragment.App.Fragment? f)
        {
            if (f is global::AndroidX.Fragment.App.DialogFragment dialog && dialog.Dialog?.Window is { } window)
            {
                SystemBarColors.ApplyModal(window);
                // A modal with a field (the input popup, the change-password dialog) must keep its card
                // and buttons above the soft keyboard: resize the dialog window for the keyboard rather
                // than let it cover them. A modal without a field never shows the keyboard, so this is
                // inert for the sheets and the confirm dialogs.
                window.SetSoftInputMode(global::Android.Views.SoftInput.AdjustResize);
            }
        }
    }

    // Apply now and follow the appearance from here on. The MAUI Application may not exist yet at
    // OnCreate, so OnResume calls this too; the hook is taken only once.
    private void HookSystemBars()
    {
        SystemBarColors.Apply(this);
        if (!_systemBarsHooked && Microsoft.Maui.Controls.Application.Current is { } app)
        {
            app.RequestedThemeChanged += OnAppThemeChanged;
            _systemBarsHooked = true;
        }
    }

    private void OnAppThemeChanged(object? sender, Microsoft.Maui.Controls.AppThemeChangedEventArgs e)
    {
        SystemBarColors.Apply(this);
        // Any modal already on screen follows the new appearance too, not just the next one shown.
        foreach (var f in SupportFragmentManager?.Fragments ?? [])
            if (f is global::AndroidX.Fragment.App.DialogFragment dialog && dialog.Dialog?.Window is { } window)
                SystemBarColors.ApplyModal(window);
        // On API 35+ the Shell app bar shows through the transparent status bar; repaint it too.
        SystemBarColors.ApplyShellAppBars(this);
    }

    protected override void OnDestroy()
    {
        if (_systemBarsHooked && Microsoft.Maui.Controls.Application.Current is { } app)
            app.RequestedThemeChanged -= OnAppThemeChanged;
        base.OnDestroy();
    }

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
#if DEBUG
        HandleAutoUnlock(intent);
        HandleAutoInit(intent);
#endif
        HandleShareIntent(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        HookSystemBars();
#if DEBUG
        HandleAutoUnlock(Intent);
        HandleAutoInit(Intent);
#endif
        HandleShareIntent(Intent);
    }

    // Every touch is activity for the idle auto-lock (the shared policy's Activity): the person is
    // still here. Forwarded to the singleton AutoLockService, which the unlock path constructs.
    public override bool DispatchTouchEvent(global::Android.Views.MotionEvent? e)
    {
        AutoLockService.Touch?.Invoke();
        return base.DispatchTouchEvent(e);
    }

    protected override void OnActivityResult(int requestCode, global::Android.App.Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        TextFileSaver.HandleResult(requestCode, resultCode, data);
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
            // A locked phone (an encrypted one: its database closed) does not open the editor over the unlock page: the text waits, and the
            // unlock opens the editor with it (PostUnlockRouter) - nothing before the unlock reads or writes the vault.
            if (IPlatformApplication.Current?.Services.GetService<SessionService>() is not { IsUnlocked: true }) return;
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try { await global::Microsoft.Maui.Controls.Shell.Current.GoToAsync("articleEdit"); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ShareIntent nav error: {ex.Message}"); }
            });
        });
    }

#if DEBUG
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
                            var router = services.GetRequiredService<BeeMemoryBank.Mobile.Services.PostUnlockRouter>();
                            await router.RouteAsync();
                            System.Diagnostics.Debug.WriteLine("AutoUnlock: routed post-unlock");
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
        var hint = intent.GetStringExtra(ExtraInitHint);
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
                await setupSvc.InitAsync(name, password, hint);
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
#endif
}
