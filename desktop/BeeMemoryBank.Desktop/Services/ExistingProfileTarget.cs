using System;
using System.IO;
using System.Linq;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>What the user picked in "Open an existing profile".</summary>
public enum ExistingProfileKind
{
    /// <summary>A profile folder (or anything else): opened as the active profile, as before.</summary>
    Profile,
    /// <summary>A backup that restores with the master password: a blind node's backup folder, a restic
    /// repository with its recovery set, or an Android blind node's backup file.</summary>
    Backup,
}

/// <summary>A picked path, classified; <see cref="BackupPath"/> is what the restore form gets.</summary>
public sealed record ExistingProfileTarget(ExistingProfileKind Kind, string Path, string? BackupPath)
{
    /// <summary>File extension of an Android blind node's backup (BlindPhoneBackupRunner.Extension).</summary>
    public const string AndroidBackupExtension = ".bmbbackup";

    // The first 8 bytes of an Android backup (AndroidBackupFile in BeeMemoryBank.Crypto, which this app does
    // not reference; a test writes a real backup with it, so the two cannot drift apart).
    private static readonly byte[] AndroidBackupMagic = "BMBABK01"u8.ToArray();

    /// <summary>
    /// Sorts a path picked natively (folder or file) into a profile to open or a backup to restore — the
    /// same shapes the restore pipeline accepts: a folder with <c>*.recovery-set.json</c>, a restic
    /// repository with <c>&lt;repo&gt;.recovery-set.json</c> beside it, an Android backup file, or a folder
    /// holding Android backup files (the newest one is taken — where "Save to…" on the phone put them).
    /// </summary>
    public static ExistingProfileTarget Classify(string path)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        try
        {
            if (File.Exists(full))
                return IsAndroidBackup(full)
                    ? new(ExistingProfileKind.Backup, full, full)
                    : new(ExistingProfileKind.Profile, full, null);

            if (Directory.Exists(full))
            {
                if (Directory.EnumerateFiles(full, "*.recovery-set.json").Any() || File.Exists(full + ".recovery-set.json"))
                    return new(ExistingProfileKind.Backup, full, full);

                // A profile folder that also happens to hold a phone backup is still a profile.
                if (!File.Exists(System.IO.Path.Combine(full, "beememorybank.db")))
                {
                    var newest = Directory.EnumerateFiles(full, "*" + AndroidBackupExtension)
                        .Where(f => f.EndsWith(AndroidBackupExtension, StringComparison.OrdinalIgnoreCase) && IsAndroidBackup(f))
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (newest != null) return new(ExistingProfileKind.Backup, full, newest);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: let the ordinary open report it.
        }
        return new(ExistingProfileKind.Profile, full, null);
    }

    /// <summary>The first-run wizard's restore form, pre-filled with the backup (no path to type).</summary>
    public static Uri RestoreFormUrl(string frontUrl, string backupPath) =>
        new($"{frontUrl.TrimEnd('/')}/Setup?step=restore&backup={Uri.EscapeDataString(backupPath)}");

    private static bool IsAndroidBackup(string file)
    {
        using var stream = File.OpenRead(file);
        var head = new byte[AndroidBackupMagic.Length];
        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
            && head.AsSpan().SequenceEqual(AndroidBackupMagic);
    }
}
