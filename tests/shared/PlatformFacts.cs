using System.Runtime.InteropServices;
using Xunit;

namespace BeeMemoryBank.TestSupport;

/// <summary>A fact for an assertion only Windows can make (a share mode, a Windows API); skipped, visibly, on Linux and macOS.</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows-specific assertion; its Linux and macOS counterpart runs at the platform gate (docs/design/step-b-spec.md 15.5).";
    }
}

/// <summary>A fact for an assertion only Linux and macOS can make (an inode, link(2), a permission bit); skipped on Windows.</summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Linux/macOS-specific assertion; run at the platform gate (docs/design/step-b-spec.md 15.5).";
    }
}

/// <summary>Real hard links for the tests: .NET has no API for them (CreateHardLinkW on Windows, link(2) elsewhere).</summary>
public static class TestLinks
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLinkWindows(string newName, string existing, IntPtr security);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int LinkUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string newName);

    public static void CreateHardLink(string existing, string newName)
    {
        var ok = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(newName, existing, IntPtr.Zero)
            : LinkUnix(existing, newName) == 0;
        if (!ok) throw new IOException($"Could not create a hard link (error {Marshal.GetLastWin32Error()}).");
    }
}
