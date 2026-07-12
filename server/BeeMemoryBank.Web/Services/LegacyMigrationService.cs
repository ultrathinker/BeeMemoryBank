using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Web.Services;

public class LegacyCandidate
{
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string NodeId { get; set; } = "";
    public bool IsValid { get; set; }
}

public class LegacyMigrationService
{
    public static List<LegacyCandidate> GetCandidates()
    {
        var candidates = new List<LegacyCandidate>();
        var paths = new[]
        {
            @"C:\bee\data",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bmb", "data")
        };

        foreach (var path in paths)
        {
            var candidate = ValidatePath(path);
            if (candidate != null)
            {
                candidates.Add(candidate);
            }
        }
        return candidates;
    }

    public static LegacyCandidate? ValidatePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return null;
        try
        {
            var resolvedPath = Environment.ExpandEnvironmentVariables(rawPath.Trim());
            if (!Directory.Exists(resolvedPath)) return null;

            var dbPath = Path.Combine(resolvedPath, "beememorybank.db");
            if (!File.Exists(dbPath)) return null;

            LegacyCandidate? result = null;
            using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();

                // Check if tbl_node_identity exists
                using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='tbl_node_identity';";
                var tableExists = checkCmd.ExecuteScalar() as string;
                if (tableExists == "tbl_node_identity")
                {
                    // Query tbl_node_identity for nodeId and displayName
                    using var queryCmd = conn.CreateCommand();
                    queryCmd.CommandText = "SELECT node_id, display_name FROM tbl_node_identity LIMIT 1;";
                    using var reader = queryCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        var nodeId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        var displayName = reader.IsDBNull(1) ? "" : reader.GetString(1);

                        result = new LegacyCandidate
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
            // Ignore and return null
        }
        return null;
    }

    public static async Task CopyLegacyDataAsync(string sourcePath, string destPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destPath))
            throw new ArgumentException("Paths cannot be empty.");

        var resolvedSourcePath = Environment.ExpandEnvironmentVariables(sourcePath.Trim());
        var resolvedDestPath = Environment.ExpandEnvironmentVariables(destPath.Trim());

        string srcDb = Path.Combine(resolvedSourcePath, "beememorybank.db");
        string destDb = Path.Combine(resolvedDestPath, "beememorybank.db");

        if (!File.Exists(srcDb))
            throw new FileNotFoundException("Legacy database not found.", srcDb);

        // Ensure destination directory exists
        Directory.CreateDirectory(resolvedDestPath);

        // Clear pools in Microsoft.Data.Sqlite in case we had any connections open
        SqliteConnection.ClearAllPools();

        // Delete existing WAL files in the destination to prevent corruption
        var destWal = destDb + "-wal";
        var destShm = destDb + "-shm";
        var destJournal = destDb + "-journal";
        if (File.Exists(destWal)) File.Delete(destWal);
        if (File.Exists(destShm)) File.Delete(destShm);
        if (File.Exists(destJournal)) File.Delete(destJournal);

        // Copy the database file
        File.Copy(srcDb, destDb, overwrite: true);

        // Copy media files if they exist
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

        // Run MigrationRunner to bring schema up to current
        var dbFactory = new DbConnectionFactory(destDb);
        var migrationRunner = new MigrationRunner(dbFactory);
        await migrationRunner.RunMigrationsAsync();
    }
}
