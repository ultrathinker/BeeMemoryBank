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
    /// <summary>How long the wipe waits for a running sync, first load or backup to end before it gives up (and deletes nothing).</summary>
    public static readonly TimeSpan DefaultQuiesceTimeout = TimeSpan.FromSeconds(20);

    /// <inheritdoc cref="WipeAsync"/>
    public static void Wipe(IServiceProvider services, string? dataDir = null, Action<string>? deletePath = null, TimeSpan? quiesceTimeout = null) =>
        WipeAsync(services, dataDir, deletePath, quiesceTimeout).GetAwaiter().GetResult();

    /// <summary>
    /// Order: new work is refused and the running work is told to stop (<see cref="BlindActivity"/>, the scheduler, the backup service), then the
    /// wipe WAITS, bounded, until every sync, first load and backup has really ended. Only then keys, state and files go. If something still
    /// runs when the bound passes, nothing has been touched: the work is allowed again and an <see cref="AggregateException"/> says to press
    /// the button again (a job that is still writing must never find its files and keys deleted beneath it).
    /// </summary>
    /// <param name="deletePath">How a file or folder is removed; tests inject a failing one.</param>
    /// <param name="quiesceTimeout">The bound of the wait; <see cref="DefaultQuiesceTimeout"/> when null.</param>
    public static async Task WipeAsync(IServiceProvider services, string? dataDir = null, Action<string>? deletePath = null,
        TimeSpan? quiesceTimeout = null, CancellationToken ct = default)
    {
        dataDir ??= System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var failures = new List<Exception>();
        void Step(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { failures.Add(new IOException($"{what}: {ex.Message}", ex)); }
        }

        var activity = services.GetService<BlindActivity>();
        activity?.Close();
        activity?.CancelAll();
        Step("stopping the background work", () => services.GetRequiredService<IBlindLifecycle>().StopBackgroundWork());
        Step("stopping the backup service", () => services.GetRequiredService<IBlindLifecycle>().StopBackupService());

        if (activity is not null && !await activity.WaitIdleAsync(quiesceTimeout ?? DefaultQuiesceTimeout, ct).ConfigureAwait(false))
        {
            var running = string.Join(", ", activity.ActiveKinds().Distinct());
            activity.Reopen();
            // The stop above cancelled the host's schedule: give it back, so the copy keeps syncing while the user tries again.
            Step("resuming the background work", () => services.GetService<IBlindLifecycle>()?.ResumeBackgroundWork());
            failures.Add(new IOException($"still running: {running}"));
            throw new AggregateException(
                "The wipe is not complete: the phone's work did not stop in time (" + running + "). Nothing was deleted and the keys are kept; "
                + string.Join("; ", failures.Select(f => f.Message)), failures);
        }

        Step("forgetting the keys", () => services.GetRequiredService<IBlindPhoneKeys>().Clear());
        Step("forgetting the state", () => services.GetRequiredService<BlindPhoneState>().Clear());
        Step("removing the state file", () => services.GetService<IBlindStateStore>()?.Erase());
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

    public static void WipeAndRestart(IServiceProvider services, string? dataDir = null) =>
        WipeAndRestartAsync(services, dataDir).GetAwaiter().GetResult();

    public static async Task WipeAndRestartAsync(IServiceProvider services, string? dataDir = null, TimeSpan? quiesceTimeout = null,
        CancellationToken ct = default)
    {
        await WipeAsync(services, dataDir, null, quiesceTimeout, ct).ConfigureAwait(false);
        services.GetService<IBlindLifecycle>()?.RestartAfterWipe();
    }
}

/// <summary>Where the blind copy keeps its files, under the app's private data directory.</summary>
public static class BlindPaths
{
    public static string Backups(string dataDir) => Path.Combine(dataDir, "blind-backups");
    public static string Replica(string dataDir) => Path.Combine(dataDir, "blind-replica");
    public static string Log(string dataDir) => Path.Combine(dataDir, "blind-log.jsonl");
}
