using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// "Disconnect and wipe" of the blind copy (plan section 10): stops its background work, forgets its
/// secrets (the AndroidKeyStore blobs: identity seed, pairing secret, backup key), its state, and deletes
/// everything it keeps in the app's data folder — the replica database with its rollback copies and
/// candidates, the media blobs, the backups, the replica work folder, the log — so that what starts next
/// is a fresh blind app. The network learns of it by the node's silence and by revoking it on
/// Windows — the phone has no way to tell anyone, it holds no authority.
///
/// <para>Order: secrets and state first, files after. A step that fails does not stop the others (a wipe
/// that stops at the first locked file would leave the rest of the copy behind); the failures are reported
/// together, naming what is left, and the app is not restarted over them.</para>
/// </summary>
public static class BlindPhoneReset
{
    /// <param name="deletePath">How a file or folder is removed; tests inject a failing one.</param>
    public static void Wipe(IServiceProvider services, string? dataDir = null, Action<string>? deletePath = null)
    {
        dataDir ??= System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var failures = new List<Exception>();
        void Step(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { failures.Add(new IOException($"{what}: {ex.Message}", ex)); }
        }

#if ANDROID
        Step("stopping the background work", () => Platforms.Android.BlindWorkScheduler.Cancel(Platform.AppContext));
        Step("stopping the backup service", () =>
            Platform.AppContext.StopService(new Android.Content.Intent(Platform.AppContext, typeof(Platforms.Android.BlindBackupService))));
#endif
        Step("forgetting the keys", () => services.GetRequiredService<IBlindPhoneKeys>().Clear());
        Step("forgetting the state", () => services.GetRequiredService<BlindPhoneState>().Clear());
        Step("closing the connections", () => services.GetService<BlindHttpClientProvider>()?.Invalidate());
        // Pooled handles keep the database files open; release them before the files go.
        Step("closing the database", Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools);

        var delete = deletePath ?? DeletePath;
        foreach (var path in OwnedPaths(dataDir))
            Step($"removing {Path.GetRelativePath(dataDir, path)}", () => delete(path));

        if (failures.Count > 0)
            throw new AggregateException(
                "The wipe is not complete: " + string.Join("; ", failures.Select(f => f.Message)), failures);
    }

    /// <summary>
    /// What the blind copy keeps in <paramref name="dataDir"/>, and nothing else there: the data folder also
    /// holds the app's Preferences and other things that are not ours to remove.
    /// </summary>
    private static IEnumerable<string> OwnedPaths(string dataDir)
    {
        yield return BlindPaths.Backups(dataDir);
        yield return BlindPaths.Replica(dataDir);
        yield return Path.Combine(dataDir, "media");
        yield return BlindPaths.Log(dataDir);
        if (!Directory.Exists(dataDir)) yield break;
        // The live database with its sidecars (-wal, -shm, -journal), the rollback copies a replica switch leaves
        // (.before-replica-*) and the candidates it builds (beememorybank.replica-*.db with theirs): an orphaned
        // -wal next to a fresh database is replayed on the next open.
        foreach (var pattern in new[] { "beememorybank.db*", "beememorybank.replica-*" })
            foreach (var file in Directory.GetFiles(dataDir, pattern))
                yield return file;
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    public static void WipeAndRestart(IServiceProvider services, string? dataDir = null)
    {
        Wipe(services, dataDir);
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
