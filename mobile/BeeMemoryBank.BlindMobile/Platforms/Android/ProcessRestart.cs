using Android.App;
using Android.Content;
using Android.OS;

namespace BeeMemoryBank.BlindMobile.Platforms.Android;

/// <summary>
/// Restarts the app after "Disconnect and wipe". The process that started the launcher activity and then killed itself took the
/// new activity with it. The helper activity runs in its own process (<c>:restart</c>): it kills the old process and starts the
/// launcher, which comes up in a fresh one.
/// </summary>
public static class ProcessRestart
{
    public static void Now()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(RestartActivity));
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask | ActivityFlags.NoAnimation);
        intent.PutExtra(RestartActivity.ExtraPid, Process.MyPid());
        context.StartActivity(intent);
        // The helper kills this process as well; this is for the case that it never gets to run.
        new Handler(Looper.MainLooper!).PostDelayed(() => Process.KillProcess(Process.MyPid()), 4000);
    }
}

[Activity(
    Process = ":restart",
    Exported = false,
    NoHistory = true,
    ExcludeFromRecents = true,
    TaskAffinity = "",
    Theme = "@android:style/Theme.Translucent.NoTitleBar")]
public class RestartActivity : global::Android.App.Activity
{
    public const string ExtraPid = "bmb_restart_pid";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var pid = Intent?.GetIntExtra(ExtraPid, -1) ?? -1;
        if (pid > 0 && pid != Process.MyPid()) Process.KillProcess(pid);

        if (PackageManager?.GetLaunchIntentForPackage(PackageName!) is { } launch)
        {
            launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
            StartActivity(launch);
        }
        Finish();
    }
}
