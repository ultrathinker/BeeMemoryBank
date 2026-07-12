using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using BeeMemoryBank.Storage.Sqlite;
using Xunit;
using FluentAssertions;

namespace BeeMemoryBank.Integration.Tests;

public class LegacyMigrationTests
{
    public class TestLegacyCandidate
    {
        public string Path { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string NodeId { get; set; } = "";
        public bool IsValid { get; set; }
    }

    private static TestLegacyCandidate? ValidatePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return null;
        try
        {
            var resolvedPath = Environment.ExpandEnvironmentVariables(rawPath.Trim());
            if (!Directory.Exists(resolvedPath)) return null;

            var dbPath = Path.Combine(resolvedPath, "beememorybank.db");
            if (!File.Exists(dbPath)) return null;

            TestLegacyCandidate? result = null;
            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();

                using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='tbl_node_identity';";
                var tableExists = checkCmd.ExecuteScalar() as string;
                if (tableExists == "tbl_node_identity")
                {
                    using var queryCmd = conn.CreateCommand();
                    queryCmd.CommandText = "SELECT node_id, display_name FROM tbl_node_identity LIMIT 1;";
                    using var reader = queryCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        var nodeId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        var displayName = reader.IsDBNull(1) ? "" : reader.GetString(1);

                        result = new TestLegacyCandidate
                        {
                            Path = resolvedPath,
                            DisplayName = displayName,
                            NodeId = nodeId,
                            IsValid = true
                        };
                    }
                }
                SqliteConnection.ClearPool(conn);
            }
            return result;
        }
        catch
        {
        }
        return null;
    }

    private static async Task CopyLegacyDataAsync(string sourcePath, string destPath)
    {
        var resolvedSourcePath = Environment.ExpandEnvironmentVariables(sourcePath.Trim());
        var resolvedDestPath = Environment.ExpandEnvironmentVariables(destPath.Trim());

        string srcDb = Path.Combine(resolvedSourcePath, "beememorybank.db");
        string destDb = Path.Combine(resolvedDestPath, "beememorybank.db");

        Directory.CreateDirectory(resolvedDestPath);

        SqliteConnection.ClearAllPools();

        var destWal = destDb + "-wal";
        var destShm = destDb + "-shm";
        var destJournal = destDb + "-journal";
        if (File.Exists(destWal)) File.Delete(destWal);
        if (File.Exists(destShm)) File.Delete(destShm);
        if (File.Exists(destJournal)) File.Delete(destJournal);

        File.Copy(srcDb, destDb, overwrite: true);

        string srcMedia = Path.Combine(resolvedSourcePath, "media");
        string destMedia = Path.Combine(resolvedDestPath, "media");
        if (Directory.Exists(srcMedia))
        {
            Directory.CreateDirectory(destMedia);
            foreach (var file in Directory.GetFiles(srcMedia))
            {
                string destFile = Path.Combine(destMedia, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }
        }

        var dbFactory = new DbConnectionFactory(destDb);
        var migrationRunner = new MigrationRunner(dbFactory);
        await migrationRunner.RunMigrationsAsync();
    }

    /// <summary>
    /// Creates a self-contained legacy fixture at a fresh temp directory: runs the real
    /// schema migrations, then inserts a minimal tbl_node_identity row. Tests must not
    /// depend on any real, hardcoded system path (e.g. C:\bee\data) existing on the
    /// machine — that state doesn't exist by default and isn't this test suite's to create
    /// or assume persists between runs.
    /// </summary>
    private static async Task<string> CreateLegacyFixtureAsync(string displayName)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bmb_legacy_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "beememorybank.db");

        var dbFactory = new DbConnectionFactory(dbPath);
        var migrationRunner = new MigrationRunner(dbFactory);
        await migrationRunner.RunMigrationsAsync();

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO tbl_node_identity
                    (node_id, display_name, ed25519_public_key, ed25519_private_key, created_at)
                VALUES
                    ($nodeId, $displayName, $pub, $priv, $createdAt);";
            cmd.Parameters.AddWithValue("$nodeId", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$displayName", displayName);
            cmd.Parameters.AddWithValue("$pub", new byte[32]);
            cmd.Parameters.AddWithValue("$priv", new byte[32]);
            cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        return dir;
    }

    [Fact]
    public async Task ValidatePath_ShouldFindAndValidateLegacyData()
    {
        var legacyDir = await CreateLegacyFixtureAsync("Legacy Test Node");
        try
        {
            var candidate = ValidatePath(legacyDir);

            candidate.Should().NotBeNull();
            candidate!.DisplayName.Should().Be("Legacy Test Node");
            candidate.NodeId.Should().NotBeNullOrEmpty();
            candidate.IsValid.Should().BeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(legacyDir, true);
        }
    }

    [Fact]
    public async Task CopyLegacyData_ShouldCopyDatabaseAndMediaAndRunMigrations()
    {
        var legacyDir = await CreateLegacyFixtureAsync("Legacy Test Node");
        var tempDestPath = Path.Combine(Path.GetTempPath(), $"bmb_migration_dest_{Guid.NewGuid():N}");
        try
        {
            var sourceMediaDir = Path.Combine(legacyDir, "media");
            Directory.CreateDirectory(sourceMediaDir);
            var testMediaFile = Path.Combine(sourceMediaDir, "test_file.enc");
            await File.WriteAllTextAsync(testMediaFile, "fake-encrypted-media");

            await CopyLegacyDataAsync(legacyDir, tempDestPath);

            var copiedDb = Path.Combine(tempDestPath, "beememorybank.db");
            File.Exists(copiedDb).Should().BeTrue();

            var copiedMediaFile = Path.Combine(tempDestPath, "media", "test_file.enc");
            File.Exists(copiedMediaFile).Should().BeTrue();
            (await File.ReadAllTextAsync(copiedMediaFile)).Should().Be("fake-encrypted-media");

            var candidate = ValidatePath(tempDestPath);
            candidate.Should().NotBeNull();
            candidate!.DisplayName.Should().Be("Legacy Test Node");
            candidate.IsValid.Should().BeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(legacyDir))
                Directory.Delete(legacyDir, true);
            if (Directory.Exists(tempDestPath))
                Directory.Delete(tempDestPath, true);
        }
    }
}
