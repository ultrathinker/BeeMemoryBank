using System.Security.Cryptography;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Rekey;

/// <summary>A step that needs the owner's credential (KeyMaterialStep rewrites the owner's slot under the password).
/// The runner hands it over before the step runs; nothing else ever sees the password.</summary>
public interface IRekeyOwnerCredentialConsumer
{
    void SetOwnerCredential(int ownerSlotId, string ownerPassword);
}

/// <summary>§8.4 exit codes.</summary>
public enum RekeyExit
{
    Done = 0,
    PreflightRefused = 2,
    FailedBeforeSwap = 3,
    SwapPending = 4,
}

public sealed record RekeyOutcome(RekeyExit Exit, string Result, string? ReportPath, string Message);

public sealed class RekeyOptions
{
    public required string DataDir { get; init; }
    public required string OwnerPassword { get; init; }
    public IRekeyProgress Progress { get; init; } = NullRekeyProgress.Instance;
    /// <summary>The steps, in order; the verb passes <see cref="RekeyPlan.Steps"/>.</summary>
    public required IReadOnlyList<IRekeyStep> Steps { get; init; }
    /// <summary>The pre-flight; the verb passes <see cref="RekeyPlan.Preflight"/>.</summary>
    public IRekeyPreflight? Preflight { get; init; }
    /// <summary>The fates the copy is checked against; default <see cref="RekeyTables.Main"/> and <see cref="RekeyTables.Chat"/>.</summary>
    public IReadOnlyDictionary<string, TableFate>? MainTables { get; init; }
    public IReadOnlyDictionary<string, TableFate>? ChatTables { get; init; }
    public DateTimeOffset? Now { get; init; }
    /// <summary>Tests only: called at each named point of the run.</summary>
    public Action<string>? Fault { get; init; }
}

public sealed class NullRekeyProgress : IRekeyProgress
{
    public static readonly NullRekeyProgress Instance = new();
    public void Report(string step, long done, long total, string? note = null) { }
}

/// <summary>
/// The offline re-key (rekey-offline.md §2): lock, pre-flight, copy, the steps of the plan on the copy, verify
/// (every step's own check, the D1-attacker check, <c>integrity_check</c>), scrub, report, swap. The node is stopped
/// and stays stopped: the verb holds <c>D/node.lock</c> and <c>&lt;D&gt;.rekey.lock</c> throughout. D is only read.
/// Any failure before the swap leaves D in use and throws the copy away; there is no resume.
/// </summary>
public static class RekeyRunner
{
    /// <summary>The steps the plan must hold, by name: a missing one fails the run before anything is created.</summary>
    public static readonly IReadOnlyList<string> RequiredSteps =
        ["KeyMaterial", "RowReseal", "ChatRekey", "DerivedDataClear", "PeerRevoke", "EventLogReset"];

    public const string MainDb = "beememorybank.db", ChatDb = "chat.db", ReportFile = RekeyReport.FileName;

    public const string FaultAfterCopy = "after-copy", FaultAfterSteps = "after-steps", FaultAfterVerify = "after-verify",
        FaultAfterScrub = "after-scrub";

    /// <summary>
    /// Where the report of a run that stopped before the swap goes: <c>&lt;D&gt;.rekey-report/rekey-report.json</c>, next to
    /// D, so a refusal still creates no <c>D.rekey-new</c>. A later successful run's report is in the new D.
    /// </summary>
    public static string StoppedReportDirFor(string dataDir) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir)) + ".rekey-report";

    public static async Task<RekeyOutcome> RunAsync(RekeyOptions options, CancellationToken ct = default)
    {
        var d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DataDir));
        var log = new RekeyRunLog { StartedAt = (options.Now ?? DateTimeOffset.UtcNow).UtcDateTime };
        var outcome = await RunCoreAsync(options, d, log, ct);
        if (log.PastLock && outcome.Exit is RekeyExit.PreflightRefused or RekeyExit.FailedBeforeSwap)
        {
            var dir = StoppedReportDirFor(d);
            Directory.CreateDirectory(dir);
            RekeyReport.Write(dir, log.ToReport(
                outcome.Exit == RekeyExit.PreflightRefused ? RekeyReport.PreflightRefused : RekeyReport.Failed, outcome.Message));
            outcome = outcome with { ReportPath = Path.Combine(dir, RekeyReport.FileName) };
        }
        return outcome;
    }

    private static async Task<RekeyOutcome> RunCoreAsync(RekeyOptions options, string d, RekeyRunLog report, CancellationToken ct)
    {
        var progress = options.Progress;
        var now = options.Now ?? DateTimeOffset.UtcNow;

        if (!Directory.Exists(d) || !File.Exists(Path.Combine(d, MainDb)))
            return Fail(RekeyExit.FailedBeforeSwap, $"{d} holds no vault ({MainDb} is missing).");
        if (RekeySwapJournal.Read(d) != null)
            return new(RekeyExit.SwapPending, RekeyReport.SwapPending, null,
                $"A swap is pending ({RekeySwapJournal.PathFor(d)}): start the node to finish it before re-keying again.");

        // Step 0: the order matters. The re-key lock first, so a node starting now refuses; then the node's own
        // lock, which fails while a node runs and, held, keeps one from starting until the verb is done.
        using var rekeyLock = RekeyLock.TryAcquire(d);
        if (rekeyLock == null) return Fail(RekeyExit.FailedBeforeSwap, "Another re-key of this vault is running.");
        var succeeded = false;
        var newDir = RekeySwapJournal.NewDirFor(d);
        report.PastLock = true;
        if (Directory.Exists(StoppedReportDirFor(d))) Directory.Delete(StoppedReportDirFor(d), recursive: true); // an older run's
        try
        {
            using var nodeLock = RekeyLock.TryAcquireNodeLock(d);
            if (nodeLock == null)
                return Fail(RekeyExit.FailedBeforeSwap, "The node is running (node.lock is held): stop it first.");

            var missing = RequiredSteps.Where(n => options.Steps.All(s => s.Name != n)).ToList();
            if (missing.Count > 0 || options.Preflight == null)
                return Fail(RekeyExit.FailedBeforeSwap,
                    "This build cannot re-key: the plan lacks " + string.Join(", ", missing.Concat(options.Preflight == null ? ["the pre-flight"] : [])) + ".");

            progress.Report("lock", 1, 1);
            using var live = OpenLive(Path.Combine(d, MainDb));
            using var liveChat = File.Exists(Path.Combine(d, ChatDb)) ? OpenLive(Path.Combine(d, ChatDb)) : null;

            // Keys: the owner's password opens the owner's slot to D2; D2 opens the retired keys.
            var owner = await OpenOwnerAsync(live, options.OwnerPassword);
            if (owner == null)
                return Refused(report, new RekeyPreflightReport([new("tbl_key_slot", "owner", "the password opens no superadmin key slot of this vault")], [], 0));
            using var keys = owner.Value.Keys;

            // Step 1: pre-flight, read-only on the live files.
            progress.Report("preflight", 0, 1);
            var pre = await options.Preflight.RunAsync(d, live, liveChat, keys, ct);
            report.Preflight = pre;
            if (pre.Blocking.Count > 0) return Refused(report, pre);
            var free = new DriveInfo(Path.GetPathRoot(d)!).AvailableFreeSpace;
            if (pre.BytesNeeded > 0 && free < pre.BytesNeeded)
                return Refused(report, pre with { Blocking = [new("disk", d, $"{pre.BytesNeeded} bytes needed, {free} free")] });
            progress.Report("preflight", 1, 1);

            // What the D1 check tries: gathered from the live vault before anything is re-keyed.
            var oldMaterial = await RekeyD1Check.GatherAsync(live, liveChat, keys);

            // Step 2: the copy. A copy left by an earlier attempt that died is thrown away first.
            DiscardNewDir(newDir);
            Directory.CreateDirectory(newDir);
            progress.Report("copy", 0, 2);
            await VacuumIntoAsync(live, Path.Combine(newDir, MainDb));
            if (liveChat != null) await VacuumIntoAsync(liveChat, Path.Combine(newDir, ChatDb));
            progress.Report("copy", 2, 2);
            live.Close();
            liveChat?.Close();
            options.Fault?.Invoke(FaultAfterCopy);

            // Step 3: the plan's steps on the copy.
            using (var main = OpenCopy(Path.Combine(newDir, MainDb)))
            using (var chat = File.Exists(Path.Combine(newDir, ChatDb)) ? OpenCopy(Path.Combine(newDir, ChatDb)) : null)
            {
                var nodeId = await main.ExecuteScalarAsync<string>("SELECT node_id FROM tbl_node_identity LIMIT 1");
                var ctx = new RekeyContext(d, newDir, main, chat, keys, Guid.TryParse(nodeId, out var g) ? g : Guid.Empty, progress, ct);
                await RequireClassifiedAsync(main, options.MainTables ?? RekeyTables.Main, MainDb);
                if (chat != null) await RequireClassifiedAsync(chat, options.ChatTables ?? RekeyTables.Chat, ChatDb);

                foreach (var step in options.Steps)
                {
                    ct.ThrowIfCancellationRequested();
                    if (step is IRekeyOwnerCredentialConsumer consumer) consumer.SetOwnerCredential(owner.Value.SlotId, options.OwnerPassword);
                    progress.Report(step.Name, 0, 1);
                    report.Steps.Add(await step.RunAsync(ctx));
                    progress.Report(step.Name, 1, 1);
                }
                options.Fault?.Invoke(FaultAfterSteps);

                // Step 4: verify.
                progress.Report("verify", 0, 1);
                var problems = new List<RekeyProblem>();
                foreach (var step in options.Steps) problems.AddRange(await step.VerifyAsync(ctx));
                problems.AddRange(await RekeyD1Check.CheckRowsAsync(ctx, oldMaterial));
                problems.AddRange(await IntegrityAsync(main, MainDb));
                if (chat != null) problems.AddRange(await IntegrityAsync(chat, ChatDb));
                if (problems.Count > 0) return FailVerify(report, problems);
                options.Fault?.Invoke(FaultAfterVerify);

                // Step 5: scrub both files.
                progress.Report("scrub", 0, 1);
                await ScrubAsync(main, MainDb);
                if (chat != null) await ScrubAsync(chat, ChatDb);
                progress.Report("scrub", 1, 1);
            }
            SqliteConnection.ClearAllPools();
            options.Fault?.Invoke(FaultAfterScrub);

            // The D1 byte check, on the scrubbed files: no old ciphertext survives anywhere in them.
            var leaks = RekeyD1Check.CheckBytes(newDir, oldMaterial);
            if (leaks.Count > 0) return FailVerify(report, leaks);
            progress.Report("verify", 1, 1);

            // Step 6: report into the new vault, then the swap. node.lock lives inside D: it is released first, or D
            // could not be renamed on Windows (and would carry a stray file). The re-key lock, held, still keeps a
            // node from starting.
            nodeLock.Dispose();
            report.OldVault = RekeySwapJournal.OldDirFor(d, now);
            RekeyReport.Write(newDir, report.ToReport(RekeyReport.Done, null));
            progress.Report("swap", 0, 1);
            try
            {
                RekeySwap.Swap(d, now, options.Fault);
            }
            catch when (RekeySwapJournal.Read(d) != null)
            {
                succeeded = true; // the journal finishes the swap at the next start; keep the new vault
                return new(RekeyExit.SwapPending, RekeyReport.SwapPending, Path.Combine(newDir, RekeyReport.FileName),
                    "The swap was interrupted; the next start of the node finishes it.");
            }
            succeeded = true;
            progress.Report("swap", 1, 1);
            return new(RekeyExit.Done, RekeyReport.Done, Path.Combine(d, RekeyReport.FileName), $"Re-keyed. The old vault is at {report.OldVault}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested)
        {
            return Fail(RekeyExit.FailedBeforeSwap, $"The re-key failed and the vault was not changed: {ex.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!succeeded)
            {
                // D is in use as before: the copy goes (a crash that skips this leaves it for the next run to discard).
                try { DiscardNewDir(newDir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            rekeyLock.Dispose();
            if (!succeeded) File.Delete(RekeySwapJournal.LockPathFor(d)); // nothing pending: a start needs no lock left
        }
    }

    private static RekeyOutcome Fail(RekeyExit exit, string message) => new(exit, RekeyReport.Failed, null, message);

    private static RekeyOutcome Refused(RekeyRunLog report, RekeyPreflightReport pre)
    {
        report.Preflight = pre;
        return new(RekeyExit.PreflightRefused, RekeyReport.PreflightRefused, null,
            "The pre-flight refused; nothing was created: " + string.Join("; ", pre.Blocking.Take(20).Select(p => $"{p.Table}:{p.RowKey} {p.Problem}")));
    }

    private static RekeyOutcome FailVerify(RekeyRunLog report, IReadOnlyList<RekeyProblem> problems)
    {
        return new(RekeyExit.FailedBeforeSwap, RekeyReport.Failed, null,
            $"Verify failed ({problems.Count} problem(s)); the vault was not changed: "
            + string.Join("; ", problems.Take(20).Select(p => $"{p.Table}:{p.RowKey} {p.Problem}")));
    }

    private static void DiscardNewDir(string newDir)
    {
        if (Directory.Exists(newDir)) Directory.Delete(newDir, recursive: true);
    }

    /// <summary>
    /// The live file with an exclusive lock held for the connection's life, and <c>query_only</c>: no statement can
    /// write. Not <see cref="SqliteOpenMode.ReadOnly"/>: SQLite cannot open a WAL database read-only when its -wal and
    /// -shm files are gone (a cleanly stopped node), and fails with a disk I/O error.
    /// </summary>
    public static SqliteConnection OpenLive(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        conn.Open();
        conn.Execute("PRAGMA locking_mode = EXCLUSIVE");
        conn.Execute("PRAGMA query_only = ON");
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master"); // takes the lock now
        return conn;
    }

    private static SqliteConnection OpenCopy(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        conn.Execute("PRAGMA locking_mode = EXCLUSIVE");
        return conn;
    }

    /// <summary>VACUUM INTO writes only the target, but query_only refuses it: lifted for this one statement.</summary>
    private static async Task VacuumIntoAsync(SqliteConnection conn, string target)
    {
        await conn.ExecuteAsync("PRAGMA query_only = OFF");
        try { await conn.ExecuteAsync("VACUUM INTO @target", new { target }); }
        finally { await conn.ExecuteAsync("PRAGMA query_only = ON"); }
    }

    private static async Task RequireClassifiedAsync(SqliteConnection conn, IReadOnlyDictionary<string, TableFate> fates, string file)
    {
        var unlisted = (await conn.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT IN ('sqlite_stat1', 'sqlite_stat4')"))
            .Where(t => !fates.ContainsKey(t)).ToList();
        if (unlisted.Count > 0)
            throw new InvalidOperationException($"{file} has tables no re-key step accounts for: {string.Join(", ", unlisted)}.");
    }

    private static async Task<IReadOnlyList<RekeyProblem>> IntegrityAsync(SqliteConnection conn, string file)
    {
        var rows = (await conn.QueryAsync<string>("PRAGMA integrity_check")).ToList();
        return rows is ["ok"] ? [] : rows.Select(r => new RekeyProblem(file, "integrity_check", r)).ToList();
    }

    /// <summary>§2 step 5: freed pages zeroed and dropped, the WAL (if any) emptied, checked.</summary>
    private static async Task ScrubAsync(SqliteConnection conn, string file)
    {
        await conn.ExecuteAsync("PRAGMA secure_delete = ON");
        await conn.ExecuteAsync("VACUUM");
        var (busy, _, _) = await conn.QuerySingleAsync<(long, long, long)>("PRAGMA wal_checkpoint(TRUNCATE)");
        if (busy != 0) throw new InvalidOperationException($"The WAL checkpoint of {file} could not complete.");
        if (await conn.ExecuteScalarAsync<long>("PRAGMA freelist_count") != 0)
            throw new InvalidOperationException($"{file} still has free pages after VACUUM.");
    }

    /// <summary>
    /// The owner: an active superadmin whose <c>user</c> slot the password opens to the key the sentinel names (or a
    /// legacy <c>password</c> slot, which predates users). D2 then opens every retired key stored under it.
    /// </summary>
    private static async Task<(int SlotId, RekeyKeys Keys)?> OpenOwnerAsync(SqliteConnection live, string password)
    {
        var sentinel = await live.ExecuteScalarAsync<byte[]?>("SELECT sentinel_value FROM tbl_node_identity LIMIT 1");
        if (sentinel == null) return null;
        var slots = await live.QueryAsync<(long SlotId, string Type, byte[] Wrapped, byte[] Iv, byte[]? Salt, long? Mem, long? It, long? Par)>(
            @"SELECT s.slot_id, s.slot_type, s.encrypted_master_dek, s.iv, s.salt, s.argon_memory, s.argon_iterations, s.argon_parallelism
              FROM tbl_key_slot s
              WHERE (s.slot_type = 'user' AND EXISTS (SELECT 1 FROM tbl_user u WHERE u.key_slot_id = s.slot_id AND u.is_active = 1 AND u.role = 'superadmin'))
                 OR s.slot_type = 'password'
              ORDER BY s.slot_type DESC, s.slot_id");
        foreach (var s in slots)
        {
            if (s.Salt == null || s.Mem == null || s.It == null || s.Par == null) continue;
            var kek = KeyDerivation.DeriveKek(password, s.Salt, (int)s.Mem, (int)s.It, (int)s.Par);
            try
            {
                byte[] dek;
                try { dek = MasterKeyManager.UnwrapMasterDek(s.Wrapped, s.Iv, kek); }
                catch (CryptographicException) { continue; }
                if (!MasterKeyManager.VerifySentinel(sentinel, dek)) { CryptographicOperations.ZeroMemory(dek); continue; }

                var retired = new List<byte[]>();
                foreach (var r in await live.QueryAsync<(string Name, byte[] W, byte[] Iv)>(
                             "SELECT key_name, wrapped_key, iv FROM tbl_node_data_key WHERE key_name LIKE 'retired-master-dek:%'"))
                    if (NodeDataKeyEnvelope.TryUnwrap(r.Name, r.W, r.Iv, dek) is { } old) retired.Add(old);
                return ((int)s.SlotId, new RekeyKeys(dek, retired, MasterKeyManager.GenerateMasterDek(), RandomNumberGenerator.GetBytes(32)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        return null;
    }
}

/// <summary>What a run collects for <see cref="RekeyReport"/> (R2's record, the one shape of rekey-report.json).</summary>
internal sealed class RekeyRunLog
{
    public bool PastLock { get; set; }
    public DateTime StartedAt { get; init; }
    public string? OldVault { get; set; }
    public RekeyPreflightReport? Preflight { get; set; }
    public List<RekeyStepResult> Steps { get; } = [];

    public RekeyReport ToReport(string result, string? error) => new(
        result, StartedAt, DateTime.UtcNow, Preflight, Steps,
        RevokedPeers: NotesOf("PeerRevoke", "revoked:"),
        ClearedSlots: NotesOf("KeyMaterial", "cleared-slot:"),
        ClearedAgents: NotesOf("KeyMaterial", "cleared-agent:"),
        OldVault: result == RekeyReport.Done ? OldVault : null,
        Error: error);

    /// <summary>PeerRevokeStep's <c>revoked:&lt;node id&gt; &lt;name&gt;</c>, KeyMaterialStep's
    /// <c>cleared-slot:&lt;slot&gt; &lt;user&gt;</c> and <c>cleared-agent:&lt;id&gt; &lt;name&gt;</c>.</summary>
    private List<string> NotesOf(string step, string prefix) =>
        Steps.Where(s => s.Name == step).SelectMany(s => s.Notes).Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..]).ToList();
}
