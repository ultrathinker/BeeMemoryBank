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
    //
    // EXCEPTION — feat/2-legacy-rescue:
    // The two entries below are the rescue call sites that intentionally reference
    // AppContext.BaseDirectory + "data" as the LEGACY SOURCE for a one-time rescue migration.
    // This is the opposite of the anti-pattern: they read from the old broken location in order
    // to COPY data away from it, never to write mutable user data there. They are not
    // regressions and must remain in the allowlist for the lifetime of the rescue feature.
    // EXCEPTION — feat/3-transit-guards:
    // Two more legitimate sites, same read-only-legacy-source pattern as Stage 2 above:
    // the VelopackApp post-update hook re-runs rescue as a belt-and-suspenders safety net,
    // and UpdateService's pre-apply guard reads (never writes) the legacy path to refuse
    // applying an update that would still wipe it.
    //
    // The allow-list counts matches PER FILE instead of pinning file:line: unrelated edits above
    // a call site kept shifting its line and failing this test. A NEW match in one of these
    // files still fails, because the count goes over.
    private static readonly Dictionary<string, int> AllowedPerFile = new(StringComparer.OrdinalIgnoreCase)
    {
        // Stage 2 rescue sources: read the legacy Velopack path as a read-only source
        ["desktop/BeeMemoryBank.Desktop/Services/NodeLifecycleService.cs"] = 1,
        ["desktop/BeeMemoryBank.Node/Program.cs"] = 1,
        // Stage 3 transit guards: same rationale
        ["desktop/BeeMemoryBank.Desktop/Program.cs"] = 1,
        ["server/BeeMemoryBank.Api/Services/UpdateService.cs"] = 1,
    };

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
        var hits = new List<(string Path, int Line)>();

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
                hits.Add((relativePath, lineNumber));
            }
        }

        foreach (var group in hits.GroupBy(h => h.Path, StringComparer.OrdinalIgnoreCase))
        {
            AllowedPerFile.TryGetValue(group.Key, out var allowed);
            if (group.Count() > allowed)
                offenders.AddRange(group.Select(h => $"{h.Path}:{h.Line} (allowed in this file: {allowed})"));
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
