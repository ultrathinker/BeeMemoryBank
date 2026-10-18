using System.Security.Cryptography;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>A test seam: the migration calls it with the name of each step it has finished; a hook that throws is a process that died there.</summary>
public delegate void SealStepHook(string step);

/// <summary>
/// "Encrypt this node" (docs/design/step-b-spec.md 10): the migration of a plain full node to a sealed one, started by the owner or, for a node that
/// is being set up, by the setup itself. Preflight with named blockers, page-encrypted copies of the vault and chat
/// databases, a boot store, a verification of everything before the journal says <c>verified</c>, the swap and the wipe of the replaced plain files
/// through the resolver's forward path, and the gate's switch to Sealed. No copy of the plain database is kept: there is no way back from the node.
/// A node that fails before the swap is unchanged; a process that dies at any step starts either plain with its old
/// files or sealed with the new ones (<see cref="SealSwapResolver"/>), and no plain copy survives the next start.
/// </summary>
public sealed class SealMigrationService
{
    public const string AuditAction = "vault_sealed";

    private readonly string _dataPath;
    private readonly VaultGate _gate;
    private readonly SessionService _session;
    private readonly IDatabaseKeyService _keyService;
    private readonly BootStore _boot;
    private readonly IUnlockMaterialMirror _mirror;
    private readonly ISealMigrationHost _host;
    private readonly Func<IReadOnlyList<SealedFile>> _files;
    private readonly MaintenanceModeService _maintenance;
    private readonly SyncScheduler? _scheduler;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger? _logger;
    private volatile bool _swapUnfinished;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly object _progressLock = new();
    private SealProgress _progress = new(SealProgress.Idle, 0, "idle");
    private CancellationTokenSource? _cancel;

    /// <summary>Test seam, called after each step (see <see cref="SealStepHook"/>).</summary>
    public SealStepHook? StepHook { get; set; }

    /// <summary>Test seam of the resolver's forward path (the swap and the wipe), step by step.</summary>
    public Action<string>? SwapStepHook { get; set; }

    /// <summary>Test seam: replaces the wipe of the replaced plain files (a failing wipe).</summary>
    public Func<string, bool>? WipeOverride { get; set; }

    /// <summary>The pause before the second try of a wipe that failed, doubled before the third (a handle that is still closing, a scanner, goes away).</summary>
    public TimeSpan WipeRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    private const int WipeTries = 4;

    public SealMigrationService(string dataPath, VaultGate gate, SessionService session, IDatabaseKeyService keyService, BootStore boot,
        IUnlockMaterialMirror mirror, ISealMigrationHost host, Func<IReadOnlyList<SealedFile>> files, MaintenanceModeService maintenance,
        SyncScheduler? scheduler, IServiceScopeFactory scopes, ILogger<SealMigrationService>? logger = null)
    {
        _dataPath = dataPath;
        _gate = gate;
        _session = session;
        _keyService = keyService;
        _boot = boot;
        _mirror = mirror;
        _host = host;
        _files = files;
        _maintenance = maintenance;
        _scheduler = scheduler;
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>What the swap of this process's migration did (its pending wipes and the hard links it unlinked); null before one ran.</summary>
    public SealSwapResolution? LastSwap { get; private set; }

    /// <summary>True from the start of a run to its end (the progress poll is still answered in the moment the gate is closed between the swap and the open).</summary>
    public bool IsRunning => _running.CurrentCount == 0;

    /// <summary>What the page polls (spec 10.9); null when no migration has run in this process.</summary>
    public SealProgress? Progress
    {
        get { lock (_progressLock) return _progress.Phase == SealProgress.Idle ? null : _progress; }
    }

    private string VaultPath => Path.Combine(_dataPath, VaultStoreFiles.Database);
    private string ChatPath => Path.Combine(_dataPath, VaultStoreFiles.Chat);
    private string BootPath => Path.Combine(_dataPath, VaultStoreFiles.Boot);
    private string Target(string path) => path + VaultStoreFiles.SealNewSuffix;

    // ---- 10.2 preflight ---------------------------------------------------------------------------------------------------------------

    public Task<SealPreflightResult> PreflightAsync(SealOptions options, CancellationToken ct = default) => PreflightCoreAsync(options, ignoreRunning: false, ct);

    private async Task<SealPreflightResult> PreflightCoreAsync(SealOptions options, bool ignoreRunning, CancellationToken ct)
    {
        var blockers = new List<SealBlocker>();
        var warnings = new List<SealWarning>();
        IReadOnlyList<PlainCopyFinding> copies = [];
        void Block(string code, string message) => blockers.Add(new SealBlocker(code, message));

        // Said before anything else, whatever else is in the way: there is no copy to go back to (BMB-306).
        if (!_gate.IsSealed) warnings.Add(new SealWarning(SealWarnings.NoWayBack, SealWarnings.NoWayBackMessage));

        if (_gate.IsSealed) Block(SealBlockers.AlreadySealed, "This node's database is already encrypted.");
        if (!ignoreRunning && _running.CurrentCount == 0) Block(SealBlockers.MigrationRunning, "The encryption of this node is already running.");
        if (!_session.IsUnlocked) Block(SealBlockers.VaultLocked, "Unlock this node first: the master key is needed to encrypt it.");
        if (!options.CallerIsSuperadmin) Block(SealBlockers.NotSuperadmin, "Only an administrator can encrypt this node.");
        if (_dataPath.StartsWith(@"\\", StringComparison.Ordinal) || _dataPath.StartsWith("//", StringComparison.Ordinal))
            Block(SealBlockers.UncPath, "The data folder is on a network share. An encrypted database needs a folder on a local disk.");

        long databaseBytes = 0;
        if (_gate.IsSealed || !_session.IsUnlocked) return new SealPreflightResult(blockers, warnings, copies, 0);

        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        databaseBytes = SizeWithWal(VaultPath);

        if (sp.GetService<ISyncProtocolPolicy>() is { SealsMetadata: false })
            Block(SealBlockers.NotActivated, "Activate the new sync format first (Admin > Metadata encryption): an encrypted node can only relay changes that are already sealed.");
        if (sp.GetService<IHeldEventStatus>() is { } held && await held.AnyHeldAsync(ct))
            Block(SealBlockers.EventsHeld, "Some received changes are still waiting for their keys. Let them finish, then try again.");
        if (await RotationPendingAsync(sp, ct) is { } rotation)
            Block(SealBlockers.RotationPending, "A key rotation is not finished (" + rotation + "). Let it finish, then try again.");
        if (_maintenance.IsInMaintenance || await RestoreRunningAsync(sp, ct))
            Block(SealBlockers.RestoreRunning, "A restore or another maintenance task is running. Try again when it has finished.");
        if (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync() is { Ed25519PrivateKeyV: 0 })
            Block(SealBlockers.IdentityV0, "This node's identity key is still stored in the old format. Lock and unlock the node once, then try again.");

        var integrity = await IntegrityAsync(ct);
        if (integrity is not null) Block(SealBlockers.Integrity, integrity);

        var needed = (long)(3.5 * (SizeWithWal(VaultPath) + SizeWithWal(ChatPath)));
        if ((_host.FreeBytes(_dataPath) ?? FreeBytes()) is { } free && free < needed)
            Block(SealBlockers.FreeSpace, $"Not enough free space on this disk: encrypting needs about {needed / (1024 * 1024)} MB.");

        var engine = EngineProblem();
        if (engine is not null) Block(SealBlockers.Engine, engine);

        copies = PlainCopyScan.Run(_dataPath, PlainScanMode.Preflight);
        if (copies.Count > 0)
            Block(SealBlockers.PlainCopies, "These files are unencrypted copies of your vault: "
                + string.Join(", ", copies.Take(5).Select(c => $"{c.RelativePath} ({c.Kind})")) + (copies.Count > 5 ? $" and {copies.Count - 5} more" : "")
                + ". A stolen disk would open them.");

        blockers.AddRange(await _host.HostBlockersAsync(options, ct));

        warnings.AddRange(await _host.HostWarningsAsync(ct));
        return new SealPreflightResult(blockers, warnings, copies, databaseBytes);
    }

    /// <summary>What is unfinished about a key rotation, in words for the person who must wait for it; null when nothing is.</summary>
    private static async Task<string?> RotationPendingAsync(IServiceProvider sp, CancellationToken ct)
    {
        var states = sp.GetRequiredService<IDekRotationStateRepository>();
        if ((await states.GetByStateAsync(DekRotationState.Proposed)).Count > 0) return "one is proposed and waits to be accepted";
        if ((await states.GetByStateAsync(DekRotationState.Committing)).Count > 0) return "one is being committed";
        var factory = sp.GetRequiredService<IDbConnectionFactory>();
        using var connection = factory.CreateConnection();
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key = 'mek_publish_pending'") > 0
            ? "the metadata key it made is not published yet"
            : null;
    }

    private static async Task<bool> RestoreRunningAsync(IServiceProvider sp, CancellationToken ct)
    {
        var states = sp.GetRequiredService<IRestoreEventStateRepository>();
        foreach (var state in new[] { RestoreEventState.Pending, RestoreEventState.Downloading, RestoreEventState.Applying })
            if ((await states.GetByStateAsync(state)).Count > 0) return true;
        return false;
    }

    private async Task<string?> IntegrityAsync(CancellationToken ct)
    {
        foreach (var file in _files())
        {
            if (!File.Exists(file.FilePath) || new FileInfo(file.FilePath).Length == 0) continue;
            using var connection = file.Open();
            var rows = (await connection.QueryAsync<string>("PRAGMA integrity_check")).ToList();
            if (rows.Count != 1 || rows[0] != "ok")
                return $"The {file.Purpose} database failed its integrity check; repair or restore it before encrypting.";
        }
        return null;
    }

    private string? EngineProblem()
    {
        try
        {
            using var probe = new SqliteConnection("Data Source=:memory:");
            probe.Open();
            if (probe.ExecuteScalar<string?>("SELECT sqlite3mc_version()") is not { Length: > 0 })
                return "The database engine of this program cannot encrypt databases.";
            var path = Path.Combine(_dataPath, "tmp", "seal-probe-" + Guid.NewGuid().ToString("N") + ".db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                using (var c = SealedSqlite.OpenKeyed(path, key))
                {
                    c.Execute("CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1)");
                }
                using (var c = SealedSqlite.OpenKeyed(path, key, readOnly: true))
                    if (c.ExecuteScalar<long>("SELECT x FROM t") != 1) return "The database engine of this program cannot encrypt databases.";
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                SecureFileWipe.TryWipe(path);
            }
            return null;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            return "The database engine of this program cannot encrypt databases.";
        }
    }

    private long? FreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_dataPath))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static long SizeWithWal(string path)
    {
        long size = 0;
        foreach (var f in new[] { path, path + "-wal" })
            if (File.Exists(f)) size += new FileInfo(f).Length;
        return size;
    }

    // ---- 10.4 the run -----------------------------------------------------------------------------------------------------------------------

    /// <summary>Re-authenticates, runs the preflight and starts the migration in the background. The refusal is a <see cref="SealMigrationException"/>.</summary>
    public async Task StartAsync(string masterPassword, SealOptions options, CancellationToken ct = default)
    {
        if (!await _session.VerifyMasterPasswordAsync(masterPassword))
            throw new UnauthorizedAccessException("The master password is not correct.");
        var preflight = await PreflightAsync(options, ct);
        if (!preflight.CanStart)
            throw new SealMigrationException(preflight.Blockers[0].Code, preflight.Blockers[0].Message);
        _ = Task.Run(() => RunAsync(options, CancellationToken.None));
    }

    /// <summary>The whole migration, awaited. Never throws for a failure: the result is in <see cref="Progress"/>.</summary>
    public async Task<SealProgress> RunAsync(SealOptions options, CancellationToken ct = default)
    {
        if (!await _running.WaitAsync(0, ct))
            return Set(new SealProgress(SealProgress.Failed, 0, "start", SealBlockers.MigrationRunning, "The encryption of this node is already running."));
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cancel.Token;
        var swapped = false;
        List<string> targets = [];
        IDisposable? writeGate = null;
        IDisposable? syncPause = null;
        var quiesce = new List<IDisposable>();   // the connection barrier of the live files, held from the copy to the swap
        var maintenanceEntered = false;
        var gateSealed = false;
        try
        {
            Set(new SealProgress(SealProgress.Running, 2, "preflight"));
            var preflight = await PreflightCoreAsync(options, ignoreRunning: true, token);
            if (!preflight.CanStart)
                throw new SealMigrationException(preflight.Blockers[0].Code, preflight.Blockers[0].Message);
            Step("preflight");

            // 1. Maintenance mode, the sync loop off, no event write in flight, the databases folded into their main files.
            _maintenance.Enter("Encrypting the database...");
            maintenanceEntered = true;
            if (_scheduler is not null) syncPause = await _scheduler.PauseAsync(token);
            writeGate = await EventWriteGate.Instance.QuiesceAsync(token);
            await _host.PrepareVaultAsync(token);   // a vault older than the sentinel gets its sentinel now, so the checkpoint folds it into the main file
            Set(new SealProgress(SealProgress.Running, 8, "checkpoint"));
            await CheckpointLiveAsync(token);
            Step("checkpoint");

            // 2. The journal: from here a start finds this migration and undoes or finishes it.
            var files = new List<string> { VaultStoreFiles.Database };
            if (File.Exists(ChatPath)) files.Add(VaultStoreFiles.Chat);
            SealJournal.Write(_dataPath, SealJournal.New(SealJournal.Building, files, string.Empty));
            Step("journal-building");

            // From here to the swap nothing may write to the live files: a connection that wants one waits at its factory, and the ones still open are
            // waited for. The copy (step 5) and its comparison with the original (step 7) are then of one and the same state, whatever background work
            // (the baseline producer, the embedding queue) was about to write; a node that cannot be drained in time stays as it was.
            foreach (var file in _files())
            {
                quiesce.Add(file.Connections.BeginQuiesce());
                try { await file.Connections.WaitDrainedAsync(VaultGate.DefaultDrainTimeout, token); }
                catch (TimeoutException)
                {
                    throw new SealMigrationException(SealFailures.Io, "The database is still in use by other work of this node. Try again in a minute; nothing has been changed.");
                }
            }

            // 4. The database key and the page keys.
            var dbk = _keyService.Create();
            var vaultKey = _keyService.DerivePageKey(dbk, DbPurposes.Vault);
            var chatKey = _keyService.DerivePageKey(dbk, DbPurposes.Chat);
            var candidateDeks = _session.GetCandidateDeks();
            try
            {
                Step("keys");
                token.ThrowIfCancellationRequested();

                // 5. Page-encrypted copies of the live databases. The source is the plain file itself: nothing new is written in clear.
                targets = [Target(VaultPath)];
                Set(new SealProgress(SealProgress.Running, 25, "encrypt-vault"));
                SealTo(VaultPath, Target(VaultPath), vaultKey);
                Step("vacuum-vault");
                if (files.Contains(VaultStoreFiles.Chat))
                {
                    targets.Add(Target(ChatPath));
                    Set(new SealProgress(SealProgress.Running, 40, "encrypt-chat"));
                    SealTo(ChatPath, Target(ChatPath), chatKey);
                    Step("vacuum-chat");
                }
                token.ThrowIfCancellationRequested();

                // 6. The boot store for the targets: doors, the database key wrapped under every master key the session holds, the identity.
                Set(new SealProgress(SealProgress.Running, 55, "boot-store"));
                targets.Add(Target(BootPath));
                var keys = new InMemoryVaultKeySource(vaultKey, chatKey);
                using (var targetVault = new DbConnectionFactory(Target(VaultPath), keys))
                    await _mirror.WriteNewBootStoreAsync(Target(BootPath), targetVault, dbk, 1, candidateDeks, token);
                SqliteConnection.ClearAllPools();
                Step("boot-target");

                // 7. Verification (10.5): everything the targets hold is compared with the live files before the journal says so.
                Set(new SealProgress(SealProgress.Running, 65, "verify"));
                await SealVerifier.VerifyAsync(new SealVerifyInput(VaultPath, Target(VaultPath), files.Contains(VaultStoreFiles.Chat) ? ChatPath : null,
                    files.Contains(VaultStoreFiles.Chat) ? Target(ChatPath) : null, Target(BootPath), dbk, 1, candidateDeks), token);
                Step("verified-check");
            }
            catch (Exception ex) when (ex is not SealKill)
            {
                WipeTargets(targets);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dbk);
                CryptographicOperations.ZeroMemory(vaultKey);
                CryptographicOperations.ZeroMemory(chatKey);
                foreach (var candidate in candidateDeks) CryptographicOperations.ZeroMemory(candidate);
            }

            // 8. The targets are flushed to disk; the journal says "verified". From here the migration is not cancellable.
            token.ThrowIfCancellationRequested();
            foreach (var t in targets) Flush(t);
            var verified = SealJournal.New(SealJournal.Verified, files, string.Empty);
            SealJournal.Write(_dataPath, verified);
            Step("journal-verified");

            // 9. No connection left; the swap and the wipe of the replaced plain files, by the resolver's forward path.
            Set(new SealProgress(SealProgress.Running, 80, "swap"));
            _swapUnfinished = false;
            try
            {
                SqliteConnection.ClearAllPools();
                SealSwapResolution swap;
                try
                {
                    swap = SealSwapResolver.GoForward(_dataPath, verified, step => { SwapStepHook?.Invoke(step); StepHook?.Invoke(step); }, WipeOverride);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogError("The swap of the encrypted files failed ({Type}): {Message}", ex.GetType().Name, ex.Message);
                    _swapUnfinished = true;
                    throw new SealMigrationException(SealFailures.Io,
                        "The swap was interrupted. Restart the node: the start finishes it, and no file is lost (" + ex.GetType().Name + ").");
                }
                swapped = true;
                LastSwap = swap;
                if (swap.Action == SealResolveAction.Refused)
                    throw new SealMigrationException(SealFailures.Io, swap.Refusal?.Message ?? "The swap was refused.");
                Step("swapped");

                // 10. The gate guards a sealed node now (closed: nothing has opened it yet).
                writeGate.Dispose();
                writeGate = null;
                _gate.SwitchToSealed();
                gateSealed = true;
            }
            finally
            {
                foreach (var q in quiesce) q.Dispose();
            }

            // 11. No plain file is left, and the final scan finds nothing, before anything opens (review round 1, H1). A run that cannot prove it ends
            // here: the node is sealed and locked, the journal keeps the retry, and no unlock opens it while a replaced plain file is readable.
            Set(new SealProgress(SealProgress.Running, 92, "scan"));
            if (await FindPlainRemainsAsync() is { } remains)
            {
                _session.Lock();   // no key of a node that may not open stays in memory
                _logger?.LogError("The encrypted node stays locked: {Code}, {Names}.", remains.Code, string.Join(", ", remains.Names));
                return Set(new SealProgress(SealProgress.Failed, 0, LockedStep, remains.Code, remains.Message, PlainCopiesFound: remains.Names));
            }

            // 12. The session's master key opens the new database (4.4 steps 3-8, no door), then the node serves again.
            var dek = _session.GetMasterDek();
            try
            {
                var open = await _gate.OpenAsync(dek, CancellationToken.None);
                if (!open.Opened)
                    throw new SealMigrationException(SealFailures.Io, "The encrypted database could not be opened after the swap (" + open.Refusal + "). Restart the node and unlock it.");
            }
            finally { CryptographicOperations.ZeroMemory(dek); }
            await _gate.CompleteOpenAsync(CancellationToken.None);
            Step("opened");

            // 13. The audit entry (no names) and the end.
            await AuditAsync(token);
            Step("audit");
            return Set(new SealProgress(SealProgress.Done, 100, "done", PlainCopiesFound: []));
        }
        catch (OperationCanceledException)
        {
            Cleanup(targets, swapped);
            return Set(new SealProgress(SealProgress.Failed, 0, "cancelled", SealFailures.Cancelled, "The encryption was cancelled; the node is unchanged."));
        }
        catch (SealMigrationException ex)
        {
            Cleanup(targets, swapped);
            _logger?.LogError("Encrypting the node failed ({Code}): {Message}", ex.Code, ex.Message);
            return Set(new SealProgress(SealProgress.Failed, 0, "failed", ex.Code, ex.Message));
        }
        catch (Exception ex) when (ex is not SealKill)
        {
            Cleanup(targets, swapped);
            _logger?.LogError(ex, "Encrypting the node failed ({Type}).", ex.GetType().Name);
            return Set(new SealProgress(SealProgress.Failed, 0, "failed", SealFailures.Io, "The encryption failed (" + ex.GetType().Name + "); the node is unchanged unless a restart is asked for."));
        }
        finally
        {
            foreach (var q in quiesce) q.Dispose();
            writeGate?.Dispose();
            syncPause?.Dispose();
            if (maintenanceEntered && !(swapped && !gateSealed)) _maintenance.Exit();   // a swap that did not finish keeps the node out of service until the restart
            _cancel?.Dispose();
            _cancel = null;
            _running.Release();
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>Asks a running migration to stop. Only possible before the journal says <c>verified</c>.</summary>
    public void Cancel() => _cancel?.Cancel();

    /// <summary>The <see cref="SealProgress.Step"/> of a run that ended after the swap with the node sealed and locked.</summary>
    public const string LockedStep = "locked";

    /// <summary>
    /// True when the last run stopped inside the swap: the journal says so, and the next start finishes it (or undoes it) from what is on disk. A caller must
    /// not reset or delete anything then; the node is neither the plain one it was nor an encrypted one yet.
    /// </summary>
    public bool SwapUnfinished => _swapUnfinished;

    private sealed record PlainRemains(string Code, string Message, IReadOnlyList<string> Names);

    /// <summary>
    /// Tries once more to erase the replaced plain files of a swap and returns what is still readable on the disk. The gate calls it before every
    /// open of a node that has a leftover (<see cref="VaultGate.PlainLeftovers"/>), so a file that was locked at the end of a run goes with the next
    /// unlock; nothing is asked of the owner.
    /// </summary>
    public IReadOnlyList<string> ClearLeftovers()
    {
        if (SealSwapResolver.Leftovers(_dataPath).Count == 0) return [];
        RetryWipe();
        return SealSwapResolver.Leftovers(_dataPath);
    }

    private void RetryWipe()
    {
        try
        {
            var retry = SealSwapResolver.RetryWipe(_dataPath, wipe: WipeOverride);
            if (retry.LinkedPlainNames.Count > 0 && LastSwap is { } swap)
                LastSwap = swap with { LinkedPlain = [.. swap.LinkedPlainNames, .. retry.LinkedPlainNames] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Another try at erasing the replaced unencrypted files failed.");   // what is left is judged by the caller
        }
    }

    /// <summary>
    /// The end of the swap, before anything opens (review round 1, H1): the replaced plain files are wiped (a few tries: what held a file lets go), then
    /// the content scan of the data folder, the replaced files included, must find nothing. A finding of a kind the erase may take is erased first;
    /// what is still there is named. Null when no readable plain copy of the vault is on the disk.
    /// </summary>
    private async Task<PlainRemains?> FindPlainRemainsAsync()
    {
        var left = SealSwapResolver.Leftovers(_dataPath);
        for (var attempt = 1; left.Count > 0 && attempt < WipeTries; attempt++)
        {
            await Task.Delay(WipeRetryDelay * attempt).ConfigureAwait(false);
            RetryWipe();
            left = SealSwapResolver.Leftovers(_dataPath);
        }
        if (left.Count > 0)
            return new PlainRemains(SealFailures.Wipe,
                "The database is encrypted, but the unencrypted file it replaced could not be erased yet (" + string.Join(", ", left) + "). "
                + "The node stays locked. It tries again at the next unlock and at the next start; nothing needs to be deleted by hand.", left);

        var findings = PlainCopyScan.Run(_dataPath, PlainScanMode.Sealed);
        if (findings.Count > 0 && findings.Where(f => PlainCopyKinds.CanBeErased(f.Kind)).Select(f => f.RelativePath).ToList() is { Count: > 0 } erasable)
            findings = PlainCopyEraser.Erase(_dataPath, PlainScanMode.Sealed, erasable, WipeOverride).Remaining;
        Step("final-scan");
        if (findings.Count == 0) return null;

        var names = findings.Select(f => f.RelativePath).ToList();
        return new PlainRemains(SealFailures.PlainCopies,
            "The database is encrypted, but a copy of it is still readable on the disk (" + string.Join(", ", findings.Take(5).Select(f => $"{f.RelativePath}, {f.Kind}"))
            + (findings.Count > 5 ? $" and {findings.Count - 5} more" : "") + "). The node stays locked until the copy has been dealt with.", names);
    }

    // ---- pieces --------------------------------------------------------------------------------------------------------------------------

    private async Task CheckpointLiveAsync(CancellationToken ct)
    {
        foreach (var file in _files())
        {
            if (!File.Exists(file.FilePath)) continue;
            var quiesce = file.Connections.BeginQuiesce();
            try
            {
                await file.Connections.WaitDrainedAsync(VaultGate.DefaultDrainTimeout, ct);
                using var c = file.Open();
                c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            }
            finally { quiesce.Dispose(); }
        }
    }

    /// <summary>Spec 10.4 step 5: <c>VACUUM INTO</c> a copy under the page key, from a plain connection of the live file.</summary>
    private static void SealTo(string source, string target, byte[] pageKey)
    {
        SecureFileWipe.TryWipe(target);   // a target of an earlier attempt
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        connection.Execute("PRAGMA busy_timeout = 30000");
        connection.Execute("VACUUM INTO " + SqlCipherScheme.VacuumIntoTarget(target, pageKey));
    }

    private static void Flush(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>A failure before the swap: the encrypted targets and the journal go, the node is exactly what it was.</summary>
    private void Cleanup(IEnumerable<string> targets, bool swapped)
    {
        if (swapped) return;   // a swap under way is the resolver's, at the next start
        WipeTargets(targets);
        var (state, journal) = SealJournal.Read(_dataPath);
        if (state != SealJournalState.Missing && (journal is null || journal.Phase is SealJournal.Building or SealJournal.Verified))
            SealJournal.Delete(_dataPath);
    }

    /// <summary>The encrypted targets and any sidecar of theirs go; they hold a key-less image of the vault, so a wipe is cheap insurance.</summary>
    private static void WipeTargets(IEnumerable<string> targets)
    {
        foreach (var t in targets)
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                SecureFileWipe.TryWipe(t + suffix);
    }

    private async Task AuditAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IAuditLogRepository>().LogAsync("node", "-", AuditAction, "web", "The database of this node was encrypted.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "The audit entry of the encryption could not be written.");
        }
    }

    private SealProgress Set(SealProgress progress)
    {
        lock (_progressLock) _progress = progress;
        return progress;
    }

    private void Step(string name)
    {
        _logger?.LogInformation("Encrypting the node: {Step} done.", name);
        StepHook?.Invoke(name);
    }
}

/// <summary>A process killed by a test hook: never caught by the migration's own cleanup, so the disk is left as a dead process leaves it.</summary>
public sealed class SealKill(string step) : Exception("killed after " + step);
