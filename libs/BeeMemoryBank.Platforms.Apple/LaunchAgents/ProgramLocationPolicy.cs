using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.Platforms.Apple.LaunchAgents;

/// <summary>
/// Where a login item may point. A plist that starts the app from a build folder or a temporary folder starts nothing the day after:
/// the folder is cleaned, rebuilt or gone. Refused with a reason, not written.
/// </summary>
public static partial class ProgramLocationPolicy
{
    [GeneratedRegex(@"/(bin|obj)/(debug|release)(/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildFolderPattern();

    private static readonly string[] TemporaryRoots =
    [
        "/tmp/", "/private/tmp/", "/var/tmp/", "/private/var/tmp/", "/var/folders/", "/private/var/folders/",
    ];

    /// <summary>
    /// Why <paramref name="program"/> must not be a login item; null when it may be. The rules are applied to the path as given, to the
    /// path with <c>.</c> and <c>..</c> taken out, AND to the path the system really reaches once every symbolic link in the file and in
    /// its folders is followed (<see cref="ProgramPaths.Physical"/>): a link in a normal folder that points into a build or temp folder, or
    /// a <c>..</c> that lands in <c>/tmp</c>, is as refused as the real path would be.
    /// </summary>
    public static string? Refusal(string? program, string? tempPath = null, ILinkReader? links = null)
    {
        if (string.IsNullOrWhiteSpace(program)) return "The path of the running app is not known.";
        if (!program.StartsWith('/')) return $"The program must be an absolute path, not '{program}'.";

        var asGiven = RefusalOfPath(program, tempPath);
        if (asGiven is not null) return asGiven;

        var lexical = ProgramPaths.Lexical(program);
        if (!string.Equals(lexical, program, StringComparison.Ordinal) && RefusalOfPath(lexical, tempPath) is { } viaDots)
            return $"{viaDots} (The path '{program}' means '{lexical}'.)";

        var physical = ProgramPaths.Physical(program, links ?? new FileSystemLinkReader());
        if (physical is null)
            return $"The path '{program}' goes through too many symbolic links (a loop?), so it cannot be a login item.";
        if (!string.Equals(physical, lexical, StringComparison.Ordinal) && RefusalOfPath(physical, tempPath) is { } viaLinks)
            return $"{viaLinks} (The path '{program}' leads to '{physical}'.)";
        return null;
    }

    /// <summary>The rules for one concrete path (no dots, no links to follow): the dotnet host, a build folder, Translocation, a temp folder.</summary>
    private static string? RefusalOfPath(string path, string? tempPath)
    {
        var normalized = path.Replace('\\', '/');
        if (string.Equals(Path.GetFileNameWithoutExtension(normalized), "dotnet", StringComparison.OrdinalIgnoreCase))
            return "This copy runs under the dotnet host (a development run), not as the installed app, so it cannot be a login item.";
        if (BuildFolderPattern().IsMatch(normalized))
            return $"This copy runs from a build folder ({path}), which the next build replaces; install the app first.";
        if (normalized.Contains("/AppTranslocation/", StringComparison.OrdinalIgnoreCase))
            return "This copy runs from a temporary quarantine location (Gatekeeper App Translocation); move the app to the Applications folder and open it from there.";

        var temp = (tempPath ?? Path.GetTempPath()).Replace('\\', '/');
        if (temp.Length > 1 && !temp.EndsWith('/')) temp += "/";
        foreach (var root in TemporaryRoots.Append(temp))
        {
            if (root.Length > 1 && normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return $"This copy runs from a temporary folder ({path}), which the system cleans up; install the app first.";
        }
        return null;
    }
}
