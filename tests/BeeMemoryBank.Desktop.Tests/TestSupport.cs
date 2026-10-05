using System;
using System.Collections.Generic;
using System.IO;
using BeeMemoryBank.Desktop.MacOS;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>A test that needs a real Mac (IOKit, launchd, pmset). Skipped, not failed, everywhere else - and never skipped on a Mac.</summary>
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "needs macOS";
    }
}

/// <summary>
/// The folder the REAL-system Mac tests (launchd, osascript, pmset, symbolic links) work in. The person running the tests chooses it with
/// the environment variable <c>BMB_MAC_TEST_ROOT</c>: a folder inside their home folder that is not a temporary or build folder. It is
/// deliberately not derived from the temp folder: those tests start real <c>launchctl</c> jobs, and a login item must never point into a
/// temp folder (the product refuses it), so the machine's TMPDIR must not decide whether the tests pass.
/// </summary>
internal static class MacTestRoot
{
    public const string Variable = "BMB_MAC_TEST_ROOT";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/');

    /// <summary>Null when the variable names a usable folder; otherwise the reason the real tests are skipped.</summary>
    public static string? Problem() => Problem(Environment.GetEnvironmentVariable(Variable), Home, Path.GetTempPath());

    /// <summary>The rules for a root, as pure text (so they are tested on every OS).</summary>
    internal static string? Problem(string? value, string home, string tempPath)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"set {Variable} to a folder inside your home folder (not a temporary folder) to run the real launchd / osascript / pmset tests";
        if (!value.StartsWith('/')) return $"{Variable} must be an absolute path, not '{value}'";
        if (value.Split('/').Any(segment => segment is "." or ".."))
            return $"{Variable} must be a plain path without dot or dot-dot segments, not '{value}'";

        var full = value.TrimEnd('/');
        home = home.TrimEnd('/');
        if (full.Length == 0 || full == home || !(full + "/").StartsWith(home + "/", StringComparison.Ordinal))
            return $"{Variable} must be a folder inside the home folder {home}, not '{full}'";
        var refusal = ProgramLocationPolicy.Refusal(full + "/x", tempPath);
        if (refusal is not null) return $"{Variable} must not be a temporary or build folder ('{full}'): {refusal}";
        return null;
    }

    /// <summary>The root folder (created when missing). Throws when the variable is not usable - the facts that need it are skipped before that.</summary>
    public static string Root
    {
        get
        {
            var problem = Problem();
            if (problem is not null) throw new InvalidOperationException(problem);
            var root = Environment.GetEnvironmentVariable(Variable)!.TrimEnd('/');
            Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>A fresh folder under the root. Never deleted.</summary>
    public static string New(string name)
    {
        var dir = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The guard that keeps <c>launchctl</c> (and anything else with a system-wide effect) away from every folder outside the root.</summary>
    public static void RequireInside(string folder)
    {
        if (!folder.StartsWith('/')) throw new InvalidOperationException($"'{folder}' is not an absolute path.");
        var root = Root;

        // as written (dots taken out) AND as the system reaches it (symbolic links followed): both must lie inside the root
        var links = new FileSystemLinkReader();
        var written = ProgramPaths.Lexical(folder) + "/";
        var reached = ProgramPaths.Physical(folder, links);
        var rootWritten = ProgramPaths.Lexical(root) + "/";
        var rootReached = ProgramPaths.Physical(root, links);
        if (!written.StartsWith(rootWritten, StringComparison.Ordinal)
            || reached is null || rootReached is null
            || !(reached + "/").StartsWith(rootReached + "/", StringComparison.Ordinal))
            throw new InvalidOperationException($"'{folder}' is not inside {Variable} ({root}); launchctl is only allowed for a test label whose plist lies inside it.");
    }
}

/// <summary>
/// A test that drives the real macOS services in a folder of its own (launchd, osascript, pmset, symbolic links). Runs only on a Mac AND
/// only when <c>BMB_MAC_TEST_ROOT</c> names a usable folder; otherwise it is SKIPPED, with the reason in the report - never passed, never
/// failed because of where the machine keeps its temporary files.
/// </summary>
public sealed class MacRealFactAttribute : FactAttribute
{
    public MacRealFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "needs macOS";
            return;
        }
        var problem = MacTestRoot.Problem();
        if (problem is not null) Skip = problem;
    }
}

/// <summary>A test of the Windows classes themselves. Skipped on any other system.</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "needs Windows";
    }
}

/// <summary>
/// A fresh folder per test under ONE root (<c>BMB_TEST_SCRATCH</c>, else a <c>bmb-fullD-tests</c> folder in the temp folder). These tests
/// never delete anything - not their folders, not their files - so what a run leaves is one known folder, listed, for the owner to clear.
/// </summary>
internal static class TestScratch
{
    public static string Root =>
        Environment.GetEnvironmentVariable("BMB_TEST_SCRATCH") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "bmb-fullD-tests");

    public static string New(string name)
    {
        var dir = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>Records what the adapters ask a system tool to do and answers from a script.</summary>
internal sealed class FakeCommandRunner : ICommandRunner
{
    public sealed record Call(string FileName, IReadOnlyList<string> Arguments);

    private readonly Func<Call, CommandResult> _answer;

    public FakeCommandRunner(Func<Call, CommandResult>? answer = null) => _answer = answer ?? (_ => Ok);

    public static CommandResult Ok { get; } = new(0, "", "", TimedOut: false);

    public List<Call> Calls { get; } = [];

    public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var call = new Call(fileName, arguments.ToArray());
        lock (Calls) Calls.Add(call);
        return _answer(call);
    }
}
