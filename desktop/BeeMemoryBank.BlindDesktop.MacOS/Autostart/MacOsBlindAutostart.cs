using System.Text;
using BeeMemoryBank.BlindDesktop.MacOS.Interop;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Platforms.Apple.LaunchAgents;

namespace BeeMemoryBank.BlindDesktop.MacOS;

public sealed class MacOsBlindAutostartOptions
{
    /// <summary>The agent's label: its own, never the full app's.</summary>
    public const string DefaultLabel = "com.beememorybank.blind";

    public string Label { get; init; } = DefaultLabel;

    /// <summary>Where the plist goes. Null means the user's <c>~/Library/LaunchAgents</c>; tests point it at a folder of their own.</summary>
    public string? LaunchAgentsDirectory { get; init; }

    /// <summary>What launchd starts. Null means the running app followed by <c>--minimized</c>.</summary>
    public IReadOnlyList<string>? ProgramArguments { get; init; }

    /// <summary>
    /// Also load the agent into the running login session now (<c>launchctl bootstrap gui/&lt;uid&gt;</c>). Because the agent has
    /// <c>RunAtLoad</c>, loading starts the program once; the host's single-instance check makes that second copy exit at once. False
    /// leaves the agent for the next login.
    /// </summary>
    public bool LoadImmediately { get; init; } = true;
}

/// <summary>What <see cref="MacOsBlindAutostart.Apply"/> ended up with.</summary>
/// <param name="Enabled">The plist is in place (autostart at the next login).</param>
/// <param name="Loaded">launchd has the agent loaded in the current login session.</param>
/// <param name="Warning">Something that did not go as asked but did not fail the change (for example a refused load).</param>
public sealed record MacOsAutostartResult(bool Enabled, bool Loaded, string? Warning);

/// <summary>
/// Autostart at login as a per-user LaunchAgent: <c>~/Library/LaunchAgents/&lt;label&gt;.plist</c> written atomically and loaded with
/// <c>launchctl bootstrap gui/&lt;uid&gt;</c>; disabled by removing the file and <c>launchctl bootout</c>. A later stage may replace this with
/// <c>SMAppService</c> for the signed app; the seam stays.
///
/// <para>One trap, handled: when launchd itself started this process from this agent, <c>bootout</c> would kill the very app that is
/// switching autostart off. Then the plist is removed (so the next login does not start the app) and the loaded job is left alone; it
/// goes away with the login session.</para>
/// </summary>
public sealed class MacOsBlindAutostart : IBlindAutostart
{
    private const UnixFileMode PlistMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(10);

    private readonly MacOsBlindAutostartOptions _options;
    private readonly ICommandRunner _runner;
    private readonly Func<uint> _uid;
    private readonly Func<string, string?> _environment;
    private readonly object _gate = new();

    public MacOsBlindAutostart(MacOsBlindAutostartOptions? options = null)
        : this(options ?? new MacOsBlindAutostartOptions(), new ProcessCommandRunner(), static () => LibC.getuid(), Environment.GetEnvironmentVariable) { }

    internal MacOsBlindAutostart(MacOsBlindAutostartOptions options, ICommandRunner runner, Func<uint> uid, Func<string, string?> environment)
    {
        if (!LaunchAgentPlist.IsValidLabel(options.Label)) throw new ArgumentException("The autostart label is not a valid label.", nameof(options));
        _options = options;
        _runner = runner;
        _uid = uid;
        _environment = environment;
    }

    public string Label => _options.Label;

    public string PlistPath => Path.Combine(
        _options.LaunchAgentsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"),
        _options.Label + ".plist");

    /// <summary>The program arguments launchd would be given: the running app, then <c>--minimized</c>.</summary>
    public IReadOnlyList<string> ProgramArguments => _options.ProgramArguments ?? DefaultProgramArguments();

    /// <summary>
    /// Whether the app starts at login (<see cref="IBlindAutostart.IsEnabled"/>): true when the plist exists and is this agent's (right
    /// label, <c>RunAtLoad</c>, a program to start); false when there is none or it is not ours; null when the file is there but cannot be
    /// read (a sharing or permission problem) - the screen then shows the toggle as unknown instead of guessing.
    /// </summary>
    public bool? IsEnabled
    {
        get
        {
            var info = Read(out var readable);
            if (!readable) return null;
            return info is not null && info.Label == _options.Label && info.RunAtLoad && info.ProgramArguments.Count > 0;
        }
    }

    /// <summary>The plist is what <see cref="Apply"/> would write now (the app has not moved since it was written).</summary>
    public bool IsCurrent => Read(out _) is { } info && info.Label == _options.Label && info.RunAtLoad && info.ProgramArguments.SequenceEqual(ProgramArguments);

    /// <summary>launchd has the agent loaded in this login session.</summary>
    public bool IsLoaded() => _runner.Run(MacTools.Launchctl, ["print", $"{Domain()}/{_options.Label}"], ToolTimeout).Succeeded;

    public void SetEnabled(bool enabled) => Apply(enabled);

    /// <summary>
    /// Turns autostart on or off. File failures throw; a launchctl call that did not take is reported in the result and can be retried
    /// (the plist alone already starts the app at the next login).
    /// </summary>
    public MacOsAutostartResult Apply(bool enabled)
    {
        lock (_gate)
        {
            return enabled ? Enable() : Disable();
        }
    }

    private MacOsAutostartResult Enable()
    {
        var text = LaunchAgentPlist.Build(_options.Label, ProgramArguments);
        var path = PlistPath;
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var changed = !string.Equals(existing, text, StringComparison.Ordinal);
        if (changed) WriteAtomically(path, text);

        if (!_options.LoadImmediately) return new MacOsAutostartResult(true, IsLoaded(), null);

        var loaded = IsLoaded();
        if (loaded && changed && existing is not null && !RunningAsThisJob())
        {
            // The loaded job still has the old program: replace it.
            var unload = _runner.Run(MacTools.Launchctl, ["bootout", $"{Domain()}/{_options.Label}"], ToolTimeout);
            loaded = !unload.Succeeded && IsLoaded();
        }
        if (loaded) return new MacOsAutostartResult(true, true, null);

        var boot = _runner.Run(MacTools.Launchctl, ["bootstrap", Domain(), path], ToolTimeout);
        if (boot.Succeeded || IsLoaded()) return new MacOsAutostartResult(true, true, null);
        return new MacOsAutostartResult(true, false,
            $"The login item is in place and starts at the next login, but launchd refused to load it now (exit {boot.ExitCode}): {Trim(boot.StandardError)}");
    }

    private MacOsAutostartResult Disable()
    {
        var path = PlistPath;
        var loaded = IsLoaded();
        if (File.Exists(path)) File.Delete(path);

        if (!loaded) return new MacOsAutostartResult(false, false, null);
        if (RunningAsThisJob())
            return new MacOsAutostartResult(false, true,
                "This copy was started by the login item, so the loaded job is left alone (stopping it would end the app); it is gone at the next login.");

        var boot = _runner.Run(MacTools.Launchctl, ["bootout", $"{Domain()}/{_options.Label}"], ToolTimeout);
        var stillLoaded = !boot.Succeeded && IsLoaded();
        return new MacOsAutostartResult(false, stillLoaded,
            stillLoaded ? $"The login item file was removed but launchd still has the job loaded (exit {boot.ExitCode}): {Trim(boot.StandardError)}" : null);
    }

    /// <summary>The parsed plist; null when there is none or it is garbage. <paramref name="readable"/> is false only when it could not be read at all.</summary>
    private LaunchAgentInfo? Read(out bool readable)
    {
        readable = true;
        try { return File.Exists(PlistPath) ? LaunchAgentPlist.TryParse(File.ReadAllText(PlistPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            readable = false;
            return null;
        }
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
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }

    private static IReadOnlyList<string> DefaultProgramArguments()
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("The path of the running app is not known.");
        // Started as "dotnet App.dll": launchd must start dotnet with the dll, not the dll.
        if (string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase) &&
            Environment.GetCommandLineArgs() is { Length: > 0 } args && args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return [process, Path.GetFullPath(args[0]), "--minimized"];
        return [process, "--minimized"];
    }
}
