using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What the scripts of the Admin area DO, run on their own by <c>JsHarness/admin-pages-harness.js</c> (node, no packages) in a small fake page. One script
/// (<c>admin.js</c>) serves every page of the area and the pages have only some of its elements each, so a page must do only its own part and a page with none of them
/// must do nothing and raise no error; the index script (<c>admin-index.js</c>) moves the bookmarks of the old single page to the pages of their blocks, and only there.
/// The tests need node; where there is none they are skipped, and <c>BMB_REQUIRE_NODE=1</c> makes that a failure (CI has node).
/// </summary>
public class AdminPagesScriptTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Pages = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web", "wwwroot", "js", "pages");
    private static readonly string Harness = Path.Combine(RepoRoot, "tests", "BeeMemoryBank.Integration.Tests", "JsHarness", "admin-pages-harness.js");

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

    private static string Run(string script, string scenario)
    {
        var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(Harness);
        info.ArgumentList.Add(script);
        info.ArgumentList.Add(scenario);
        using var p = Process.Start(info) ?? throw new InvalidOperationException("node did not start");
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); throw new TimeoutException($"the scenario {scenario} did not end"); }
        return output.Result.Trim().Split('\n').LastOrDefault() is { } line && line.StartsWith('{') ? line : throw new InvalidOperationException($"the harness said: '{output.Result}' / '{error.Result}'");
    }

    private static (bool Ok, string Detail) RunScenario(string script, string scenario)
    {
        var result = JsonSerializer.Deserialize<JsonElement>(Run(script, scenario));
        return (result.GetProperty("ok").GetBoolean(), result.GetProperty("detail").GetString() ?? "");
    }

    [BusyHelperBehaviorTests.NodeTheory]
    [InlineData("a_page_without_any_of_its_elements_does_nothing")]
    [InlineData("the_branding_page_asks_for_the_branding_and_nothing_else")]
    [InlineData("the_web_session_page_asks_for_the_session_settings_and_nothing_else")]
    [InlineData("the_search_page_asks_for_the_embeddings_switch_and_nothing_else")]
    [InlineData("the_ai_page_asks_for_its_keys_models_defaults_and_switch")]
    [InlineData("the_snapshots_page_posts_to_its_own_handlers")]
    [InlineData("the_upload_goes_to_the_snapshots_page")]
    public void TheScriptOfThePagesDoesOnlyThePartOfThePageItIsOn(string scenario)
    {
        var (ok, detail) = RunScenario(Path.Combine(Pages, "admin.js"), scenario);

        Assert.True(ok, detail);
    }

    [BusyHelperBehaviorTests.NodeTheory]
    [InlineData("the_index_moves_a_known_fragment_to_its_page")]
    [InlineData("the_index_ignores_what_it_does_not_know")]
    [InlineData("the_index_follows_nothing_but_a_page_of_the_area")]
    [InlineData("the_index_without_its_table_or_with_a_damaged_one_does_nothing")]
    public void TheScriptOfTheIndexMovesOldBookmarksAndNothingElse(string scenario)
    {
        var (ok, detail) = RunScenario(Path.Combine(Pages, "admin-index.js"), scenario);

        Assert.True(ok, detail);
    }

    [Fact]
    public void EveryScenarioOfTheHarnessIsRunByATest()
    {
        var source = File.ReadAllText(Harness);
        var defined = System.Text.RegularExpressions.Regex.Matches(source, @"^    async (\w+)\(\)", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var tested = typeof(AdminPagesScriptTests).GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>())
            .Select(a => (string)a.GetData(null!).First()[0]).ToList();

        defined.Should().NotBeEmpty();
        tested.Should().BeEquivalentTo(defined, "a scenario nobody runs checks nothing");
    }
}
