using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services.BlindBackup;

/// <summary>
/// The blind node's backup pipeline (plan §7): consistent DB snapshot → restic backup →
/// recovery-set next to the repo → retention. Runs as one job under the
/// <see cref="BlindJobManager"/>'s CPU mode, with progress fed by restic's --json status lines.
///
/// <para>Consistency comes from <c>VACUUM INTO</c>, not from copying the live database file: the
/// Api keeps writing (peers sync at any moment), a raw copy could catch a half-written page.
/// The vacuumed copy lands in a staging directory that is wiped both in a finally and at
/// construction — a crashed backup must not leave a second copy of the vault lying around.</para>
/// </summary>
public sealed class BlindBackupService(
    IDbConnectionFactory connFactory,
    BlindBackupSettingsStore settingsStore,
    IResticRunner restic,
    IRecoverySetSource recoverySet,
    BlindJobManager jobs,
    RecoverySetWriter recoverySetWriter,
    ResticRepositoryLock repoLock,
    string dataPath,
    ILogger<BlindBackupService> logger)
{
    /// <summary>restic's --host for every snapshot and retention run: the container's own hostname
    /// changes on every recreate, which would split retention groups and keep far more snapshots
    /// than the policy says.</summary>
    public const string ResticHost = "bmb-blind";

    // VACUUM INTO writes a second copy of the database while the original keeps its WAL; restic
    // then reads it and builds pack files. 2.5x the database size is the tested headroom (plan §7).
    public const double FreeSpaceMultiplier = 2.5;

    private string StageDir => Path.Combine(dataPath, "blind", "stage");
    private string StageDb => Path.Combine(StageDir, "beememorybank.db");
    private string MediaDir => Path.Combine(dataPath, "media");

    public BlindBackupService CleanupStaleStage()
    {
        // Startup half of "temp file removed in finally AND at startup": finally does not run for
        // SIGKILL, a power cut, or an OOM.
        try
        {
            if (Directory.Exists(StageDir))
                foreach (var f in Directory.EnumerateFiles(StageDir))
                    File.Delete(f);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clean the backup staging directory at startup");
        }
        return this;
    }

    /// <summary>Starts a backup job; null when another job is already running.</summary>
    public RunningJob? TryStartBackup() =>
        jobs.TryStart("backup", RunBackupAsync);

    /// <summary>Starts a repo check job; null when another job is already running.</summary>
    public RunningJob? TryStartVerify(bool full) =>
        jobs.TryStart("verify", (ctx, ct) => RunVerifyAsync(ctx, full, ct));

    /// <summary>
    /// Checks a one-off copy destination: absolute, outside the node's data folder (see
    /// <see cref="BlindPaths"/>) and apart from the configured folder repository — a copy inside
    /// the repository would be pruned with it, and a copy that IS the repository is no copy.
    /// Null when acceptable, else the reason.
    /// </summary>
    public string? CheckCopyDestination(string? destination) =>
        CheckCopyDestination(destination, settingsStore.Load());

    /// <summary>
    /// The same check against given settings — inside the copy job always the job's own
    /// snapshot: the repository the copy reads from is the one the destination must stay apart
    /// from, whatever settings.json says by now.
    /// </summary>
    private string? CheckCopyDestination(string? destination, BlindBackupSettings s)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return "a destination folder is required";
        if (BlindPaths.OutsideNodeData(destination, dataPath, "destination") is { } problem)
            return problem;
        if (s.RepoType == BlindRepoType.Folder && !string.IsNullOrWhiteSpace(s.RepoFolder)
            && (BlindPaths.RealPath(destination) is not { } dest || BlindPaths.RealPath(s.RepoFolder) is not { } repo
                || BlindPaths.Overlaps(dest, repo)))
            return $"the destination {destination} overlaps the backup repository {s.RepoFolder}";
        return null;
    }

    /// <summary>Starts "Save a copy to…"; null when another job is already running.</summary>
    public RunningJob? TryStartCopy(string destination) =>
        jobs.TryStart("copy", (ctx, ct) => RunCopyAsync(ctx, destination, ct));

    internal async Task RunBackupAsync(BlindJobContext ctx, CancellationToken ct)
    {
        var s = settingsStore.Load();
        if (s.Validate(dataPath) is { Ok: false, Problem: { } problem })
            throw new InvalidOperationException($"backup is not configured: {problem}");

        await ctx.WaitForResumeAsync(ct);

        Directory.CreateDirectory(StageDir);
        try
        {
            ctx.ReportProgress(null, null, null, "vacuuming database");
            await VacuumIntoStageAsync(ctx, ct);
            await ctx.WaitForResumeAsync(ct);

            // Again right before restic opens (or creates) it: the job was validated before the
            // vacuum and a pause — time enough for a symlink on the way to be pointed elsewhere.
            RecheckRepositoryLocation(s);
            await EnsureRepoAsync(s, ct);
            await ClearStaleLocksAsync(s, ct);
            ctx.ReportProgress(null, null, null, "backing up");

            var args = new List<string> { "backup", "--json", "--host", ResticHost, "--tag", "bmb" };
            args.Add(StageDb);
            if (Directory.Exists(MediaDir)) args.Add(MediaDir);
            var backup = await RunResticAsync(new ResticCall(s, args, ctx), ct);
            if (backup.ExitCode != 0)
                throw new InvalidOperationException($"restic backup failed ({backup.ExitCode}): {Truncate(backup.Stderr, 400)}");

            await ctx.WaitForResumeAsync(ct);

            // Recovery-set AFTER the snapshot exists (plan §6.8: updated after every backup) and
            // OUTSIDE the repository tree (see RecoverySetWriter).
            ctx.ReportProgress(1.0, null, null, "writing recovery-set");
            if (await recoverySet.BuildAsync(ct) is { } setJson)
                await recoverySetWriter.WriteAsync(s, setJson, ct);

            await RetainAsync(ctx, s, ct);
        }
        finally
        {
            try { if (File.Exists(StageDb)) File.Delete(StageDb); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove the staging database"); }
        }
    }

    private async Task VacuumIntoStageAsync(BlindJobContext ctx, CancellationToken ct)
    {
        var dbPath = Path.Combine(dataPath, "beememorybank.db");
        if (!File.Exists(dbPath))
            throw new InvalidOperationException($"no vault database at {dbPath} — nothing to back up");

        var dbSize = new FileInfo(dbPath).Length;
        EnsureFreeSpace(requiredBytes: (long)(dbSize * FreeSpaceMultiplier),
            availableBytes: new DriveInfo(Path.GetFullPath(dataPath)).AvailableFreeSpace);

        // Sync ADO on purpose: IDbConnection (the factory's contract) has no async surface, and
        // this runs on the job's own thread where blocking is the job.
        using var conn = connFactory.CreateConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        // VACUUM INTO cannot run inside a transaction and takes its target as an SQL expression —
        // escape it as a string literal rather than interpolating a path raw.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"VACUUM INTO {QuoteSql(StageDb)}";
        cmd.ExecuteNonQuery();
    }

    /// <summary>The ×2.5 free-space invariant, on its own line so a test can pin it.</summary>
    internal static void EnsureFreeSpace(long requiredBytes, long availableBytes)
    {
        if (availableBytes < requiredBytes)
            throw new InvalidOperationException(
                $"not enough free space: backup needs ~{requiredBytes / 1_000_000} MB (2.5× database size), " +
                $"{availableBytes / 1_000_000} MB free. Free space on the data volume or move the repository.");
    }

    /// <summary>
    /// <c>restic unlock</c> before every job — the plain form, which removes only STALE locks
    /// (a dead process on this host, or older than 30 minutes), never <c>--remove-all</c>.
    /// restic does not skip stale locks on its own when it needs an exclusive one: a restic
    /// killed mid-run (restart, OOM, power cut, the wipe's cancel) would otherwise fail every
    /// later forget/prune — and every backup after a killed prune — until someone ran unlock by
    /// hand. Seen on a test host. Safe because the job manager never runs two jobs at once, so
    /// no live lock of ours can be in the repository at this point. A failure here is logged, not
    /// fatal: the step that needs the lock reports the real problem.
    /// </summary>
    private async Task ClearStaleLocksAsync(BlindBackupSettings s, CancellationToken ct)
    {
        var unlock = await RunResticAsync(new ResticCall(s, ["unlock"]), ct);
        if (unlock.ExitCode != 0)
            logger.LogWarning("restic unlock exited {Code}: {Err}", unlock.ExitCode, Truncate(unlock.Stderr, 300));
    }

    private async Task EnsureRepoAsync(BlindBackupSettings s, CancellationToken ct)
    {
        var probe = await RunResticAsync(
            new ResticCall(s, ["snapshots", "--host", ResticHost, "--json"]), ct);
        if (probe.RepoMissing)
        {
            var init = await RunResticAsync(new ResticCall(s, ["init"]), ct);
            if (init.ExitCode != 0)
                throw new InvalidOperationException($"restic init failed ({init.ExitCode}): {Truncate(init.Stderr, 400)}");
        }
        else if (probe.ExitCode != 0)
        {
            throw new InvalidOperationException($"repository unreachable ({probe.ExitCode}): {Truncate(probe.Stderr, 400)}");
        }
        // The repository exists now, under the password this run used: the console password must not
        // move that password any more (BlindBackupSettingsStore.AdoptConsolePassword).
        settingsStore.MarkRepositoryInUse();
    }

    private async Task RetainAsync(BlindJobContext ctx, BlindBackupSettings s, CancellationToken ct)
    {
        ctx.ReportProgress(1.0, null, null, "applying retention");
        var args = new List<string>
        {
            "forget", "--host", ResticHost, "--prune",
            "--keep-daily", s.KeepDaily.ToString(),
            "--keep-weekly", s.KeepWeekly.ToString(),
            "--keep-monthly", s.KeepMonthly.ToString(),
            "--keep-yearly", s.KeepYearly.ToString(),
        };
        var forget = await RunResticAsync(new ResticCall(s, args, ctx), ct);
        if (forget.ExitCode != 0)
            // The snapshot itself is safe; retention is a policy job. A failed forget must fail
            // the job visibly (an ever-growing repo is how "backups work" turns into "disk full").
            throw new InvalidOperationException($"restic forget failed ({forget.ExitCode}): {Truncate(forget.Stderr, 400)}");
    }

    internal async Task RunVerifyAsync(BlindJobContext ctx, bool full, CancellationToken ct)
    {
        var s = settingsStore.Load();
        if (s.Validate(dataPath) is { Ok: false, Problem: { } problem })
            throw new InvalidOperationException($"backup is not configured: {problem}");

        await ctx.WaitForResumeAsync(ct);
        RecheckRepositoryLocation(s);
        await ClearStaleLocksAsync(s, ct);
        ctx.ReportProgress(null, null, null, full ? "full check" : $"checking {s.CheckSubsetPercent}% sample");
        var args = full
            ? new List<string> { "check", "--read-data" }
            : new List<string> { "check", $"--read-data-subset={s.CheckSubsetPercent}%" };
        var check = await RunResticAsync(new ResticCall(s, args, ctx), ct);
        if (check.ExitCode != 0)
            throw new InvalidOperationException($"restic check failed ({check.ExitCode}): {Truncate(check.Stderr, 400)}");
    }

    /// <summary>
    /// "Save a copy to…" (plan §7): the node's snapshots copied into a restic repository in
    /// another folder — a USB disk, a NAS mount. <c>restic copy</c>, not an export: the copy stays
    /// encrypted under the same repository password, is incremental when repeated into the same
    /// folder, and is checked and restored with the same tools as the main repository. An export
    /// (restic restore into the folder) would put the vault database — its metadata is open by
    /// design — as a plain file on a stick. The recovery-set goes next to the copy as well, so the
    /// copy alone is enough for a restore.
    /// </summary>
    internal async Task RunCopyAsync(BlindJobContext ctx, string destination, CancellationToken ct)
    {
        var s = settingsStore.Load();
        if (s.Validate(dataPath) is { Ok: false, Problem: { } problem })
            throw new InvalidOperationException($"backup is not configured: {problem}");
        // Checked again inside the job, against the job's settings snapshot.
        if (CheckCopyDestination(destination, s) is { } bad)
            throw new InvalidOperationException(bad);

        var dest = Path.GetFullPath(destination);
        await ctx.WaitForResumeAsync(ct);
        // Both locations again after the pause, before any restic call (see
        // RecheckRepositoryLocation) — and against the SAME snapshot: comparing the destination
        // with repository B from a newer settings.json while this job reads repository A would
        // let a retargeted link put the copy inside A.
        RecheckRepositoryLocation(s);
        if (CheckCopyDestination(destination, s) is { } moved)
            throw new InvalidOperationException(moved);
        await ClearStaleLocksAsync(s, ct);
        Directory.CreateDirectory(dest);
        if (!File.Exists(Path.Combine(dest, "config")))
        {
            ctx.ReportProgress(null, null, null, "creating the copy repository");
            // Same chunker parameters as the source, or every copied blob would be re-chunked
            // and deduplication against later copies lost.
            var init = await RunResticAsync(new ResticCall(s, ["init", "--copy-chunker-params"],
                Repository: dest, FromConfigured: true), ct);
            if (init.ExitCode != 0)
                throw new InvalidOperationException($"restic init of the copy failed ({init.ExitCode}): {Truncate(init.Stderr, 400)}");
        }

        // The destination's stale locks too, not only the source's: a copy cancelled or killed
        // earlier leaves its lock there, and the next copy — no init, the repository exists —
        // would fail on it for good.
        var unlock = await RunResticAsync(new ResticCall(s, ["unlock"], Repository: dest), ct);
        if (unlock.ExitCode != 0)
            logger.LogWarning("restic unlock of the copy exited {Code}: {Err}", unlock.ExitCode, Truncate(unlock.Stderr, 300));

        await ctx.WaitForResumeAsync(ct);
        ctx.ReportProgress(null, null, null, "copying snapshots");
        var copy = await RunResticAsync(new ResticCall(s, ["copy", "--host", ResticHost], ctx,
            Repository: dest, FromConfigured: true), ct);
        if (copy.ExitCode != 0)
            throw new InvalidOperationException($"restic copy failed ({copy.ExitCode}): {Truncate(copy.Stderr, 400)}");

        if (await recoverySet.BuildAsync(ct) is { } setJson)
            await recoverySetWriter.WriteAsync(
                new BlindBackupSettings { RepoType = BlindRepoType.Folder, RepoFolder = dest }, setJson, ct);
        ctx.ReportProgress(1.0, null, null, $"copied to {dest}");
    }

    /// <summary>The console's copy list: raw restic snapshots JSON (id, time, size, paths).</summary>
    public async Task<string> ListSnapshotsJsonAsync(CancellationToken ct)
    {
        var s = settingsStore.Load();
        if (s.Validate(dataPath) is { Ok: false, Problem: { } problem })
            throw new InvalidOperationException($"backup is not configured: {problem}");
        // Not a job, so it does not wait: during a backup or a wipe the console gets "busy"
        // instead of a second restic. The reservation comes from the manager (atomic with the
        // wipe gate: a wipe that starts now waits for it), then the repository lock.
        using var reserved = jobs.TryReserveRead() ?? throw new ResticRepositoryBusyException();
        using var held = repoLock.TryAcquire() ?? throw new ResticRepositoryBusyException();
        var r = await restic.RunAsync(new ResticCall(s, ["snapshots", "--host", ResticHost, "--json"]), ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"restic snapshots failed ({r.ExitCode}): {Truncate(r.Stderr, 400)}");
        return string.IsNullOrWhiteSpace(r.Stdout) ? "[]" : r.Stdout;
    }

    /// <summary>
    /// The folder repository's location, checked at the moment of use rather than only when the
    /// job started: validation resolves symlinks, and a link can change between the two.
    /// </summary>
    private void RecheckRepositoryLocation(BlindBackupSettings s)
    {
        if (s.RepoType == BlindRepoType.Folder
            && BlindPaths.OutsideNodeData(s.RepoFolder!, dataPath, "repository folder") is { } problem)
            throw new InvalidOperationException(problem);
    }

    /// <summary>A job's restic call: waits for the repository lock, holds it for this one process.</summary>
    private async Task<ResticResult> RunResticAsync(ResticCall call, CancellationToken ct)
    {
        using var held = await repoLock.AcquireAsync(ct);
        return await restic.RunAsync(call, ct);
    }

    private static string QuoteSql(string path) => "'" + path.Replace("'", "''") + "'";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
