using System;
using BeeMemoryBank.Hosting;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>What a WebView navigation asks the shell to do instead of navigating (<see cref="DesktopShellCommands"/>).</summary>
public enum ShellCommand
{
    /// <summary>An ordinary navigation: not a command.</summary>
    None,
    OpenExistingProfile,
    PickBackupFile,
}

public static class ShellCommands
{
    /// <summary>
    /// The command a navigation names, or <see cref="ShellCommand.None"/>. Only the exact address counts —
    /// a query, another path or another host is an ordinary navigation, so no page can smuggle arguments
    /// into a picker.
    /// </summary>
    public static ShellCommand Match(Uri? request)
    {
        if (request == null || !request.IsAbsoluteUri) return ShellCommand.None;
        var url = request.AbsoluteUri;
        if (string.Equals(url, DesktopShellCommands.OpenExistingProfile, StringComparison.OrdinalIgnoreCase))
            return ShellCommand.OpenExistingProfile;
        if (string.Equals(url, DesktopShellCommands.PickBackupFile, StringComparison.OrdinalIgnoreCase))
            return ShellCommand.PickBackupFile;
        return ShellCommand.None;
    }
}
