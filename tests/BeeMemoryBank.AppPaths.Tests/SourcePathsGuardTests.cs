using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// Guard test: Ensures that AppContext.BaseDirectory is not used to resolve default/hardcoded
/// paths to mutable user data (like "data" directory) directly next to the installation path.
/// LEGITIMATE usage (like wwwroot, model.onnx, AutoDiscovery) is permitted.
/// </summary>
public class SourcePathsGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly Regex PathGuardRegex = new Regex(
        @"(?s)(?:AppContext\.BaseDirectory(?:[^\n]*\n){0,3}[^\n]*?""data"")|(?:""data""(?:[^\n]*\n){0,3}[^\n]*?AppContext\.BaseDirectory)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // feat/1-desktop-paths and feat/1-node-default-path (the four known pre-existing sites) are
    // merged - no allowlist needed anymore. A newly-added entry here should be treated as a real
    // regression, not silenced.
    private static readonly HashSet<string> TemporaryAllowlist = new(StringComparer.OrdinalIgnoreCase);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> GetSourceFiles()
    {
        var searchFolders = new[] { "desktop", "server", "libs" };
        foreach (var folder in searchFolders)
        {
            var folderPath = Path.Combine(RepoRoot, folder);
            if (!Directory.Exists(folderPath))
                continue;

            foreach (var file in Directory.EnumerateFiles(folderPath, "*.cs", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(file);
                if (full.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    full.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;

                yield return file;
            }
        }
    }

    private static int GetLineNumber(string content, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    [Fact]
    public void AppContextBaseDirectory_MustNotBeUsedWithDataDirectory_ForMutableData()
    {
        var offenders = new List<string>();

        foreach (var file in GetSourceFiles())
        {
            var content = File.ReadAllText(file);
            var matches = PathGuardRegex.Matches(content);

            foreach (Match match in matches)
            {
                int appDirIndex = content.IndexOf("AppContext.BaseDirectory", match.Index, match.Length);
                if (appDirIndex == -1)
                {
                    appDirIndex = match.Index; // fallback if case or whitespace differs
                }

                int lineNumber = GetLineNumber(content, appDirIndex);
                var relativePath = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
                var entry = $"{relativePath}:{lineNumber}";

                if (!TemporaryAllowlist.Contains(entry))
                {
                    offenders.Add(entry);
                }
            }
        }

        if (offenders.Count > 0)
        {
            Assert.Fail(
                "Source Guard Violation: Found AppContext.BaseDirectory in combination with string literal \"data\" " +
                "(hardcoded/default path to mutable user data next to BaseDirectory). " +
                "Mutable user data must be resolved via BmbPaths to stable app data locations.\n" +
                "Offenders:\n" + string.Join("\n", offenders.Select(o => $"  - {o}")));
        }
    }
}
