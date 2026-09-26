using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BeeMemoryBank.AppPaths;

namespace BeeMemoryBank.Profiles;

/// <summary>Outcome of <see cref="VaultCopier.CopyVerified"/>.</summary>
public sealed record VaultCopyResult(int FileCount, long TotalBytes);

/// <summary>
/// Copies a vault directory to a new location for "move profile". The source is never
/// modified; the copy is verified file by file (presence and size) before the caller is
/// allowed to repoint the profile at it. The node that uses the vault must be stopped first,
/// otherwise the database files may change under the copy.
/// </summary>
public static class VaultCopier
{
    /// <summary>
    /// Checks that <paramref name="destination"/> can receive a copy of <paramref name="source"/>.
    /// Returns an error message for the user, or null when the target is fine.
    /// </summary>
    public static string? ValidateTarget(string source, string destination)
    {
        var src = Normalize(source);
        var dst = Normalize(destination);

        if (string.Equals(src, dst, PathComparison))
            return "The profile is already in this folder.";
        if (IsInside(dst, src))
            return "The new folder cannot be inside the profile's current folder.";
        if (IsInside(src, dst))
            return "The new folder cannot contain the profile's current folder.";
        if (BmbPaths.IsInsideVelopackCurrentDir(dst))
            return "This folder is inside the application folder, which is replaced on every update. Choose a folder outside the installation.";

        return VaultFiles.Inspect(dst) switch
        {
            VaultFolderState.Missing or VaultFolderState.Empty => null,
            VaultFolderState.Vault => "This folder already contains a Bee Memory Bank profile.",
            _ => "Choose an empty folder.",
        };
    }

    /// <summary>
    /// Copies every non-transient file of <paramref name="source"/> into <paramref name="destination"/>
    /// and verifies the result. Throws <see cref="IOException"/> if the copy is incomplete.
    /// </summary>
    public static VaultCopyResult CopyVerified(string source, string destination, IProgress<string>? progress, CancellationToken ct)
    {
        var error = ValidateTarget(source, destination);
        if (error != null) throw new ArgumentException(error, nameof(destination));

        var files = new List<(string Relative, long Length)>();
        Collect(source, source, files);

        long totalBytes = 0;
        foreach (var f in files) totalBytes += f.Length;

        long copiedBytes = 0;
        var lastReport = DateTime.MinValue;
        Directory.CreateDirectory(destination);
        foreach (var (relative, length) in files)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source, relative), target, overwrite: false);
            copiedBytes += length;

            if (progress != null && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
            {
                lastReport = DateTime.UtcNow;
                progress.Report($"Copying data... {Percent(copiedBytes, totalBytes)}%");
            }
        }

        progress?.Report("Checking the copy...");
        foreach (var (relative, length) in files)
        {
            var target = new FileInfo(Path.Combine(destination, relative));
            if (!target.Exists || target.Length != length)
                throw new IOException($"The copy of '{relative}' is incomplete.");
        }

        return new VaultCopyResult(files.Count, totalBytes);
    }

    private static void Collect(string root, string dir, List<(string, long)> files)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (VaultFiles.IsTransient(Path.GetFileName(file))) continue;
            files.Add((Path.GetRelativePath(root, file), new FileInfo(file).Length));
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            // Same rule as the legacy rescue: never follow junctions/symlinks out of the vault.
            if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0) continue;
            Collect(root, sub, files);
        }
    }

    private static int Percent(long done, long total) => total == 0 ? 100 : (int)(done * 100 / total);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsInside(string candidate, string parent) =>
        candidate.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison);
}
