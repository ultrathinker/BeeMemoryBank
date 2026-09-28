using System.Runtime.InteropServices;

namespace BeeMemoryBank.AppPaths;

/// <summary>
/// Renames and directory entries that survive a power cut (review release-b R1-5). Flushing a file's contents does not
/// make its directory entry durable: after a rename, the entry lives in the parent directory's metadata.
/// <list type="bullet">
/// <item>Linux and macOS: the parent directory is opened and <c>fsync</c>ed after every rename that matters.</item>
/// <item>Windows: renames go through <c>MoveFileExW</c> with <c>MOVEFILE_WRITE_THROUGH</c>, which returns only once the
///   rename is on disk; the directory is flushed as well where the file system allows it (best effort).</item>
/// </list>
/// </summary>
public static class DurableFs
{
    /// <summary>Raised after a parent directory was flushed (diagnostics and tests).</summary>
    public static event Action<string>? DirectoryFlushed;

    /// <summary>Renames a file durably, replacing <paramref name="to"/>.</summary>
    public static void MoveFile(string from, string to)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileExW(from, to, MoveFileReplaceExisting | MoveFileWriteThrough))
                throw new IOException($"Could not move {from} to {to}.", Marshal.GetHRForLastWin32Error());
        }
        else
        {
            File.Move(from, to, overwrite: true);
        }
        FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
    }

    /// <summary>Renames a directory durably (same volume).</summary>
    public static void MoveDirectory(string from, string to)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileExW(from, to, MoveFileWriteThrough))
            {
                var error = Marshal.GetLastWin32Error();
                throw error == 5 // ERROR_ACCESS_DENIED: what an open handle inside the directory gives
                    ? new UnauthorizedAccessException($"Could not move {from} to {to} (access denied).")
                    : new IOException($"Could not move {from} to {to} (error {error}).", Marshal.GetHRForLastWin32Error());
            }
        }
        else
        {
            Directory.Move(from, to);
        }
        FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
        var fromParent = Path.GetDirectoryName(Path.GetFullPath(from))!;
        if (!string.Equals(fromParent, Path.GetDirectoryName(Path.GetFullPath(to)), StringComparison.Ordinal)) FlushDirectory(fromParent);
    }

    /// <summary>Makes the entries of <paramref name="dir"/> durable.</summary>
    public static void FlushDirectory(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            // Needs write access to the directory; on file systems that refuse it, MOVEFILE_WRITE_THROUGH already made
            // the rename durable, so a refusal is not an error.
            using var handle = CreateFileW(dir, GenericWrite, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (!handle.IsInvalid) FlushFileBuffers(handle);
        }
        else
        {
            var fd = open(dir, ORdOnly);
            if (fd < 0) throw new IOException($"Could not open directory {dir} to flush it (errno {Marshal.GetLastWin32Error()}).");
            try
            {
                if (fsync(fd) != 0) throw new IOException($"Could not flush directory {dir} (errno {Marshal.GetLastWin32Error()}).");
            }
            finally
            {
                close(fd);
            }
        }
        DirectoryFlushed?.Invoke(dir);
    }

    private const uint MoveFileReplaceExisting = 0x1, MoveFileWriteThrough = 0x8;
    private const uint GenericWrite = 0x40000000, FileShareAll = 0x7, OpenExisting = 3, FileFlagBackupSemantics = 0x02000000;
    private const int ORdOnly = 0;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileExW(string existing, string replacement, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle handle);

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
