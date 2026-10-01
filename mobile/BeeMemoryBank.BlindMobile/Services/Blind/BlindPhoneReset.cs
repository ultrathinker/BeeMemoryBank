using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// "Disconnect and wipe" of the blind copy (plan section 10): stops its background work, forgets its
/// keys (AndroidKeyStore), state, backups and log, deletes the local database and restarts the app.
/// The network learns of it by the node's silence and by revoking it on
/// Windows — the phone has no way to tell anyone, it holds no authority.
/// </summary>
public static class BlindPhoneReset
{
    public static void WipeAndRestart(IServiceProvider services)
    {
        var dataDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
#if ANDROID
        Platforms.Android.BlindWorkScheduler.Cancel(Platform.AppContext);
        Platform.AppContext.StopService(new Android.Content.Intent(Platform.AppContext, typeof(Platforms.Android.BlindBackupService)));
#endif
        services.GetRequiredService<IBlindPhoneKeys>().Clear();
        services.GetRequiredService<BlindPhoneState>().Clear();
        foreach (var dir in new[] { BlindPaths.Backups(dataDir), BlindPaths.Replica(dataDir) })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // The WAL sidecars too: an orphaned -wal next to a fresh db is replayed on the next open.
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var f = Path.Combine(dataDir, "beememorybank.db" + suffix);
            if (File.Exists(f)) File.Delete(f);
        }
        if (File.Exists(BlindPaths.Log(dataDir))) File.Delete(BlindPaths.Log(dataDir));

#if ANDROID
        // Restart the process: the only reliable way to reset every singleton and migrate a fresh db.
        var intent = Android.App.Application.Context.PackageManager!
            .GetLaunchIntentForPackage(Android.App.Application.Context.PackageName!)!;
        intent.AddFlags(Android.Content.ActivityFlags.ClearTop | Android.Content.ActivityFlags.NewTask);
        Android.App.Application.Context.StartActivity(intent);
        Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
#endif
    }
}

/// <summary>Where the blind copy keeps its files, under the app's private data directory.</summary>
public static class BlindPaths
{
    public static string Backups(string dataDir) => Path.Combine(dataDir, "blind-backups");
    public static string Replica(string dataDir) => Path.Combine(dataDir, "blind-replica");
    public static string Log(string dataDir) => Path.Combine(dataDir, "blind-log.jsonl");
}
