using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What the single-page navigation (<c>wwwroot/js/site-spa.js</c>) DECIDES, run on its own by <c>JsHarness/site-spa-harness.js</c> in a small fake browser (node, no
/// packages): the pages of the Admin area (<c>/Admin</c> and <c>/Admin/&lt;Name&gt;</c>) are swapped in from the list and from each other, the template's Back goes
/// back by a swap, the real <c>admin.js</c> runs on a swapped page and its timers and listeners go with the page, and what is not a page of the area (a download, a
/// look-alike path, another site) stays the browser's. The tests need node; where there is none they are skipped, and <c>BMB_REQUIRE_NODE=1</c> makes that a failure.
/// </summary>
public class SiteSpaNavigationTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Web = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web");
    private static readonly string Spa = Path.Combine(Web, "wwwroot", "js", "site-spa.js");
    private static readonly string Harness = Path.Combine(RepoRoot, "tests", "BeeMemoryBank.Integration.Tests", "JsHarness", "site-spa-harness.js");

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

    private static string Run(string scenario)
    {
        var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(Harness);
        info.ArgumentList.Add(Spa);
        info.ArgumentList.Add(scenario);
        using var p = Process.Start(info) ?? throw new InvalidOperationException("node did not start");
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); throw new TimeoutException($"the scenario {scenario} did not end"); }
        return output.Result.Trim().Split('\n').LastOrDefault() is { } line && line.StartsWith('{') ? line : throw new InvalidOperationException($"the harness said: '{output.Result}' / '{error.Result}'");
    }

    [BusyHelperBehaviorTests.NodeTheory]
    // the Admin area is swapped, in both directions, and its scripts run
    [InlineData("the_list_to_a_page_is_swapped_and_the_script_of_the_page_runs")]
    [InlineData("the_back_link_of_the_template_goes_back_to_the_list")]
    [InlineData("a_page_to_a_page_and_to_another_area_of_the_site_are_swapped")]
    [InlineData("the_case_of_the_address_and_a_trailing_slash_do_not_matter")]
    [InlineData("the_back_and_forward_buttons_swap_the_pages_of_the_area_and_reload_for_the_others")]
    [InlineData("the_entry_of_a_page_of_the_area_is_marked_as_a_swapped_page")]
    // what is not a page of the area stays the browser's
    [InlineData("a_handler_link_of_the_area_is_left_to_the_browser")]
    [InlineData("an_unrelated_or_malformed_path_is_not_swapped")]
    [InlineData("the_existing_exclusions_still_hold")]
    [InlineData("a_page_the_node_does_not_have_falls_back_to_a_full_navigation")]
    // what the page that is left takes with it
    [InlineData("the_timers_of_a_page_that_is_left_die_with_it")]
    [InlineData("the_script_of_the_page_hears_its_own_clicks_only_while_the_page_is_there")]
    // what a page with requests of its own leaves running when it is left: Hard Delete (its script is run for real, against fake elements and requests)
    [InlineData("a_hard_delete_list_that_fails_after_the_page_was_left_writes_nothing_to_the_new_page")]
    [InlineData("a_hard_delete_list_that_arrives_after_the_page_was_left_writes_nothing")]
    [InlineData("the_hard_delete_page_still_loads_draws_and_reports_while_it_is_there")]
    [InlineData("an_audit_log_that_arrives_after_the_page_was_left_writes_nothing")]
    [InlineData("the_typing_delay_of_the_filter_dies_with_the_page")]
    [InlineData("a_hard_delete_that_ends_after_the_page_was_left_is_not_taken_back_and_says_so_once")]
    // the rows admin.js builds as strings for AI / Chat
    [InlineData("the_rows_of_the_ai_chat_tables_carry_the_classes_and_labels_of_the_stacked_layout")]
    [InlineData("the_empty_and_the_failed_states_of_the_ai_chat_tables_are_sentences_that_wrap")]
    public void TheNavigationDoesWhatItSays(string scenario)
    {
        var result = JsonSerializer.Deserialize<JsonElement>(Run(scenario));

        Assert.True(result.GetProperty("ok").GetBoolean(), result.GetProperty("detail").GetString());
    }

    [Fact]
    public void EveryScenarioOfTheHarnessIsRunByATest()
    {
        var defined = Regex.Matches(File.ReadAllText(Harness), @"^    async (\w+)\(\)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var tested = typeof(SiteSpaNavigationTests).GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>())
            .Select(a => (string)a.GetData(null!).First()[0]).ToList();

        defined.Should().NotBeEmpty();
        tested.Should().BeEquivalentTo(defined, "a scenario nobody runs checks nothing");
    }

    /// <summary>The swap takes the content of <c>#page-content</c>, the title and the scripts after site.js; it never loads a page's styles. So no page of the area may need any.</summary>
    [Fact]
    public void ThePagesOfTheAdminArea_NeedNothingThatASwapDoesNotBring()
    {
        var pages = Path.Combine(Web, "Pages", "Admin");
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(pages, "*.cshtml"))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            if (text.Contains("@section HeadStyles", StringComparison.Ordinal)) problems.Add($"{name}: a HeadStyles section is in the head, which a swap does not replace");
            if (Regex.IsMatch(text, @"<link\b[^>]*rel=""stylesheet""", RegexOptions.IgnoreCase)) problems.Add($"{name}: a stylesheet link");
            // the only scripts of the area are the ones its layout/index puts in the Scripts section (after site.js), which the swap executes
            if (name != "_AdminPage.cshtml" && name != "Index.cshtml" && Regex.IsMatch(text, @"<script\b", RegexOptions.IgnoreCase)) problems.Add($"{name}: a script of its own");
            if (name != "_AdminPage.cshtml" && name != "Index.cshtml" && text.Contains("@section Scripts", StringComparison.Ordinal)) problems.Add($"{name}: its own Scripts section (admin.js is the template's)");
        }
        problems.Should().BeEmpty();
        File.ReadAllText(Path.Combine(pages, "_AdminPage.cshtml")).Should().Contain("/js/pages/admin.js").And.Contain("RenderSectionAsync(\"Scripts\"");
    }

    [Fact]
    public void TheSwapKeepsEveryExclusionItHad()
    {
        var js = File.ReadAllText(Spa);

        js.Should().Contain("e.ctrlKey || e.metaKey || e.shiftKey || e.altKey").And.Contain("url.origin !== window.location.origin")
            .And.Contain("getAttribute('target')").And.Contain("href.startsWith('#')");
        js.Should().Contain("lower === '/folder'").And.Contain("lower === '/article/view'").And.Contain("lower === '/search'").And.Contain("lower === '/activity'");
        js.Should().Contain("/^\\/admin\\/[a-z]+$/", "the area is /admin/<letters>, nothing deeper");
        js.Should().Contain("isSpaLink(url)", "the click, the popstate and spaNavigate ask about the whole address (the handler query), not the path alone");
    }
}
