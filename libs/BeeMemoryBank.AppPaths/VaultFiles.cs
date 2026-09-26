using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BeeMemoryBank.AppPaths;

/// <summary>
/// What a folder looks like from the point of view of "can a profile live here".
/// </summary>
public enum VaultFolderState
{
    /// <summary>The folder does not exist yet.</summary>
    Missing,
    /// <summary>The folder exists and has nothing in it.</summary>
    Empty,
    /// <summary>The folder holds a vault: a readable <c>beememorybank.db</c> with a SQLite header.</summary>
    Vault,
    /// <summary>The folder has files, but no vault database.</summary>
    Other,
}

/// <summary>
/// Knowledge about the files inside a vault directory that more than one component needs:
/// which files are runtime-only and must never be carried to another location, and how to
/// recognise a folder that already holds a vault.
/// </summary>
public static class VaultFiles
{
    /// <summary>The main database file of a vault.</summary>
    public const string DatabaseFileName = "beememorybank.db";

    private static readonly byte[] SqliteMagic = Encoding.ASCII.GetBytes("SQLite format 3\0");

    // Written by a running node and meaningless anywhere else: copying node.lock or a stale
    // status/runtime descriptor next to a vault could make the next start think a node is
    // already running there.
    private static readonly HashSet<string> TransientFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node.lock",
        ".runtime.json",
        "node.status.json",
    };

    /// <summary>True for runtime-only files (lock, status, runtime descriptor, *.ready).</summary>
    public static bool IsTransient(string fileName)
    {
        return TransientFileNames.Contains(fileName)
            || fileName.EndsWith(".ready", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Classifies <paramref name="directory"/>. Never throws for an unreadable folder: it reports <see cref="VaultFolderState.Other"/>.</summary>
    public static VaultFolderState Inspect(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return VaultFolderState.Missing;
            if (HasSqliteHeader(Path.Combine(directory, DatabaseFileName))) return VaultFolderState.Vault;
            return Directory.EnumerateFileSystemEntries(directory).Any() ? VaultFolderState.Other : VaultFolderState.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VaultFolderState.Other;
        }
    }

    /// <summary>
    /// True when a node currently holds <c>node.lock</c> in <paramref name="directory"/>. bmbd opens
    /// the file with <see cref="FileShare.None"/> for its whole lifetime, so failing to open it
    /// means somebody is using the vault right now.
    /// </summary>
    public static bool IsLockedByNode(string directory)
    {
        var lockPath = Path.Combine(directory, "node.lock");
        if (!File.Exists(lockPath)) return false;
        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool HasSqliteHeader(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[SqliteMagic.Length];
            return fs.Read(header, 0, header.Length) == header.Length && header.AsSpan().SequenceEqual(SqliteMagic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
