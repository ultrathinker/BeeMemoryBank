using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Maui;

namespace BeeMemoryBank.BlindMobile.Platforms.Android;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    LaunchMode = LaunchMode.SingleTop)]
// A bmb-invite: link (a tap on it in a message, or the Camera reading its QR code) opens the app on the Add screen with the preview of the invitation. Nothing is added by the link alone.
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "bmb-invite")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // A recreated activity (savedInstanceState set) already handed its link over once.
        if (savedInstanceState is null) HandOverLink(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandOverLink(intent);
    }

    /// <summary>
    /// The link is a one-time capability: it goes to the Add screen in memory and is taken out of the intent, so it is not left in the activity's record. The Add screen shows the preview; the
    /// person presses Add. A link that is not this app's, or too long to be an invitation, is dropped by <see cref="BlindInviteLinks.Offer"/>.
    /// </summary>
    private static void HandOverLink(Intent? intent)
    {
        if (intent?.Action != Intent.ActionView) return;
        var link = intent.DataString;
        intent.SetData(null);
        BlindInviteLinks.Offer(link);
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

    // "Save to…" of the blind copy's backups returns here from the system file picker.
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == SafExport.RequestCode) SafExport.OnResult(resultCode, data);
    }
}
