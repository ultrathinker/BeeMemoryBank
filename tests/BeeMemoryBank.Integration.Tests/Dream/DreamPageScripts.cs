using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>Where the files of the Dream page are, and how the node harnesses are run (shared by the model, script and page tests of package U1).</summary>
internal static class DreamPageScripts
{
    internal static readonly string RepoRoot = FindRepoRoot();
    internal static readonly string Web = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web");
    internal static readonly string ModelJs = Path.Combine(Web, "wwwroot", "js", "pages", "dream-model.js");
    internal static readonly string PageJs = Path.Combine(Web, "wwwroot", "js", "pages", "dream.js");
    internal static readonly string Css = Path.Combine(Web, "wwwroot", "css", "dream.css");
    internal static readonly string Markup = Path.Combine(Web, "Pages", "Dream.cshtml");
    internal static readonly string PageModel = Path.Combine(Web, "Pages", "Dream.cshtml.cs");
    internal static readonly string HarnessDir = Path.Combine(RepoRoot, "tests", "BeeMemoryBank.Integration.Tests", "JsHarness");
    internal static readonly string ModelHarness = Path.Combine(HarnessDir, "dream-model-harness.js");
    internal static readonly string PageHarness = Path.Combine(HarnessDir, "dream-page-harness.js");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")) || File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    /// <summary>The scenarios a harness defines, in the order of its file: <c>name() {</c> for the model, <c>async name() {</c> for the page.</summary>
    internal static List<string> ScenariosOf(string harness, bool isAsync) =>
        Regex.Matches(File.ReadAllText(harness), isAsync ? @"^    async ([a-z0-9_]+)\(\) \{" : @"^    ([a-z0-9_]+)\(\) \{", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();

    /// <summary>Runs one scenario of a harness with node, and returns what it said.</summary>
    internal static (bool Ok, string Detail) Run(string harness, IEnumerable<string> args, string scenario)
    {
        var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(harness);
        foreach (var a in args) info.ArgumentList.Add(a);
        info.ArgumentList.Add(scenario);
        using var p = Process.Start(info) ?? throw new InvalidOperationException("node did not start");
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); throw new TimeoutException($"the scenario {scenario} did not end"); }
        var line = output.Result.Trim().Split('\n').LastOrDefault() ?? "";
        if (!line.StartsWith('{')) throw new InvalidOperationException($"the harness said: '{output.Result}' / '{error.Result}'");
        var result = JsonSerializer.Deserialize<JsonElement>(line);
        return (result.GetProperty("ok").GetBoolean(), result.GetProperty("detail").GetString() ?? "");
    }
}
