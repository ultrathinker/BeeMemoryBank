using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BeeMemoryBank.AppPaths;

/// <summary>Who a file is, whatever its name: the volume and the file index, and how many names the file has.</summary>
public readonly record struct FileIdentityInfo(ulong Volume, ulong Index, uint LinkCount)
{
    /// <summary>True when both describe the same file (two names of one file share volume and index).</summary>
    public bool IsSameFileAs(FileIdentityInfo other) => Volume == other.Volume && Index == other.Index;
}

/// <summary>
/// The identity of a file independent of its name (docs/design/step-b-spec.md 10.2): volume and file index, from
/// <c>GetFileInformationByHandle</c> on Windows and <c>fstat</c> on Linux and macOS. The scanner and the erase use it to see that a
/// finding is another name of a live database (a hard link) and that a live file has a second name; a wipe through such a name would
/// overwrite the live database. Null when the identity cannot be read - callers fail closed.
/// </summary>
public static class FileIdentity
{
    /// <summary>
    /// True where the identity is read through libc <c>stat</c>/<c>fstat</c>. .NET answers false to <c>IsLinux()</c> on Android although its libc
    /// (bionic) has the same calls and, on arm64, the same <c>struct stat</c> layout; without it a phone could never pass the scan of its own folder.
    /// </summary>
    internal static bool UsesUnixStat(bool isLinux, bool isMacOs, bool isAndroid) => isLinux || isMacOs || isAndroid;

    private static bool UsesUnixStat() => UsesUnixStat(OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), OperatingSystem.IsAndroid());

    /// <summary>The identity of <paramref name="path"/> (symbolic links are followed, as an open would), or null when it cannot be read.</summary>
    public static FileIdentityInfo? TryGet(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return TryGetWindows(path);
            if (UsesUnixStat()) return TryGetUnix(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The identity from an already-held handle, without opening the path again.</summary>
    public static FileIdentityInfo? TryGet(SafeFileHandle handle)
    {
        try
        {
            if (handle is null || handle.IsInvalid) return null;
            if (OperatingSystem.IsWindows()) return TryGetWindowsHandle(handle);
            if (UsesUnixStat()) return TryGetUnixHandle(handle);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    // ---- Windows ---------------------------------------------------------------------------------------------------------------

    private const uint FileShareAll = 0x7, OpenExisting = 3, FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh, LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    private static FileIdentityInfo? TryGetWindows(string path)
    {
        // Access 0: the handle only asks for attributes, so it conflicts with no share mode - a file another program holds exclusively
        // still has an identity.
        using var handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        return TryGetWindowsHandle(handle);
    }

    private static FileIdentityInfo? TryGetWindowsHandle(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) return null;
        return new FileIdentityInfo(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
    }

    // ---- Linux and macOS -------------------------------------------------------------------------------------------------------

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int fd, IntPtr buffer);

    // glibc before 2.33 has no exported fstat; the versioned __fxstat is the entry point (1 = _STAT_VER_LINUX on x86-64, 0 elsewhere).
    [DllImport("libc", EntryPoint = "__fxstat", SetLastError = true)]
    private static extern int FxStat(int version, int fd, IntPtr buffer);

    private const int StatBufferSize = 512;

    private static FileIdentityInfo? TryGetUnix(string path)
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        if (arch is not (Architecture.X64 or Architecture.Arm64)) return null;

        var buffer = Marshal.AllocHGlobal(StatBufferSize);
        try { return TryReadUnixStat(buffer, () => Stat(path, buffer)); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int Stat(string path, IntPtr buffer);

    private static FileIdentityInfo? TryGetUnixHandle(SafeFileHandle handle)
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        var fd = (int)handle.DangerousGetHandle();
        var buffer = Marshal.AllocHGlobal(StatBufferSize);
        try
        {
            return TryReadUnixStat(buffer, () =>
            {
                try { return FStat(fd, buffer); }
                catch (EntryPointNotFoundException) { return FxStat(arch == Architecture.X64 ? 1 : 0, fd, buffer); }
            });
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static FileIdentityInfo? TryReadUnixStat(IntPtr buffer, Func<int> stat)
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        if (arch is not (Architecture.X64 or Architecture.Arm64)) return null;
        for (var i = 0; i < StatBufferSize; i += 8) Marshal.WriteInt64(buffer, i, 0);
        if (stat() != 0) return null;

        if (OperatingSystem.IsMacOS())
        {
            // struct stat (64-bit inodes): st_dev int32 @0, st_mode u16 @4, st_nlink u16 @6, st_ino u64 @8.
            return new FileIdentityInfo((uint)Marshal.ReadInt32(buffer, 0), (ulong)Marshal.ReadInt64(buffer, 8), (ushort)Marshal.ReadInt16(buffer, 6));
        }
        if (arch == Architecture.X64)
        {
            // Linux x86-64: st_dev u64 @0, st_ino u64 @8, st_nlink u64 @16.
            return new FileIdentityInfo((ulong)Marshal.ReadInt64(buffer, 0), (ulong)Marshal.ReadInt64(buffer, 8), (uint)Marshal.ReadInt64(buffer, 16));
        }
        // Linux arm64 (asm-generic): st_dev u64 @0, st_ino u64 @8, st_mode u32 @16, st_nlink u32 @20.
        return new FileIdentityInfo((ulong)Marshal.ReadInt64(buffer, 0), (ulong)Marshal.ReadInt64(buffer, 8), (uint)Marshal.ReadInt32(buffer, 20));
    }
}
