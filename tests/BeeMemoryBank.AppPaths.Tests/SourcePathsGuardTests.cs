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
    // EXCEPTION — feat/4-node-lifecycle:
    // The Stage 2 MainWindow.axaml.cs rescue call site moved verbatim into the new
    // NodeLifecycleService.cs during the 1:1 lifecycle extraction; same rationale, new
    // location. The reported line has shifted THREE times from code added above it in the
    // same file (feat/4-profile-switching's INodeLifecycleService interface, the
    // orchestrator's own single-flight-gate fix for the Codex Этап 4 review, and the Этап 6
    // review fix that added a default-vault guard around the call site) - this is not a
    // one-time fixup, re-check with `grep -n` every time this file changes above the call
    // site, not just once. As of the Этап 6 fix, the comment/code gap is wide enough that the
    // regex now anchors directly to the actual call-site LINE (not an explanatory comment
    // above it), since the {0,3}-newline lookahead no longer bridges the two.
    private static readonly HashSet<string> TemporaryAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // Stage 2 rescue sources — intentionally reference the legacy Velopack path as read-only source
        "desktop/BeeMemoryBank.Desktop/Services/NodeLifecycleService.cs:123",
        "desktop/BeeMemoryBank.Node/Program.cs:156",
        // Stage 3 transit guards — same rationale
        "desktop/BeeMemoryBank.Desktop/Program.cs:28",
        "server/BeeMemoryBank.Api/Services/UpdateService.cs:344",
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
