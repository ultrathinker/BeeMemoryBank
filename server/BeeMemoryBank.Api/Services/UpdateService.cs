using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Velopack;


namespace BeeMemoryBank.Api.Services;

/// <summary>
/// UpdateService — manual choreography state machine for the BeeMemoryBank update flow.
///
/// State machine:
///   Idle → Checking → UpdateAvailable(v) → Downloading(pct) → ReadyToApply → Applying → (Completed | Failed)
///
/// Key design decisions (per task brief):
///   - Manifest signature is verified against two hardcoded Ed25519 public keys (rotation support).
///   - Artifact download is abstracted behind IUpdateArtifactSource (no real HTTP in this task).
///   - Post-apply health check is abstracted behind IUpdateHealthCheck (no real process restart).
///   - Safety gates (maintenance, DEK rotation, restore, heavy-op lock) are all checked before Apply.
///   - Pre-update snapshot uses SnapshotService.CreateAsync (same pattern as DekRotationService).
///   - Cold backup (WAL checkpoint + file copy) used when session is locked.
///   - update.inprogress marker written before Apply, removed on success.
///   - All public state is volatile / Interlocked — safe for polling from GET /node/update/status.
/// </summary>
public sealed class UpdateService
{
    // ── Hardcoded release public keys (two for rotation support) ─────────────
    // In a real deployment these would be the actual release-signing key(s).
    // Both are placeholder zero keys here — tests generate ephemeral keys and
    // inject them via the testable constructor. The real provisioning comes
    // in a later task. Format: raw 32-byte Ed25519 public key, base64-encoded.
    //
    // IMPORTANT: "either key verifies = valid" — this allows key rotation
    // without a flag day (ship v2 key in old software, decommission v1 later).
    private static readonly byte[][] DefaultReleasePublicKeys =
    [
        // Key slot 0 — placeholder; replace with real public key at provisioning time.
        new byte[32],
        // Key slot 1 — placeholder; replace with second real public key at provisioning time.
        new byte[32]
    ];

    private readonly byte[][] _releasePublicKeys;
    private readonly SnapshotService _snapshotService;
    private readonly MaintenanceModeService _maintenance;
    private readonly DekRotationService _dekRotation;
    private readonly RestoreInitiatorService _snapshotRestore;
    private readonly SessionService _sessionService;
    private readonly string _dataPath;
    private readonly ILogger<UpdateService> _logger;

    // A sealed node's pre-update copy is taken under the gate's hold and with the relay's writes paused (docs/design/step-b-spec.md 8.5).
    // Both are null on a node that cannot be sealed (a plain node, a test without them): then the locked path is the plain one.
    private readonly IVaultGate? _gate;
    private readonly ISealedRelayLifecycle? _relayLifecycle;

    // Pluggable collaborators — defaults are set at construction; tests override.
    private IUpdateArtifactSource _artifactSource;
    private IUpdateHealthCheck _healthCheck;

    // ── Gate-check seams (public; defaults read the real collaborators) ────────
    // DekRotationService / RestoreInitiatorService hold private state with no
    // test-facing setter and a non-virtual GetProgress(), and HeavyOperationLock is
    // process-internal — so gates 2/3/4 cannot be driven through the real services
    // from a test. These providers default to the real reads (production behaviour is
    // byte-for-byte unchanged) and are overridden per-gate in UpdateServiceTests to
    // simulate a busy collaborator. Gate 1 (maintenance) is read straight off the real
    // MaintenanceModeService — it is public and directly drivable, so no seam is needed.
    //
    // These are exposed publicly (rather than internal+InternalsVisibleTo) to keep the
    // test setup self-contained within the files this task is permitted to touch. A
    // future UI / Velopack-integration task does not need to set these.
    public Func<DekRotationFlowStep> DekRotationStepProvider = () => DekRotationFlowStep.Idle;
    public Func<RestoreFlowStep> RestoreStepProvider = () => RestoreFlowStep.Idle;
    public Func<bool> HeavyOperationLockHeldProvider = () => false;

    // Seam to allow mocking the actual process-killing Velopack call in integration tests.
    public Action<UpdateManager, VelopackAsset> ApplyUpdatesAndRestartAction { get; set; } =
        (mgr, asset) => mgr.ApplyUpdatesAndRestart(asset);

    private readonly string _applyBehavior;

    // ── State ─────────────────────────────────────────────────────────────────
    private volatile UpdateFlowStep _step = UpdateFlowStep.Idle;
    private volatile int _pct;
    private volatile string? _availableVersion;
    private volatile string? _statusMessage;
    private volatile string? _errorMessage;
    private volatile string[]? _blockedGates;

    // Kept for Apply's pre-snapshot reference
    private volatile string? _preUpdateSnapshotPath; // path to the backup/snapshot on disk

    // Single-flight: only one update operation at a time
    private readonly SemaphoreSlim _executeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Primary constructor (used in production DI).</summary>
    public UpdateService(
        SnapshotService snapshotService,
        MaintenanceModeService maintenance,
        DekRotationService dekRotation,
        RestoreInitiatorService snapshotRestore,
        SessionService sessionService,
        string dataPath,
        ILogger<UpdateService> logger,
        string applyBehavior = "simulate",
        IVaultGate? gate = null,
        ISealedRelayLifecycle? relayLifecycle = null)
        : this(DefaultReleasePublicKeys, snapshotService, maintenance, dekRotation,
               snapshotRestore, sessionService, dataPath, logger,
               new InMemoryArtifactSource(new Dictionary<string, byte[]>()),
               new AlwaysHealthyHealthCheck(),
               applyBehavior, gate, relayLifecycle)
    {
    }

    /// <summary>
    /// Testable constructor — callers supply ephemeral keys and stub collaborators.
    /// </summary>
    public UpdateService(
        byte[][] releasePublicKeys,
        SnapshotService snapshotService,
        MaintenanceModeService maintenance,
        DekRotationService dekRotation,
        RestoreInitiatorService snapshotRestore,
        SessionService sessionService,
        string dataPath,
        ILogger<UpdateService> logger,
        IUpdateArtifactSource artifactSource,
        IUpdateHealthCheck healthCheck,
        string applyBehavior = "simulate",
        IVaultGate? gate = null,
        ISealedRelayLifecycle? relayLifecycle = null)
    {
        _gate = gate;
        _relayLifecycle = relayLifecycle;
        _releasePublicKeys = releasePublicKeys;
        _snapshotService = snapshotService;
        _maintenance = maintenance;
        _dekRotation = dekRotation;
        _snapshotRestore = snapshotRestore;
        _sessionService = sessionService;
        _dataPath = dataPath;
        _logger = logger;
        _artifactSource = artifactSource;
        _healthCheck = healthCheck;
        _applyBehavior = applyBehavior;

        // Wire the gate-check seams to the real collaborators (production behaviour).
        // Tests override individual providers to simulate a busy gate.
        DekRotationStepProvider = () => _dekRotation.GetProgress().CurrentStep;
        RestoreStepProvider = () => _snapshotRestore.GetProgress().CurrentStep;
        HeavyOperationLockHeldProvider = () =>
        {
            // Check-then-release: we only want to know if the lock is free, not hold it.
            bool free = HeavyOperationLock.Instance.Wait(0);
            if (free)
                HeavyOperationLock.Instance.Release();
            return !free;
        };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Current state machine snapshot — safe to poll from any thread.
    /// This is the primary API surface for an eventual UI or Velopack-integration task.
    /// </summary>
    public UpdateProgressResponse GetProgress() => new(
        _step,
        _availableVersion,
        _pct,
        _statusMessage,
        _errorMessage,
        _blockedGates
    );

    /// <summary>
    /// Replace the artifact source at runtime (for tests that want to swap it after construction).
    /// </summary>
    public void SetArtifactSource(IUpdateArtifactSource source) => _artifactSource = source;

    /// <summary>
    /// Replace the health check at runtime (for tests).
    /// </summary>
    public void SetHealthCheck(IUpdateHealthCheck check) => _healthCheck = check;

    /// <summary>
    /// Step 1 — Check: verify the manifest signature and compare versions.
    /// Transitions: Idle → Checking → (UpdateAvailable | Idle back if already up-to-date).
    /// </summary>
    /// <param name="manifestJson">Raw UTF-8 JSON of the releases.json manifest.</param>
    /// <param name="manifestSignatureBase64">
    ///   Detached base64 Ed25519 signature over the raw manifest bytes.
    ///   Signed with one of the two hardcoded release keys.
    /// </param>
    /// <returns>True if a newer version was found and state became UpdateAvailable.</returns>
    public async Task<bool> CheckAsync(string manifestJson, string manifestSignatureBase64,
        CancellationToken ct = default)
    {
        if (!await _executeLock.WaitAsync(TimeSpan.Zero, ct))
            throw new InvalidOperationException("Another update operation is already in progress.");

        try
        {
            SetState(UpdateFlowStep.Checking, 5, "Verifying manifest signature…");

            var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
            var signature = Convert.FromBase64String(manifestSignatureBase64);

            // Verify against either key (rotation support: either = valid)
            var sigValid = _releasePublicKeys.Any(pk => Ed25519Signer.Verify(pk, manifestBytes, signature));
            if (!sigValid)
            {
                SetFailed("Manifest signature verification failed — not signed by any trusted release key.");
                return false;
            }

            ReleasesManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ReleasesManifest>(manifestJson, JsonOpts)
                    ?? throw new InvalidOperationException("Manifest deserialized to null.");
            }
            catch (JsonException ex)
            {
                SetFailed($"Manifest JSON is malformed: {ex.Message}");
                return false;
            }

            var stableVersion = manifest.Channels.Stable.Version;
            var runningVersion = AppVersion.Current;

            _logger.LogInformation("UpdateService.Check: stable={Stable} running={Running}",
                stableVersion, runningVersion);

            if (!IsNewer(stableVersion, runningVersion))
            {
                // Already up-to-date — return to Idle
                SetState(UpdateFlowStep.Idle, 0, $"Already on latest ({runningVersion}).");
                return false;
            }

            _availableVersion = stableVersion;
            SetState(UpdateFlowStep.UpdateAvailable, 10,
                $"Update available: {stableVersion} (current: {runningVersion})");
            return true;
        }
        catch (Exception ex) when (ex is not (InvalidOperationException or OperationCanceledException))
        {
            SetFailed($"Check failed: {ex.Message}");
            throw;
        }
        finally
        {
            _executeLock.Release();
        }
    }

    /// <summary>
    /// Step 2 — Download and verify the first artifact declared in the manifest.
    /// The artifact's SHA-256 is checked against the manifest's declared hash.
    /// Transitions: UpdateAvailable → Downloading(pct) → ReadyToApply (or Failed on hash mismatch).
    /// </summary>
    public async Task DownloadAsync(ReleasesManifest manifest, CancellationToken ct = default)
    {
        if (!await _executeLock.WaitAsync(TimeSpan.Zero, ct))
            throw new InvalidOperationException("Another update operation is already in progress.");

        try
        {
            if (_step != UpdateFlowStep.UpdateAvailable)
                throw new InvalidOperationException($"Cannot download from state {_step}; must be UpdateAvailable.");

            var artifacts = manifest.Channels.Stable.Artifacts;
            if (artifacts.Count == 0)
                throw new InvalidOperationException("Manifest has no artifacts to download.");

            // Download the first artifact (typically the platform package).
            var descriptor = artifacts[0];
            SetState(UpdateFlowStep.Downloading, 20,
                $"Downloading artifact '{descriptor.Name}'…");

            byte[] bytes;
            try
            {
                bytes = await _artifactSource.GetArtifactBytesAsync(descriptor, ct);
            }
            catch (Exception ex)
            {
                SetFailed($"Artifact download failed: {ex.Message}");
                return;
            }

            SetState(UpdateFlowStep.Downloading, 70, "Verifying artifact SHA-256…");

            // SHA-256 verification — mismatch → Failed (do NOT proceed to Apply)
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var expectedHash = descriptor.Sha256.ToLowerInvariant();
            if (actualHash != expectedHash)
            {
                SetFailed($"Artifact SHA-256 mismatch for '{descriptor.Name}': " +
                          $"expected {expectedHash}, got {actualHash}. Refusing to apply.");
                return;
            }

            _logger.LogInformation("UpdateService: artifact '{Name}' verified OK ({Bytes} bytes)",
                descriptor.Name, bytes.Length);

            SetState(UpdateFlowStep.ReadyToApply, 100,
                $"Artifact '{descriptor.Name}' downloaded and verified. Ready to apply.");
        }
        finally
        {
            _executeLock.Release();
        }
    }

    /// <summary>
    /// Steps 3–5 — Gate check, pre-update snapshot/backup, simulated Apply, health check.
    /// Transitions: ReadyToApply → Applying → (Completed | Failed).
    ///
    /// Gates checked (all must pass):
    ///   1. MaintenanceModeService.IsInMaintenance == false
    ///   2. DekRotationService.GetProgress().CurrentStep == Idle
    ///   3. RestoreInitiatorService.GetProgress().CurrentStep == Idle
    ///   4. HeavyOperationLock is currently acquirable (check-then-release)
    ///
    /// Pre-update backup strategy:
    ///   - Session unlocked: call SnapshotService.CreateAsync (full signed snapshot).
    ///   - Session locked, plain node: WAL checkpoint + raw file copy to &lt;data&gt;/updates/pre-update-&lt;version&gt;/.
    ///   - Session locked, sealed node: the quiesced set of docs/design/step-b-spec.md 8.5 (see <see cref="TakePreUpdateBackupAsync"/>).
    ///
    /// Apply is simulated (no real binary swap in this task).
    /// Health check: up to 3 failures → Failed; pass → Completed.
    /// </summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        if (!await _executeLock.WaitAsync(TimeSpan.Zero, ct))
            throw new InvalidOperationException("Another update operation is already in progress.");

        try
        {
            if (_step != UpdateFlowStep.ReadyToApply)
                throw new InvalidOperationException($"Cannot apply from state {_step}; must be ReadyToApply.");

            // ── Pre-apply guard check ─────────────────────────────────────────
            // Checks the ACTUAL active data directory (_dataPath), not AppContext.BaseDirectory:
            // this service runs inside the Api process, whose own BaseDirectory is
            // <install>\current\api\ in the packaged layout — never the dangerous
            // <install>\current\data\ / <install>\current\bmbd\data\ paths a prior regression
            // could resurrect. Walking up from _dataPath for a directory literally named
            // "current" with a sibling Update.exe is deployment-topology-agnostic: it fires
            // for any process (Desktop/bmbd/Api) whose active data lives inside a Velopack
            // live-payload folder that gets wiped/replaced on every apply, and never fires for
            // Docker/standalone deployments that have no such folder.
            if (BmbPaths.IsInsideVelopackCurrentDir(_dataPath))
            {
                SetFailed($"Apply blocked: active data directory '{_dataPath}' is inside a Velopack-managed 'current' folder that gets wiped/replaced on apply. Update cannot be applied to avoid data loss.");
                return;
            }

            // ── Gate checks ───────────────────────────────────────────────────
            SetState(UpdateFlowStep.Applying, 5, "Checking safety gates…");

            var blocked = CheckGates();
            if (blocked.Count > 0)
            {
                _blockedGates = blocked.ToArray();
                SetFailed($"Apply blocked by: {string.Join(", ", blocked)}");
                return;
            }

            // ── Pre-update snapshot / backup ──────────────────────────────────
            SetState(UpdateFlowStep.Applying, 15, "Creating pre-update backup…");

            var version = _availableVersion ?? "unknown";
            string? backupPath = null;
            try
            {
                backupPath = await TakePreUpdateBackupAsync(version, ct);
                _preUpdateSnapshotPath = backupPath;
            }
            catch (Exception ex)
            {
                SetFailed($"Pre-update backup failed: {ex.Message}");
                return;
            }

            // ── Write update.inprogress marker ────────────────────────────────
            var updatesDir = Path.Combine(_dataPath, "updates");
            Directory.CreateDirectory(updatesDir);
            var markerPath = Path.Combine(updatesDir, "update.inprogress");
            await File.WriteAllTextAsync(markerPath, version, ct);
            _logger.LogInformation("UpdateService: wrote inprogress marker at {Path}", markerPath);

            try
            {
                if (_applyBehavior == "real")
                {
                    if (_artifactSource is VelopackArtifactSource veloSource)
                    {
                        var mgr = veloSource.UpdateManager;
                        var asset = veloSource.UpdateAsset;
                        if (mgr != null && asset != null)
                        {
                            SetState(UpdateFlowStep.Applying, 50, "Applying update via Velopack…");
                            ApplyUpdatesAndRestartAction(mgr, asset);
                        }
                        else
                        {
                            throw new InvalidOperationException("Velopack UpdateManager or UpdateAsset is not initialized. Was Download completed?");
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("Cannot perform real Apply when artifact source is not Velopack.");
                    }
                }
                else
                {
                    // ── Simulated Apply ───────────────────────────────────────────
                    SetState(UpdateFlowStep.Applying, 50, "Applying update (simulated)…");
                    // NOTE: Real binary-swap and process-tree restart are explicitly out of scope
                    // for this task. In the later Velopack-integration task this is where
                    // ApplyUpdatesAndRestart() would be called. We simulate a brief delay.
                    await Task.Delay(50, ct);
                }

                // ── Health checks (up to 3 failures) ─────────────────────────
                SetState(UpdateFlowStep.Applying, 70, "Running post-apply health checks…");
                const int maxFailures = 3;
                int failures = 0;
                bool healthy = false;

                for (int attempt = 1; attempt <= maxFailures; attempt++)
                {
                    try
                    {
                        healthy = await _healthCheck.CheckAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "UpdateService: health check attempt {N} threw", attempt);
                        healthy = false;
                    }

                    if (healthy)
                    {
                        _logger.LogInformation("UpdateService: health check passed on attempt {N}", attempt);
                        break;
                    }

                    failures++;
                    _logger.LogWarning("UpdateService: health check failed (attempt {N}/{Max})", attempt, maxFailures);

                    if (failures >= maxFailures)
                    {
                        // 3 failures → Failed; record that rollback would restore from backupPath.
                        // Actual rollback mechanics (restoring from snapshot) are a later task.
                        SetFailed(
                            $"Health check failed {maxFailures} times after applying update {version}. " +
                            $"Node did NOT proceed. Pre-update backup is at: {backupPath ?? "(none)"}. " +
                            $"Use the existing snapshot restore path to roll back if needed.");
                        return;
                    }

                    await Task.Delay(200, ct);
                }

                // Success path
                File.Delete(markerPath);
                _logger.LogInformation("UpdateService: update {Version} applied successfully.", version);
                SetState(UpdateFlowStep.Completed, 100, $"Update {version} applied successfully.");
            }
            catch (OperationCanceledException)
            {
                // Leave marker in place — restart will detect it
                throw;
            }
            catch (Exception ex)
            {
                SetFailed($"Apply failed: {ex.Message}");
            }
        }
        finally
        {
            _executeLock.Release();
        }
    }

    /// <summary>
    /// Reset state machine to Idle. Useful for tests and after a Failed state.
    /// </summary>
    public async Task ResetAsync()
    {
        if (!await _executeLock.WaitAsync(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("Could not acquire lock for reset.");
        try
        {
            _step = UpdateFlowStep.Idle;
            _pct = 0;
            _availableVersion = null;
            _statusMessage = null;
            _errorMessage = null;
            _blockedGates = null;
            _preUpdateSnapshotPath = null;
        }
        finally
        {
            _executeLock.Release();
        }
    }

    /// <summary>
    /// The path where the pre-update backup/snapshot was placed (null until Apply runs).
    /// Exposed for test assertions (DoD: confirm backup is present and points at real snapshot).
    /// </summary>
    public string? PreUpdateSnapshotPath => _preUpdateSnapshotPath;

    // ── Private helpers ───────────────────────────────────────────────────────

    private void SetState(UpdateFlowStep step, int pct, string? msg)
    {
        _step = step;
        _pct = pct;
        _statusMessage = msg;
        _errorMessage = null;
        _blockedGates = null;
    }

    private void SetFailed(string error)
    {
        _step = UpdateFlowStep.Failed;
        _statusMessage = null;
        _errorMessage = error;
        _logger.LogError("UpdateService → Failed: {Error}", error);
    }

    /// <summary>
    /// Checks all four safety gates.
    /// Returns a list of gate names that are NOT satisfied (empty = all clear).
    /// </summary>
    private List<string> CheckGates()
    {
        var blocked = new List<string>();

        // Gate 1: Maintenance mode must be inactive (read from the real service — it is
        // public and directly drivable from tests, so no seam is needed here).
        if (_maintenance.IsInMaintenance)
            blocked.Add($"MaintenanceModeActive({_maintenance.Reason})");

        // Gate 2: DEK rotation must be idle (via seam; defaults to the real service).
        var dekStep = DekRotationStepProvider();
        if (dekStep != DekRotationFlowStep.Idle)
            blocked.Add($"DekRotationInProgress({dekStep})");

        // Gate 3: Snapshot restore must be idle (via seam; defaults to the real service).
        var restoreStep = RestoreStepProvider();
        if (restoreStep != RestoreFlowStep.Idle)
            blocked.Add($"SnapshotRestoreInProgress({restoreStep})");

        // Gate 4: HeavyOperationLock must be acquirable (via seam; defaults to the real lock).
        if (HeavyOperationLockHeldProvider())
            blocked.Add("HeavyOperationLockHeld");

        return blocked;
    }

    /// <summary>
    /// Takes the pre-update backup and returns the path to the result.
    /// Unlocked (plain or sealed): a snapshot (<see cref="SnapshotService.CreateAsync"/>). Locked, plain node: WAL checkpoint + file copy.
    /// Locked, sealed node: the QUIESCED SET (docs/design/step-b-spec.md 8.5, 8.7.1) - the sealed database, the chat database and the boot
    /// store copied as files while nothing can write any of the three: the gate is held closed (<see cref="IVaultGate.HoldClosedAsync"/>),
    /// the relay's writes are paused (<see cref="ISealedRelayLifecycle.PauseWritesAsync"/>), the boot store is checkpointed, and every copy
    /// is checked against its source by length and SHA-256 (<c>backup_inconsistent</c> on any difference). A node unlocked meanwhile gets
    /// a snapshot instead. <c>server-auto-unlock.*</c> and <c>os-auto-unlock.dat</c> are never copied: a backup must not carry the key that
    /// opens it.
    /// </summary>
    internal async Task<string> TakePreUpdateBackupAsync(string version, CancellationToken ct)
    {
        if (_sessionService.IsUnlocked)
        {
            // Unlocked path: use SnapshotService (same pattern as DekRotationService)
            return await TakeSnapshotBackupAsync();
        }
        if (_gate is { IsSealed: true })
        {
            return await TakeSealedColdBackupAsync(version, ct);
        }
        else
        {
            // Locked path: WAL checkpoint + raw file copy
            var backupDir = Path.Combine(_dataPath, "updates", $"pre-update-{SanitizeVersion(version)}");
            Directory.CreateDirectory(backupDir);

            var dbPath = Path.Combine(_dataPath, "beememorybank.db");
            if (File.Exists(dbPath))
            {
                // WAL checkpoint to truncate WAL before copy
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                cmd.ExecuteNonQuery();

                var destPath = Path.Combine(backupDir, "beememorybank.db");
                File.Copy(dbPath, destPath, overwrite: true);
                _logger.LogInformation("UpdateService: cold backup to {Path}", destPath);
                return destPath;
            }
            else
            {
                _logger.LogWarning("UpdateService: DB file not found at {Path}; skipping cold backup.", dbPath);
                return backupDir;
            }
        }
    }

    private async Task<string> TakeSnapshotBackupAsync()
    {
        var snap = await _snapshotService.CreateAsync(filterSecrets: false, sign: false);
        _logger.LogInformation("UpdateService: pre-update snapshot created: {FileName}", snap.FileName);
        return Path.Combine(_snapshotService.SnapshotsDir, snap.FileName);
    }

    // ── The quiesced set of a sealed node ─────────────────────────────────────

    /// <summary>The files of a sealed node's quiesced set, in copy order (spec 8.5). The two sidecars of a database belong to the set only when they exist and are not empty.</summary>
    private static readonly string[] QuiescedSetFiles =
    [
        "beememorybank.db", "beememorybank.db-wal", "beememorybank.db-shm",
        "chat.db", "chat.db-wal", "chat.db-shm",
        "beememorybank.boot.db", "beememorybank.boot.db-wal",
    ];

    /// <summary>Test seam: every SQL statement the cold backup of a sealed node runs (the boot store's checkpoint; there is no VACUUM INTO).</summary>
    internal Action<string>? ColdBackupSqlRecorder { get; set; }

    /// <summary>Test seam: called with the backup folder after the files are copied and before they are verified, while the hold and the pause are still in force.</summary>
    internal Func<string, Task>? AfterColdBackupCopy { get; set; }

    private async Task<string> TakeSealedColdBackupAsync(string version, CancellationToken ct)
    {
        IAsyncDisposable hold;
        try
        {
            hold = await _gate!.HoldClosedAsync("pre-update", ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Not Closed any more: the node was unlocked in the meantime, so the snapshot is the backup (spec 8.5 step 1).
            _logger.LogInformation("UpdateService: the vault is no longer closed ({Reason}); taking a snapshot instead of a file copy.", ex.GetType().Name);
            return await TakeSnapshotBackupAsync();
        }

        await using (hold.ConfigureAwait(false))
        {
            if (_relayLifecycle is null)
                throw new InvalidOperationException("The relay of this sealed node cannot be paused, so a consistent file copy cannot be taken.");
            await using var pause = (await _relayLifecycle.PauseWritesAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
            return await CopyQuiescedSetAsync(version, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> CopyQuiescedSetAsync(string version, CancellationToken ct)
    {
        var backupDir = Path.Combine(_dataPath, "updates", $"pre-update-{SanitizeVersion(version)}");
        Directory.CreateDirectory(backupDir);
        var main = Path.Combine(_dataPath, "beememorybank.db");
        var boot = Path.Combine(_dataPath, "beememorybank.boot.db");
        if (!File.Exists(main) || !File.Exists(boot))
            throw new InvalidOperationException("The sealed database or its boot store is missing: nothing consistent to copy.");

        // The boot store needs no key: a plain checkpoint makes its main file complete. The sealed files were checkpointed at the clean
        // close (spec 5.4); a non-empty -wal or -shm beside them means that close was not clean, and then it belongs to the set.
        const string checkpoint = "PRAGMA wal_checkpoint(TRUNCATE)";
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = boot, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = checkpoint;
            ColdBackupSqlRecorder?.Invoke(checkpoint);
            cmd.ExecuteNonQuery();
        }

        var copied = new List<string>();
        try
        {
            foreach (var name in QuiescedSetFiles)
            {
                ct.ThrowIfCancellationRequested();
                var source = Path.Combine(_dataPath, name);
                var target = Path.Combine(backupDir, name);
                var isMain = name is "beememorybank.db" or "chat.db" or "beememorybank.boot.db";
                var present = File.Exists(source) && (isMain || new FileInfo(source).Length > 0);
                // A copy of an earlier attempt of the same version must not leave a sidecar that this set does not have.
                if (!present) { if (File.Exists(target)) File.Delete(target); continue; }
                File.Copy(source, target, overwrite: true);
                copied.Add(name);
            }

            if (AfterColdBackupCopy is { } hook) await hook(backupDir).ConfigureAwait(false);

            foreach (var name in copied)
            {
                ct.ThrowIfCancellationRequested();
                // Re-read both sides: the source may have changed while it was held (which cannot happen), the copy may be short.
                var source = Path.Combine(_dataPath, name);
                var target = Path.Combine(backupDir, name);
                if (!File.Exists(source) || new FileInfo(source).Length != new FileInfo(target).Length
                    || !await Sha256Matches(source, target, ct).ConfigureAwait(false))
                    throw new BackupInconsistentException($"{name} differs from its copy");
            }
        }
        catch
        {
            // No half set is left behind: the backup either holds every file of the set or none.
            foreach (var name in copied)
            {
                try { File.Delete(Path.Combine(backupDir, name)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            try { if (!Directory.EnumerateFileSystemEntries(backupDir).Any()) Directory.Delete(backupDir); } catch (IOException) { }
            throw;
        }

        _logger.LogInformation("UpdateService: quiesced set of the sealed node copied to {Path} ({Count} files)", backupDir, copied.Count);
        return backupDir;
    }

    private static async Task<bool> Sha256Matches(string a, string b, CancellationToken ct)
    {
        var first = await HashFileAsync(a, ct).ConfigureAwait(false);
        var second = await HashFileAsync(b, ct).ConfigureAwait(false);
        return first.AsSpan().SequenceEqual(second);
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>A copy of the quiesced set differs from its source (<c>backup_inconsistent</c>); the update does not go on.</summary>
    internal sealed class BackupInconsistentException(string detail) : IOException("backup_inconsistent: " + detail);

    /// <summary>
    /// Returns true if <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.
    /// Falls back to string comparison if semver parsing fails (graceful degradation).
    /// </summary>
    private static bool IsNewer(string candidate, string current)
    {
        if (Version.TryParse(candidate, out var cv) && Version.TryParse(current, out var rv))
            return cv > rv;
        // Fallback: not equal and candidate is not empty
        return !string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(candidate);
    }

    private static string SanitizeVersion(string v) =>
        string.Join("", v.Where(c => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-'));
}
