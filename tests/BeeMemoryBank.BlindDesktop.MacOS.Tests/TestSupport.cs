using BeeMemoryBank.BlindDesktop.MacOS;

namespace BeeMemoryBank.BlindDesktop.MacOS.Tests;

/// <summary>A test that needs a real Mac (Keychain, IOKit, launchd, the system tools). Skipped, not failed, everywhere else.</summary>
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "needs macOS";
    }
}

/// <summary>A folder that exists for one test and is removed with it (only what the test created inside it).</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; }

    public TempFolder(string? root = null)
    {
        Path = System.IO.Path.Combine(root ?? System.IO.Path.GetTempPath(), "bmb-mac-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A scripted stand-in for the system tools: answers by exact command line, records every call.</summary>
internal sealed class FakeCommandRunner : ICommandRunner
{
    private readonly Dictionary<string, CommandResult> _results = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public List<string> Calls { get; } = [];

    public FakeCommandRunner On(string fileName, string arguments, string output, int exitCode = 0, string error = "")
    {
        _results[fileName + " " + arguments] = new CommandResult(exitCode, output, error, TimedOut: false);
        return this;
    }

    public FakeCommandRunner Fail(string fileName, string arguments) => On(fileName, arguments, "", exitCode: 1, error: "failed");

    public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var key = fileName + " " + string.Join(' ', arguments);
        lock (_gate)
        {
            Calls.Add(key);
            return _results.TryGetValue(key, out var result) ? result : new CommandResult(-1, "", "not scripted: " + key, TimedOut: false);
        }
    }

    public int CallCount(string startsWith) => Calls.Count(c => c.StartsWith(startsWith, StringComparison.Ordinal));
}
