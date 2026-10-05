using System;
using System.Diagnostics;
using System.IO;
using BeeMemoryBank.Desktop.Services;

namespace BeeMemoryBank.Desktop.MacOS;

/// <summary>
/// "Open the folder" and "show it in the file manager" for a Mac: <c>/usr/bin/open &lt;folder&gt;</c> and <c>/usr/bin/open -R &lt;item&gt;</c>, started
/// directly with the path as ONE argument of an argument list - no shell and no string building, so a folder with spaces, quotes or a
/// leading dash is just a name. The path is made absolute first, so it can never look like an option of <c>open</c>.
/// </summary>
public sealed class MacOsFileManager : IFileManagerReveal
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);

    private readonly ICommandRunner _runner;
    private readonly Action<string> _ensureDirectory;
    private readonly Action<string> _log;

    public MacOsFileManager() : this(new ProcessCommandRunner(), path => Directory.CreateDirectory(path), Console.Error.WriteLine) { }

    internal MacOsFileManager(ICommandRunner runner, Action<string> ensureDirectory, Action<string> log)
    {
        _runner = runner;
        _ensureDirectory = ensureDirectory;
        _log = log;
    }

    public void OpenFolder(string path)
    {
        var full = Absolute(path);
        if (full is null) return;

        // Same as the Windows side: a vault that was never started has no folder yet, and `open` would fail on it.
        try { _ensureDirectory(full); }
        catch (Exception ex) { _log($"[MacOsFileManager] Could not create '{full}': {ex.Message}"); }

        Launch(full, [full]);
    }

    public void Reveal(string path)
    {
        var full = Absolute(path);
        if (full is null) return;

        Launch(full, ["-R", full]);
    }

    private void Launch(string path, string[] arguments)
    {
        try
        {
            var result = _runner.Run(MacTools.Open, arguments, OpenTimeout);
            if (!result.Succeeded)
                _log($"[MacOsFileManager] open failed for '{path}' (exit {result.ExitCode}{(result.TimedOut ? ", timed out" : "")}): {result.StandardError.Trim()}");
        }
        catch (Exception ex)
        {
            _log($"[MacOsFileManager] open failed for '{path}': {ex.Message}");
        }
    }

    /// <summary>The path as an absolute one (a relative path is taken from the current folder); null for an empty one.</summary>
    internal static string? Absolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return path.StartsWith('/') ? path : Path.GetFullPath(path);
    }
}
