using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Extensions.DependencyInjection;

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

/// <summary>
/// What the Avalonia host's lifecycle does for the core's wipe, reduced to what a test can look at: it records the calls and holds no
/// scheduler. The real one (<c>HostLifecycle</c>) lives in the app; these tests exercise the macOS seams, not it.
/// </summary>
internal sealed class RecordingLifecycle : IBlindLifecycle
{
    public int Stopped, Restarted;
    public void StopBackgroundWork() => Stopped++;
    public void StopBackupService() => Stopped++;
    public void RestartAfterWipe() => Restarted++;
}

internal static class TestHost
{
    /// <summary>The macOS seams, a lifecycle, and Blind.AppCore on the same data folder: what <c>BlindDesktopRuntime</c> composes on a Mac, minus the Avalonia parts.</summary>
    public static IServiceCollection AddSeamsAndCore(this IServiceCollection services, MacOsBlindHostOptions options)
    {
        services.AddMacOsBlindSeams(options);
        services.AddSingleton<RecordingLifecycle>();
        services.AddSingleton<IBlindLifecycle>(sp => sp.GetRequiredService<RecordingLifecycle>());
        return BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(options.Paths.DataDirectory, options.Paths.DatabasePath));
    }
}
