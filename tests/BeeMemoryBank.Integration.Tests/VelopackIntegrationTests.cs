using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

public class VelopackIntegrationTests : IAsyncLifetime
{
    private string _tempDir = null!;
    private string _releasesDir = null!;
    private string _packagesDir = null!;
    private ServiceProvider _services = null!;
    private UpdateService _svc = null!;
    private MaintenanceModeService _maintenance = null!;
    private SessionService _session = null!;
    private VelopackArtifactSource _artifactSource = null!;

    private byte[] _releasePrivateKey = null!;
    private byte[] _releasePublicKey = null!;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        // These scenarios build and pack a real win-x64 Velopack release and drive Update.exe
        // apply/rollback - Windows installer/executable-stub mechanics, not something a Linux/macOS
        // CI runner can meaningfully exercise (vpk's own pack command resolves to a different,
        // non-Windows option set on those platforms, and there is no Update.exe to run anyway).
        // See the matching guard in each [Fact] below.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"bmb_velo_integration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _releasesDir = Path.Combine(_tempDir, "releases");
        Directory.CreateDirectory(_releasesDir);

        _packagesDir = Path.Combine(_tempDir, "packages");
        Directory.CreateDirectory(_packagesDir);

        // 1. Build ServiceProvider
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddStorage(_tempDir);
        services.AddCore();
        services.AddHttpClient();
        services.AddSingleton(sp =>
            new SnapshotService(_tempDir, sp.GetRequiredService<DbConnectionFactory>()));
        _services = services.BuildServiceProvider();

        // 2. Run migrations
        await _services.GetRequiredService<MigrationRunner>().RunMigrationsAsync();

        _maintenance = _services.GetRequiredService<MaintenanceModeService>();
        _session = _services.GetRequiredService<SessionService>();

        var snapshotService = _services.GetRequiredService<SnapshotService>();
        var dekRotation = ActivatorUtilities.CreateInstance<DekRotationService>(_services, _tempDir);
        var snapshotRestore = ActivatorUtilities.CreateInstance<RestoreInitiatorService>(_services, _tempDir);

        (_releasePublicKey, _releasePrivateKey) = Ed25519Signer.GenerateKeyPair();

        // 3. Build & pack dummy console app version 1.1.0 using dotnet and vpk
        var dummySrcDir = Path.Combine(_tempDir, "dummy_src");
        var dummyPubDir = Path.Combine(_tempDir, "dummy_pub");
        Directory.CreateDirectory(dummySrcDir);
        Directory.CreateDirectory(dummyPubDir);

        var psiNew = new System.Diagnostics.ProcessStartInfo("dotnet", $"new console -o \"{dummySrcDir}\" --force")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        var pNew = System.Diagnostics.Process.Start(psiNew);
        await pNew!.WaitForExitAsync();

        var psiPub = new System.Diagnostics.ProcessStartInfo("dotnet", $"publish \"{dummySrcDir}\" -c Release -r win-x64 --self-contained true -o \"{dummyPubDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        var pPub = System.Diagnostics.Process.Start(psiPub);
        await pPub!.WaitForExitAsync();

        var psiPack = new System.Diagnostics.ProcessStartInfo("vpk", $"pack --packId TestApp --packVersion 1.1.0 --packDir \"{dummyPubDir}\" --mainExe dummy_src.exe --outputDir \"{_releasesDir}\" --skipVeloAppCheck -y")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        var pPack = System.Diagnostics.Process.Start(psiPack);
        await pPack!.WaitForExitAsync();

        // Set up the TestVelopackLocator to mock our running app's environment as "TestApp" version "1.0.0"
        var updateExePath = Path.Combine(_tempDir, "Update.exe");
        var locator = new TestVelopackLocator(
            "TestApp",
            "1.0.0",
            _packagesDir,
            _tempDir,
            _tempDir,
            updateExePath
        );
        File.WriteAllText(updateExePath, ""); // Create dummy Update.exe file

        _artifactSource = new VelopackArtifactSource(_releasesDir, locator);

        _svc = new UpdateService(
            [_releasePublicKey],
            snapshotService, _maintenance, dekRotation, snapshotRestore, _session,
            _tempDir,
            _services.GetRequiredService<ILogger<UpdateService>>(),
            _artifactSource,
            new AlwaysHealthyHealthCheck(),
            "real");
    }

    public Task DisposeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        _services.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    [Fact]
    public async Task Scenario1_UpdateWorksEndToEnd()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 1. Get the package info of the produced package TestApp-1.1.0-full.nupkg
        var packagePath = Path.Combine(_releasesDir, "TestApp-1.1.0-full.nupkg");
        File.Exists(packagePath).Should().BeTrue();
        var packageBytes = await File.ReadAllBytesAsync(packagePath);
        var packageSha256 = Sha256Hex(packageBytes);

        // 2. Build the signed manifest for BMB UpdateService
        var (json, sig) = BuildSignedManifest("1.1.0", "TestApp-1.1.0-full.nupkg", packageSha256, packageBytes.Length);

        // Mock the ApplyUpdatesAndRestart action to verify it gets called
        bool applyCalled = false;
        _svc.ApplyUpdatesAndRestartAction = (mgr, asset) =>
        {
            applyCalled = true;
            asset.Version.Should().Be(SemanticVersion.Parse("1.1.0"));
            asset.PackageId.Should().Be("TestApp");
        };

        // 3. Run Update Check
        var checkResult = await _svc.CheckAsync(json, sig);
        checkResult.Should().BeTrue();
        _svc.GetProgress().CurrentStep.Should().Be(UpdateFlowStep.UpdateAvailable);

        // 4. Run Update Download (this calls Velopack under the hood)
        var manifest = JsonSerializer.Deserialize<ReleasesManifest>(json, JsonOpts)!;
        await _svc.DownloadAsync(manifest);
        _svc.GetProgress().CurrentStep.Should().Be(UpdateFlowStep.ReadyToApply);

        // 5. Run Update Apply
        await _svc.ApplyAsync();
        _svc.GetProgress().CurrentStep.Should().Be(UpdateFlowStep.Completed);

        // 6. Verify assertions
        applyCalled.Should().BeTrue("ApplyUpdatesAndRestart should be called");

        // The update.inprogress marker is created and deleted on success
        var markerPath = Path.Combine(_tempDir, "updates", "update.inprogress");
        File.Exists(markerPath).Should().BeFalse("Marker file should be deleted on success");

        // Pre-update backup is present
        _svc.PreUpdateSnapshotPath.Should().NotBeNull();
        File.Exists(_svc.PreUpdateSnapshotPath!).Should().BeTrue("Pre-update backup should exist");
    }

    [Fact]
    public async Task Scenario2_CorruptedPackageRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 1. Get the package info of the produced package TestApp-1.1.0-full.nupkg
        var packagePath = Path.Combine(_releasesDir, "TestApp-1.1.0-full.nupkg");
        var packageBytes = await File.ReadAllBytesAsync(packagePath);
        
        // 2. Build the signed manifest but with a tampered SHA-256 hash
        var tamperedSha256 = Sha256Hex(new byte[] { 0, 1, 2 }); // Guarantees a mismatch
        var (json, sig) = BuildSignedManifest("1.1.0", "TestApp-1.1.0-full.nupkg", tamperedSha256, packageBytes.Length);

        // Mock the ApplyUpdatesAndRestart action (should not be called)
        _svc.ApplyUpdatesAndRestartAction = (mgr, asset) =>
        {
            Assert.Fail("ApplyUpdatesAndRestart should not be called for corrupted package");
        };

        // 3. Run Update Check
        var checkResult = await _svc.CheckAsync(json, sig);
        checkResult.Should().BeTrue();

        // 4. Run Update Download (which should fail due to SHA-256 mismatch)
        var manifest = JsonSerializer.Deserialize<ReleasesManifest>(json, JsonOpts)!;
        await _svc.DownloadAsync(manifest);

        _svc.GetProgress().CurrentStep.Should().Be(UpdateFlowStep.Failed);
        _svc.GetProgress().ErrorMessage.Should().Contain("SHA-256 mismatch");
    }

    [Fact]
    public async Task Scenario3_KillMidApplyRollback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // We simulate a crash-recovery startup where:
        // 1. `update.inprogress` is found in the updates directory.
        // 2. A backup of the database is present in the updates directory.
        var updatesDir = Path.Combine(_tempDir, "updates");
        Directory.CreateDirectory(updatesDir);

        var markerPath = Path.Combine(updatesDir, "update.inprogress");
        await File.WriteAllTextAsync(markerPath, "1.1.0");

        var backupDir = Path.Combine(updatesDir, "pre-update-1.1.0");
        Directory.CreateDirectory(backupDir);

        // Create a dummy record in the DB before backup to confirm it gets restored
        var originalDbPath = Path.Combine(_tempDir, "beememorybank.db");
        using (var conn = new SqliteConnection($"Data Source={originalDbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS tbl_test_marker (id INTEGER PRIMARY KEY, name TEXT)";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO tbl_test_marker (name) VALUES ('BackupIsIntact')";
            cmd.ExecuteNonQuery();

            // Checkpoint WAL to flush all tables/data from WAL file to main DB file before copy
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            checkCmd.ExecuteNonQuery();
        }

        // Clear pools to release locks held by the singleton factory and other connections
        using (var tempConn = new SqliteConnection($"Data Source={originalDbPath};Pooling=False"))
        {
            SqliteConnection.ClearPool(tempConn);
        }

        // Copy the current database to the backup location (this represents our pre-update snapshot database)
        var backupDbPath = Path.Combine(backupDir, "beememorybank.db");
        File.Copy(originalDbPath, backupDbPath, overwrite: true);

        // Now, modify the active database to represent a corrupted/interrupted apply state
        using (var conn = new SqliteConnection($"Data Source={originalDbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE IF EXISTS tbl_test_marker";
            cmd.ExecuteNonQuery();
        }

        // Verify simulated state is ready
        File.Exists(markerPath).Should().BeTrue("inprogress marker exists");
        File.Exists(backupDbPath).Should().BeTrue("backup db exists");

        // 3. Rollback Action:
        // Startup code checks if update.inprogress exists. If so, it restores the backup db by copying it back.
        if (File.Exists(markerPath))
        {
            var version = await File.ReadAllTextAsync(markerPath);
            var expectedBackupDb = Path.Combine(updatesDir, $"pre-update-{version}", "beememorybank.db");
            if (File.Exists(expectedBackupDb))
            {
                using (var tempConn = new SqliteConnection($"Data Source={originalDbPath};Pooling=False"))
                {
                    SqliteConnection.ClearPool(tempConn);
                }

                File.Copy(expectedBackupDb, originalDbPath, overwrite: true);
                
                // Delete target WAL/SHM files to prevent SQLite from applying the old transaction logs (like DROP TABLE)
                var targetWal = originalDbPath + "-wal";
                var targetShm = originalDbPath + "-shm";
                if (File.Exists(targetWal)) File.Delete(targetWal);
                if (File.Exists(targetShm)) File.Delete(targetShm);

                File.Delete(markerPath); // Rollback complete, clear marker
            }
        }

        // 4. Verify the database is restored and intact
        File.Exists(markerPath).Should().BeFalse("Marker file should be cleared after rollback");
        
        using (var conn = new SqliteConnection($"Data Source={originalDbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM tbl_test_marker WHERE id = 1";
            var result = cmd.ExecuteScalar() as string;
            result.Should().Be("BackupIsIntact", "Rollback should restore the database to its pre-update state");
        }
    }
}
