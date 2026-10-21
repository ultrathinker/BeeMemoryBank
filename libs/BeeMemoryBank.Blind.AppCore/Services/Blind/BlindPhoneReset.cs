using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;
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
    /// <param name="dataDir">The folder of THIS copy (an account always passes its own). When null it is the folder the composition was built for
    /// (<see cref="IBlindPaths"/>); there is no fallback to the app's general data folder, which holds things that are not ours.</param>
    /// <param name="deletePath">How a file or folder is removed; tests inject a failing one.</param>
    /// <param name="quiesceTimeout">The bound of the wait; <see cref="DefaultQuiesceTimeout"/> when null.</param>
    public static async Task WipeAsync(IServiceProvider services, string? dataDir = null, Action<string>? deletePath = null,
        TimeSpan? quiesceTimeout = null, CancellationToken ct = default)
    {
        dataDir ??= services.GetService<IBlindPaths>()?.DataDirectory
            ?? throw new ArgumentException("The wipe needs the folder of the copy it wipes: pass it, or register IBlindPaths.", nameof(dataDir));
        var failures = new List<Exception>();
        void Step(string what, Action action)
        {
            try { action(); }
            // The kind of failure, and no inner exception: a head that tells the whole chain of what this throws (its message and everything inside it) would read the path of the file
            // that could not be removed or the alias of the key that could not be forgotten.
            catch (Exception ex) { failures.Add(new IOException($"{what}: {BlindFailureText.Say(ex)}")); }
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
        // Pooled handles keep the database files open; release them before the files go. Only THIS copy's pool: another account of the same
        // process has its own database and must not lose its idle connections.
        Step("closing the database", () => ClosePool(services.GetService<DbConnectionFactory>()));

        var delete = deletePath ?? DeletePath;
        foreach (var path in OwnedPaths(dataDir))
            Step($"removing {Path.GetRelativePath(dataDir, path)}", () => delete(path));

        if (failures.Count > 0)
            throw new AggregateException(
                "The wipe is not complete: " + string.Join("; ", failures.Select(f => f.Message)), failures);
    }

    /// <summary>Releases the idle pooled connections of one database file (the pool is keyed by the connection string, so it is the file's).</summary>
    internal static void ClosePool(DbConnectionFactory? factory)
    {
        if (factory is null) return;
        using var probe = new SqliteConnection(factory.DatabaseId);
        SqliteConnection.ClearPool(probe);
    }

    /// <summary>
    /// What the blind copy keeps in <paramref name="dataDir"/>, and nothing else there: the data folder also
    /// holds the app's Preferences and other things that are not ours to remove. For an account that lives in the app's root
    /// (the one a 2.6 install had) this list is also what removing the account deletes.
    /// </summary>
    internal static IEnumerable<string> OwnedPaths(string dataDir)
    {
        yield return BlindPaths.Backups(dataDir);
        yield return BlindPaths.Replica(dataDir);
        yield return Path.Combine(dataDir, "media");
        yield return BlindPaths.Log(dataDir);
        // The sealed baseline the copy holds (BaselineStore: <data>/baselines) and what it counted today (usage.json).
        yield return BlindPaths.Baselines(dataDir);
        yield return BlindPaths.Usage(dataDir);
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
    public static string Baselines(string dataDir) => Path.Combine(dataDir, "baselines");
    public static string Usage(string dataDir) => Path.Combine(dataDir, "usage.json");
}
