using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BeeMemoryBank.Desktop.MacOS.Interop;
using BeeMemoryBank.Desktop.Services;
using BeeMemoryBank.Platforms.Apple.LaunchAgents;

namespace BeeMemoryBank.Desktop.MacOS;

public sealed class MacOsAutostartOptions
{
    /// <summary>The full app's agent label (also its bundle id). The blind app has its own.</summary>
    public const string DefaultLabel = "com.beememorybank.desktop";

    public string Label { get; init; } = DefaultLabel;

    /// <summary>Where the plist goes. Null means the user's <c>~/Library/LaunchAgents</c>; tests point it at a folder of their own.</summary>
    public string? LaunchAgentsDirectory { get; init; }

    /// <summary>What launchd starts. Null means the running app followed by <c>--minimized</c>; tests give their own program.</summary>
    public IReadOnlyList<string>? ProgramArguments { get; init; }

    /// <summary>
    /// Also load the agent into the running login session now (<c>launchctl bootstrap gui/&lt;uid&gt;</c>). Because the agent has
    /// <c>RunAtLoad</c>, loading starts the program once; the app's single-instance check makes that second copy exit at once. False (the
    /// default) leaves the agent for the next sign-in: the running app does not need a second copy of itself.
    /// </summary>
    public bool LoadImmediately { get; init; }
}

/// <summary>
/// Where a login item may point. A plist that starts the app from a build folder or a temporary folder starts nothing the day after:
/// the folder is cleaned, rebuilt or gone. Refused with a reason, not written.
/// </summary>
internal static partial class ProgramLocationPolicy
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

/// <summary>
/// Autostart at sign-in as a per-user LaunchAgent: <c>~/Library/LaunchAgents/&lt;label&gt;.plist</c> (<see cref="LaunchAgentPlist"/>: the app's
/// executable plus <c>--minimized</c>, <c>RunAtLoad</c>, no <c>KeepAlive</c>, only in a graphical session). Written atomically and read
/// back; removed by deleting that one file and, when launchd has the job loaded, <c>launchctl bootout</c>. A later stage may move to
/// <c>SMAppService</c> for the signed app; the seam stays.
///
/// <para>It never claims what it did not do: a plist that cannot be written, or that does not read back as a valid login item, is an
/// exception (the Settings window shows it); a <c>launchctl</c> call that did not take is a <see cref="LastWarning"/>. Both calls are
/// idempotent: enabling twice leaves the file untouched, disabling twice finds nothing to do.</para>
///
/// <para>One trap, handled: when launchd itself started this process from this agent, <c>bootout</c> would kill the very app that is
/// switching autostart off. Then the plist is removed (so the next sign-in does not start the app) and the loaded job is left alone; it
/// goes away with the login session.</para>
/// </summary>
public sealed class MacOsAutostart : IAutostartService
{
    private const UnixFileMode PlistMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(10);

    private readonly MacOsAutostartOptions _options;
    private readonly ICommandRunner _runner;
    private readonly Func<uint> _uid;
    private readonly Func<string, string?> _environment;
    private readonly Func<string?> _processPath;
    private readonly Func<string, bool> _fileExists;
    private readonly string? _tempPath;
    private readonly ILinkReader _links;
    private readonly object _gate = new();

    public MacOsAutostart(MacOsAutostartOptions? options = null)
        : this(options ?? new MacOsAutostartOptions(), new ProcessCommandRunner(),
            static () => OperatingSystem.IsMacOS() ? LibC.getuid() : uint.MaxValue,
            Environment.GetEnvironmentVariable, static () => Environment.ProcessPath, File.Exists, tempPath: null, new FileSystemLinkReader()) { }

    internal MacOsAutostart(MacOsAutostartOptions options, ICommandRunner runner, Func<uint> uid, Func<string, string?> environment,
        Func<string?> processPath, Func<string, bool> fileExists, string? tempPath, ILinkReader? links = null)
    {
        if (!LaunchAgentPlist.IsValidLabel(options.Label)) throw new ArgumentException("The autostart label is not a valid label.", nameof(options));
        _options = options;
        _runner = runner;
        _uid = uid;
        _environment = environment;
        _processPath = processPath;
        _fileExists = fileExists;
        _tempPath = tempPath;
        _links = links ?? new FileSystemLinkReader();
    }

    public string Label => _options.Label;

    public string PlistPath => Path.Combine(
        _options.LaunchAgentsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"),
        _options.Label + ".plist");

    public string? LastWarning { get; private set; }

    /// <summary>
    /// True when the plist is this agent's (right label, <c>RunAtLoad</c>), the program it starts exists, and that program is the one this
    /// copy of the app would write now (the app has not moved since: the Windows side compares the registry value the same way). A file
    /// that cannot be read, or is not a login item, is "not enabled".
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                if (!File.Exists(PlistPath)) return false;
                var info = LaunchAgentPlist.TryParse(File.ReadAllText(PlistPath));
                if (info is null || info.Label != _options.Label || !info.RunAtLoad || info.ProgramArguments.Count == 0) return false;
                if (!_fileExists(info.ProgramArguments[0])) return false;

                var expected = TryExpectedProgram();
                return expected is null || info.ProgramArguments.SequenceEqual(expected);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>launchd has the agent loaded in this login session.</summary>
    public bool IsLoaded() => _runner.Run(MacTools.Launchctl, ["print", $"{Domain()}/{_options.Label}"], ToolTimeout).Succeeded;

    public void Enable()
    {
        lock (_gate)
        {
            LastWarning = null;
            var program = ResolveProgram();   // refuses a build or temp folder, a missing program

            var text = LaunchAgentPlist.Build(_options.Label, program);
            var path = PlistPath;
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            var changed = !string.Equals(existing, text, StringComparison.Ordinal);
            if (changed) WriteAtomically(path, text);

            // Read it back: success is what the file now says, not that the write call returned.
            var written = LaunchAgentPlist.TryParse(File.Exists(path) ? File.ReadAllText(path) : null);
            if (written is null || written.Label != _options.Label || !written.RunAtLoad || !written.ProgramArguments.SequenceEqual(program))
                throw new InvalidOperationException($"The login item was written to '{path}' but does not read back as a valid login item of this app.");

            if (_options.LoadImmediately) LoadNow(path, replaceLoaded: changed && existing is not null);
        }
    }

    public void Disable()
    {
        lock (_gate)
        {
            LastWarning = null;
            var path = PlistPath;
            var loaded = IsLoaded();

            if (File.Exists(path))
            {
                var info = LaunchAgentPlist.TryParse(File.ReadAllText(path));
                if (info is not null && info.Label != _options.Label)
                    throw new InvalidOperationException($"The file '{path}' is not this app's login item (it names the job '{info.Label}'); it was left alone.");
                File.Delete(path);   // the product's own login-item file, and nothing else
            }
            if (File.Exists(path)) throw new IOException($"The login item '{path}' could not be removed.");

            if (!loaded) return;
            if (RunningAsThisJob())
            {
                LastWarning = "This copy was started by the login item, so the loaded job is left alone (stopping it would end the app); it is gone at the next sign-in.";
                return;
            }

            var boot = _runner.Run(MacTools.Launchctl, ["bootout", $"{Domain()}/{_options.Label}"], ToolTimeout);
            if (!boot.Succeeded && IsLoaded())
                LastWarning = $"The login item file was removed but launchd still has the job loaded (exit {boot.ExitCode}): {Trim(boot.StandardError)}";
        }
    }

    private void LoadNow(string path, bool replaceLoaded)
    {
        var loaded = IsLoaded();
        if (loaded && replaceLoaded && !RunningAsThisJob())
        {
            // The loaded job still has the old program: replace it.
            _runner.Run(MacTools.Launchctl, ["bootout", $"{Domain()}/{_options.Label}"], ToolTimeout);
            loaded = IsLoaded();
        }
        if (loaded) return;

        var boot = _runner.Run(MacTools.Launchctl, ["bootstrap", Domain(), path], ToolTimeout);
        if (boot.Succeeded || IsLoaded()) return;
        LastWarning = $"The login item is in place and starts at the next sign-in, but launchd refused to load it now (exit {boot.ExitCode}): {Trim(boot.StandardError)}";
    }

    /// <summary>The program arguments to write: the explicit ones of the options, or the running app plus <c>--minimized</c>. Throws when refused.</summary>
    private IReadOnlyList<string> ResolveProgram()
    {
        var program = _options.ProgramArguments is { Count: > 0 } explicitProgram ? explicitProgram : [_processPath() ?? "", "--minimized"];
        var refusal = ProgramLocationPolicy.Refusal(program[0], _tempPath, _links);
        if (refusal is not null) throw new InvalidOperationException(refusal);
        if (!_fileExists(program[0])) throw new InvalidOperationException($"The program '{program[0]}' does not exist, so it cannot be a login item.");
        return program;
    }

    /// <summary>What this copy would write; null when it could not (a development run), in which case any valid plist of this label counts.</summary>
    private IReadOnlyList<string>? TryExpectedProgram()
    {
        try { return ResolveProgram(); }
        catch (InvalidOperationException) { return null; }
    }

    private string Domain() => $"gui/{_uid()}";

    /// <summary>launchd sets XPC_SERVICE_NAME to the label of the job it started.</summary>
    private bool RunningAsThisJob() => string.Equals(_environment("XPC_SERVICE_NAME"), _options.Label, StringComparison.Ordinal);

    private static string Trim(string text) => text.Trim().Replace('\n', ' ');

    private static void WriteAtomically(string path, string text)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PlistMode;
            using (var stream = new FileStream(temporary, options))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing is deleted here, not even the half-written temporary file (a dot-file that launchd ignores): the only file this
            // class ever removes is the login item itself, in Disable.
            throw new IOException($"The login item could not be written to '{path}': {ex.Message}", ex);
        }
    }
}
