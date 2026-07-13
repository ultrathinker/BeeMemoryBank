using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

// UpdateService — manual update-choreography state machine. These tests prove the four
// definition-of-done scenarios directly against the service's public API:
//   (a) signature verification (accept validly-signed manifest, reject tampered),
//   (b) each safety gate individually blocks Apply and reports why,
//   (c) a wrong-SHA256 artifact is rejected before Apply,
//   (d) 3 failed health checks -> Failed, with the pre-update backup present & restorable.
//
// The collaborators DekRotationService / SnapshotRestoreService hold private state with no
// test-facing setter and a non-virtual GetProgress(), and HeavyOperationLock is process-
// internal, so gates 2/3/4 are exercised via the internal seam providers on UpdateService
// (each defaults to the real read — production behaviour is unchanged). Gate 1 (maintenance)
// is driven through the real MaintenanceModeService, which is public and directly drivable.
public class UpdateServiceTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bmb_updatetest_{Guid.NewGuid():N}");
    private ServiceProvider _services = null!;
    private UpdateService _svc = null!;
    private MaintenanceModeService _maintenance = null!;
    private SessionService _session = null!;
    private InMemoryArtifactSource _artifactSource = null!;

    private byte[] _releasePrivateKey = null!;
    private byte[] _releasePublicKey = null!;
    private byte[] _rotatedPrivateKey = null!;
    private byte[] _rotatedPublicKey = null!;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // AppVersion.Current is "1.0.1" (repo VERSION file). Pick versions strictly above/below.
    private const string NewerVersion = "2.0.0";
    private const string OlderVersion = "1.0.0";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);

        // Build a minimal real ServiceProvider so the heavy collaborators
        // (DekRotationService / SnapshotRestoreService / SnapshotService / SessionService)
        // are genuine, idle instances — matching how Program.cs wires them.
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddStorage(_tempDir);
        services.AddCore();
        services.AddHttpClient();
        services.AddSingleton(sp =>
            new SnapshotService(_tempDir, sp.GetRequiredService<DbConnectionFactory>()));
        _services = services.BuildServiceProvider();

        // Run migrations so <tempDir>/beememorybank.db is a valid SQLite database (needed for
        // the cold-backup path's PRAGMA wal_checkpoint + file copy in the Apply tests).
        await _services.GetRequiredService<MigrationRunner>().RunMigrationsAsync();

        _maintenance = _services.GetRequiredService<MaintenanceModeService>();
        _session = _services.GetRequiredService<SessionService>();

        var snapshotService = _services.GetRequiredService<SnapshotService>();
        var dekRotation = ActivatorUtilities.CreateInstance<DekRotationService>(_services, _tempDir);
        var snapshotRestore = ActivatorUtilities.CreateInstance<SnapshotRestoreService>(_services, _tempDir);

        // Two release keypairs: the primary signer + a rotation key (either verifies = valid).
        (_releasePublicKey, _releasePrivateKey) = Ed25519Signer.GenerateKeyPair();
        (_rotatedPublicKey, _rotatedPrivateKey) = Ed25519Signer.GenerateKeyPair();

        _artifactSource = new InMemoryArtifactSource(new Dictionary<string, byte[]>());

        _svc = new UpdateService(
            [_releasePublicKey, _rotatedPublicKey],
            snapshotService, _maintenance, dekRotation, snapshotRestore, _session,
            _tempDir,
            _services.GetRequiredService<ILogger<UpdateService>>(),
            _artifactSource,
            new AlwaysHealthyHealthCheck());
    }

    public Task DisposeAsync()
    {
        _services.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Builds a releases.json manifest + its detached base64 Ed25519 signature.</summary>
    private (string json, string signature) BuildSignedManifest(
        string version, string artifactName, string sha256, long size, byte[]? signingKey = null)
    {
        var manifest = new ReleasesManifest
        {
            SchemaVersion = 1,
            Channels = new ReleasesChannels
            {
                Stable = new ReleaseChannelInfo
                {
                    Version = version,
                    ProtocolVersion = 1,
                    Artifacts =
                    [
                        new ArtifactDescriptor { Name = artifactName, Sha256 = sha256, Size = size }
                    ]
                }
            }
        };
        var json = JsonSerializer.Serialize(manifest, JsonOpts);
        var sig = Convert.ToBase64String(
            Ed25519Signer.Sign(signingKey ?? _releasePrivateKey, Encoding.UTF8.GetBytes(json)));
        return (json, sig);
    }

    /// <summary>Drives the state machine Idle -> UpdateAvailable -> ReadyToApply.</summary>
    private async Task DriveToReadyToApply(byte[] artifactBytes)
    {
        _artifactSource.AddOrUpdate("package.bin", artifactBytes);
        var (json, sig) = BuildSignedManifest(NewerVersion, "package.bin", Sha256Hex(artifactBytes), artifactBytes.Length);
        (await _svc.CheckAsync(json, sig)).Should().BeTrue();
        await _svc.DownloadAsync(JsonSerializer.Deserialize<ReleasesManifest>(json, JsonOpts)!);
        _svc.GetProgress().CurrentStep.Should().Be(UpdateFlowStep.ReadyToApply);
    }

    /// <summary>Ensures gates 2/3/4 are clear for an Apply that only wants to exercise one gate.</summary>
    private void ClearSideGates()
    {
        _svc.DekRotationStepProvider = () => DekRotationFlowStep.Idle;
        _svc.RestoreStepProvider = () => RestoreFlowStep.Idle;
        _svc.HeavyOperationLockHeldProvider = () => false;
    }

    // ── (a) Signature verification ─────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_ValidSignature_NewerVersion_TransitionsToUpdateAvailable()
    {
        var (json, sig) = BuildSignedManifest(NewerVersion, "package.bin", "deadbeef", 1);

        var found = await _svc.CheckAsync(json, sig);

        found.Should().BeTrue();
        var p = _svc.GetProgress();
        p.CurrentStep.Should().Be(UpdateFlowStep.UpdateAvailable);
        p.AvailableVersion.Should().Be(NewerVersion);
    }

    [Fact]
    public async Task CheckAsync_TamperedSignature_IsRejectedAndFails()
    {
        // A correctly-shaped manifest, but signed by a random (untrusted) key.
        var (untrustedPub, untrustedPriv) = Ed25519Signer.GenerateKeyPair();
        var (json, sig) = BuildSignedManifest(NewerVersion, "package.bin", "deadbeef", 1, signingKey: untrustedPriv);

        var found = await _svc.CheckAsync(json, sig);

        found.Should().BeFalse();
        var p = _svc.GetProgress();
        p.CurrentStep.Should().Be(UpdateFlowStep.Failed);
        p.ErrorMessage.Should().Contain("signature");
    }

    [Fact]
    public async Task CheckAsync_TamperedManifestBytes_AreRejectedBySignature()
    {
        // Sign the legitimate manifest, then mutate a byte AFTER signing -> signature mismatch.
        var (json, sig) = BuildSignedManifest(NewerVersion, "package.bin", "deadbeef", 1);
        var tampered = json.Replace(NewerVersion, "9.9.9");

        var found = await _svc.CheckAsync(tampered, sig);

        found.Should().BeFalse();
        _svc.GetProgress().CurrentStep.Should().Be(UpdateFlowStep.Failed);
    }

    [Fact]
    public async Task CheckAsync_RotationKey_AlsoVerifies()
    {
        // Signed by the SECOND trusted key (rotation support) -> still valid.
        var (json, sig) = BuildSignedManifest(NewerVersion, "package.bin", "deadbeef", 1,
            signingKey: _rotatedPrivateKey);

        var found = await _svc.CheckAsync(json, sig);

        found.Should().BeTrue();
        _svc.GetProgress().CurrentStep.Should().Be(UpdateFlowStep.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_OlderVersion_ReturnsToIdle()
    {
        var (json, sig) = BuildSignedManifest(OlderVersion, "package.bin", "deadbeef", 1);

        var found = await _svc.CheckAsync(json, sig);

        found.Should().BeFalse();
        _svc.GetProgress().CurrentStep.Should().Be(UpdateFlowStep.Idle);
    }

    // ── (c) Wrong-SHA256 artifact is rejected before Apply ─────────────────────

    [Fact]
    public async Task DownloadAsync_WrongSha256_TransitionsToFailed_NotReadyToApply()
    {
        var realBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        // Declare the hash of a DIFFERENT byte sequence -> guaranteed mismatch with realBytes.
        var wrongHash = Sha256Hex(new byte[] { 99 });
        wrongHash.Should().NotBe(Sha256Hex(realBytes));
        _artifactSource.AddOrUpdate("package.bin", realBytes);

        var (json, sig) = BuildSignedManifest(NewerVersion, "package.bin", wrongHash, realBytes.Length);
        await _svc.CheckAsync(json, sig);

        await _svc.DownloadAsync(JsonSerializer.Deserialize<ReleasesManifest>(json, JsonOpts)!);

        var p = _svc.GetProgress();
        p.CurrentStep.Should().Be(UpdateFlowStep.Failed);
        p.ErrorMessage.Should().Contain("SHA-256 mismatch");
        // Never reached the apply-ready state.
        p.CurrentStep.Should().NotBe(UpdateFlowStep.ReadyToApply);
    }

    [Fact]
    public async Task DownloadAsync_CorrectSha256_TransitionsToReadyToApply()
    {
        var realBytes = new byte[] { 10, 20, 30, 40 };
        await DriveToReadyToApply(realBytes);

        _svc.GetProgress().CurrentStep.Should().Be(UpdateFlowStep.ReadyToApply);
    }

    // ── (b) Each safety gate individually blocks Apply ──────────────────────────

    [Fact]
    public async Task ApplyAsync_BlockedWhenMaintenanceActive()
    {
        await DriveToReadyToApply(new byte[] { 1, 2, 3 });
        ClearSideGates();
        _maintenance.Enter("scheduled maintenance");

        await _svc.ApplyAsync();

        var p = _svc.GetProgress();
        p.CurrentStep.Should().Be(UpdateFlowStep.Failed);
        p.ErrorMessage.Should().Contain("Apply blocked");
        p.BlockedGates.Should().NotBeNull()
            .And.Contain(g => g.StartsWith("MaintenanceModeActive"));
    }

    [Fact]
    public async Task ApplyAsync_BlockedWhenDekRotationInProgress()
    {
        await DriveToReadyToApply(new byte[] { 1, 2, 3 });
        ClearSideGates();
        _svc.DekRotationStepProvider = () => DekRotationFlowStep.Committing;

        await _svc.ApplyAsync();

        var p = _svc.GetProgress();
        p.CurrentStep.Should().Be(UpdateFlowStep.Failed);
        p.ErrorMessage.Should().Contain("Apply blocked");
        p.BlockedGates.Should().Contain(g => g.StartsWith("DekRotationInProgress(Committing)"));
    }

    [Fact]
    public async Task ApplyAsync_BlockedWhenSnapshotRestoreInProgress()
    {
        await DriveToReadyToApply(new byte[] { 1, 2, 3 });
        ClearSideGates();
        _svc.RestoreStepProvider = () => RestoreFlowStep.ApplyingSnapshot;

        await _svc.ApplyAsync();

        var p = _svc.GetProgress();
        p.CurrentStep.Should().Be(UpdateFlowStep.Failed);
        p.ErrorMessage.Should().Contain("Apply blocked");
        p.BlockedGates.Should().Contain(g => g.StartsWith("SnapshotRestoreInProgress(ApplyingSnapshot)"));
    }

    [Fact]
    public async Task ApplyAsync_BlockedWhenHeavyOperationLockHeld()
    {
        await DriveToReadyToApply(new byte[] { 1, 2, 3 });
        ClearSideGates();
        _svc.HeavyOperationLockHeldProvider = () => true;

        await _svc.ApplyAsync();

        var p = _svc.GetProgress();
        p.CurrentStep.Should().Be(UpdateFlowStep.Failed);
        p.ErrorMessage.Should().Contain("Apply blocked");
        p.BlockedGates.Should().Contain("HeavyOperationLockHeld");
    }

    // ── (d) 3 failed health checks -> Failed + pre-update backup present ───────

    [Fact]
    public async Task ApplyAsync_ThreeFailedHealthChecks_FailsAndLeavesBackupAndMarker()
    {
        await DriveToReadyToApply(new byte[] { 1, 2, 3 });
        ClearSideGates();
        // Session stays LOCKED -> Apply takes the cold-backup path (WAL checkpoint + file copy),
        // which is the simplest path to assert a real restorable backup on disk.
        _session.IsUnlocked.Should().BeFalse("fixture session is never unlocked");
        // Fail every health-check attempt (3 -> Failed per the superplan rule).
        _svc.SetHealthCheck(new FlakyHealthCheck(99));

        await _svc.ApplyAsync();

        var p = _svc.GetProgress();
        p.CurrentStep.Should().Be(UpdateFlowStep.Failed);
        p.ErrorMessage.Should().Contain("Health check failed 3 times");

        // Pre-update backup must be present and point at a real, restorable SQLite database.
        _svc.PreUpdateSnapshotPath.Should().NotBeNull();
        File.Exists(_svc.PreUpdateSnapshotPath!).Should().BeTrue("pre-update backup file should exist");

        await using var conn = new SqliteConnection($"Data Source={_svc.PreUpdateSnapshotPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        (await cmd.ExecuteScalarAsync()).Should().Be(1);

        // On failure the in-progress marker is intentionally left in place (a restart would
        // detect it). It is only removed on success.
        var marker = Path.Combine(_tempDir, "updates", "update.inprogress");
        File.Exists(marker).Should().BeTrue("update.inprogress marker must remain after a failed apply");
    }

    [Fact]
    public async Task ApplyAsync_HealthyCheck_CompletesAndRemovesMarker()
    {
        await DriveToReadyToApply(new byte[] { 1, 2, 3 });
        ClearSideGates();
        _svc.SetHealthCheck(new AlwaysHealthyHealthCheck());

        await _svc.ApplyAsync();

        var p = _svc.GetProgress();
        p.CurrentStep.Should().Be(UpdateFlowStep.Completed);
        p.ErrorMessage.Should().BeNull();

        // Success removes the marker.
        var marker = Path.Combine(_tempDir, "updates", "update.inprogress");
        File.Exists(marker).Should().BeFalse("update.inprogress marker must be removed on success");
    }

    [Fact]
    public async Task ApplyAsync_DangerousLegacyDbExists_BlocksApply()
    {
        // The guard checks the service's ACTIVE data path (_dataPath) for a Velopack
        // live-payload signature (an ancestor directory literally named "current" with a
        // sibling Update.exe) -- not AppContext.BaseDirectory, which for this test process
        // (and for the real Api process in the packaged layout, current\api\) is never the
        // dangerous path. So this test builds its own UpdateService pointed at a fake nested
        // path that reproduces that exact signature, reusing this class's already-built
        // collaborators (they are never touched -- the guard returns before any of them run).
        var fakeInstallRoot = Path.Combine(Path.GetTempPath(), $"bmb_veloguard_{Guid.NewGuid():N}");
        var fakeDataPath = Path.Combine(fakeInstallRoot, "current", "vaults", "default");
        Directory.CreateDirectory(fakeDataPath);
        File.WriteAllText(Path.Combine(fakeInstallRoot, "Update.exe"), "stub");

        try
        {
            var guardedSvc = new UpdateService(
                [_releasePublicKey, _rotatedPublicKey],
                _services.GetRequiredService<SnapshotService>(), _maintenance,
                ActivatorUtilities.CreateInstance<DekRotationService>(_services, fakeDataPath),
                ActivatorUtilities.CreateInstance<SnapshotRestoreService>(_services, fakeDataPath),
                _session,
                fakeDataPath,
                _services.GetRequiredService<ILogger<UpdateService>>(),
                _artifactSource,
                new AlwaysHealthyHealthCheck());

            var artifactBytes = new byte[] { 1, 2, 3 };
            _artifactSource.AddOrUpdate("guard-package.bin", artifactBytes);
            var (json, sig) = BuildSignedManifest(NewerVersion, "guard-package.bin", Sha256Hex(artifactBytes), artifactBytes.Length);
            (await guardedSvc.CheckAsync(json, sig)).Should().BeTrue();
            await guardedSvc.DownloadAsync(JsonSerializer.Deserialize<ReleasesManifest>(json, JsonOpts)!);
            guardedSvc.GetProgress().CurrentStep.Should().Be(UpdateFlowStep.ReadyToApply);
            guardedSvc.DekRotationStepProvider = () => DekRotationFlowStep.Idle;
            guardedSvc.RestoreStepProvider = () => RestoreFlowStep.Idle;
            guardedSvc.HeavyOperationLockHeldProvider = () => false;

            await guardedSvc.ApplyAsync();

            var p = guardedSvc.GetProgress();
            p.CurrentStep.Should().Be(UpdateFlowStep.Failed);
            p.ErrorMessage.Should().Contain("is inside a Velopack-managed 'current' folder");
        }
        finally
        {
            if (Directory.Exists(fakeInstallRoot))
            {
                try { Directory.Delete(fakeInstallRoot, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ApplyAsync_DataPathOutsideVelopackCurrentDir_DoesNotBlockOnGuard()
    {
        // _tempDir (this class's normal data path) has no "current" ancestor with a sibling
        // Update.exe, so the guard must not fire -- confirms the fix doesn't false-positive on
        // ordinary deployments (Docker/standalone/dev), only on the exact Velopack signature.
        await DriveToReadyToApply(new byte[] { 4, 5, 6 });
        ClearSideGates();

        await _svc.ApplyAsync();

        var p = _svc.GetProgress();
        p.ErrorMessage.Should().NotContain("is inside a Velopack-managed 'current' folder");
    }
}

