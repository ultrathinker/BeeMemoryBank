using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

public partial class SnapshotService
{
    private const string DbFileName = "beememorybank.db";
    private const string ManifestFileName = "manifest.json";

    private const string DbEncryptionMagicV1 = "BMBDB1";
    private const string DbEncryptionMagicV2 = "BMBDB2";
    private const int DbEncryptionOverheadV1 = 6 + 12 + 16;
    private const int DbEncryptionOverheadV2 = 6 + 16 + 12 + 16;
    private const long MaxEncryptableDbSize = 2L * 1024 * 1024 * 1024;

    private readonly string _dataPath;
    private readonly DbConnectionFactory _connFactory;
    private readonly INodeIdentityRepository? _nodeRepo;
    private readonly ILamportClock? _clock;
    private readonly ILogger<SnapshotService>? _logger;
    private readonly IRestoreReplayShieldRepository? _replayShieldRepo;
    private readonly IWhitelistRepository? _whitelistRepo;
    private readonly BeeMemoryBank.Core.Services.SessionService? _sessionService;

    public string SnapshotsDir => Path.Combine(_dataPath, "snapshots");

    public SnapshotService(string dataPath, DbConnectionFactory connFactory,
        INodeIdentityRepository? nodeRepo = null, ILamportClock? clock = null,
        ILogger<SnapshotService>? logger = null,
        IRestoreReplayShieldRepository? replayShieldRepo = null,
        IWhitelistRepository? whitelistRepo = null,
        BeeMemoryBank.Core.Services.SessionService? sessionService = null)
    {
        _dataPath = dataPath;
        _connFactory = connFactory;
        _nodeRepo = nodeRepo;
        _clock = clock;
        _logger = logger;
        _replayShieldRepo = replayShieldRepo;
        _whitelistRepo = whitelistRepo;
        _sessionService = sessionService;
    }

    public List<SnapshotInfo> List()
    {
        Directory.CreateDirectory(SnapshotsDir);
        return Directory.GetFiles(SnapshotsDir, "*.tar.gz")
            .Select(f => new SnapshotInfo(
                Path.GetFileName(f),
                new FileInfo(f).Length,
                File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }

    public int PruneOldSnapshots(int keepCount = 2)
    {
        Directory.CreateDirectory(SnapshotsDir);
        var files = Directory.GetFiles(SnapshotsDir, "*.tar.gz")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        if (files.Count <= keepCount) return 0;

        int deleted = 0;
        foreach (var fi in files.Skip(keepCount))
        {
            File.Delete(fi.FullName);
            var sigPath = $"{fi.FullName}.sig";
            if (File.Exists(sigPath))
                File.Delete(sigPath);
            deleted++;
        }

        return deleted;
    }

    public bool Delete(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (!safeName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return false;

        var filePath = Path.Combine(SnapshotsDir, safeName);
        if (!File.Exists(filePath)) return false;

        File.Delete(filePath);
        var sigPath = $"{filePath}.sig";
        if (File.Exists(sigPath))
            File.Delete(sigPath);

        return true;
    }

    public string GetSnapshotPath(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (!safeName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) && !safeName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid snapshot file name");
        var filePath = Path.Combine(SnapshotsDir, safeName);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Snapshot {safeName} not found");
        return filePath;
    }

    public string? FindSnapshotFileById(Guid id)
    {
        Directory.CreateDirectory(SnapshotsDir);
        // Match the exact filename suffix `-<id:N>.<ext>` to avoid substring collisions on
        // arbitrary filenames in the directory. SaveUploadedAsync names files
        // `imported-<originator>-<id:N>.bin`; CreateAsync uses `bmb-snapshot-<timestamp>.tar.gz`
        // (timestamps never contain a 32-hex GUID by construction).
        var idStr = id.ToString("N");
        var rootedDir = Path.GetFullPath(SnapshotsDir);
        foreach (var file in Directory.GetFiles(SnapshotsDir))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.EndsWith("-" + idStr, StringComparison.OrdinalIgnoreCase)) continue;
            // Defensive: ensure resolved path is inside SnapshotsDir.
            var resolved = Path.GetFullPath(file);
            if (!resolved.StartsWith(rootedDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && resolved != rootedDir) continue;
            return file;
        }
        return null;
    }
}
