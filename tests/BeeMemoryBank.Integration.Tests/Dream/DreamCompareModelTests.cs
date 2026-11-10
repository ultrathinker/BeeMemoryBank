using System.Text.RegularExpressions;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Package C1, the "Compare trial runs" section of the Dream page. Its model (<c>wwwroot/js/pages/dream-compare-model.js</c>: which runs may be picked, how the
/// columns are lettered and shuffled so the comparison can be blind, what a cell and a row say, what each run found alone, the filters, the sample to judge by hand)
/// is run on its own by <c>JsHarness/dream-compare-harness.js</c>, and its script (<c>dream-compare.js</c>: what it asks the node, what it draws, that the models stay
/// hidden until the switch is on, that a late answer is dropped) in a fake page by <c>JsHarness/dream-compare-page-harness.js</c>. Both run under node with no
/// packages; where there is none the tests are skipped, and <c>BMB_REQUIRE_NODE=1</c> makes that a failure (CI has node). The static tests say what the files are
/// made of: Shoelace components only, the page's own classes only, no sending of their own, nothing but English.
/// </summary>
public class DreamCompareModelTests
{
    private static readonly string Pages = Path.Combine(DreamPageScripts.Web, "wwwroot", "js", "pages");
    internal static readonly string CompareModelJs = Path.Combine(Pages, "dream-compare-model.js");
    internal static readonly string CompareJs = Path.Combine(Pages, "dream-compare.js");
    internal static readonly string CompareCss = Path.Combine(DreamPageScripts.Web, "wwwroot", "css", "dream-compare.css");
    internal static readonly string CompareHarness = Path.Combine(DreamPageScripts.HarnessDir, "dream-compare-harness.js");
    internal static readonly string ComparePageHarness = Path.Combine(DreamPageScripts.HarnessDir, "dream-compare-page-harness.js");

    public static IEnumerable<object[]> ModelScenarios() => DreamPageScripts.ScenariosOf(CompareHarness, isAsync: false).Select(s => new object[] { s });

    public static IEnumerable<object[]> PageScenarios() => DreamPageScripts.ScenariosOf(ComparePageHarness, isAsync: true).Select(s => new object[] { s });

    [BusyHelperBehaviorTests.NodeTheory]
    [MemberData(nameof(ModelScenarios))]
    public void TheModelDoesWhatItSays(string scenario)
    {
        var (ok, detail) = DreamPageScripts.Run(CompareHarness, [DreamPageScripts.ModelJs, CompareModelJs], scenario);

        Assert.True(ok, detail);
    }

    [BusyHelperBehaviorTests.NodeTheory]
    [MemberData(nameof(PageScenarios))]
    public void TheSectionDoesWhatItSays(string scenario)
    {
        var (ok, detail) = DreamPageScripts.Run(ComparePageHarness, [CompareJs, DreamPageScripts.ModelJs, CompareModelJs, DreamPageScripts.Markup], scenario);

        Assert.True(ok, detail);
    }

    [Fact]
    public void TheHarnessesCoverWhatTheBriefAsksFor()
    {
        var model = DreamPageScripts.ScenariosOf(CompareHarness, isAsync: false);
        var page = DreamPageScripts.ScenariosOf(ComparePageHarness, isAsync: true);

        Assert.True(model.Count >= 20, $"only {model.Count} model scenarios");
        foreach (var needed in new[]
        {
            // columns A, B, C in an order nobody chose
            "letters_are_a_b_c", "shuffle_is_a_fair_permutation_and_leaves_the_input_alone", "reshuffle_always_gives_another_order",
            // two or three runs
            "toggling_keeps_to_three_runs_and_never_twice", "the_picker_says_what_is_left_to_do", "a_run_in_the_picker_hides_its_model_until_names_are_shown",
            // differences made obvious
            "a_disagreement_marks_the_odd_one_out_and_with_no_majority_all_of_them", "advice_from_one_run_only_names_its_letter",
            "a_run_that_did_not_look_or_failed_is_not_a_voice_in_the_vote", "each_column_counts_what_it_found_and_what_only_it_found",
            "the_filters_pick_the_rows_they_name", "the_sample_to_judge_by_hand_is_ten_disagreements_and_ten_top_scores", "the_sorts_put_the_biggest_gaps_or_the_highest_scores_first",
            // never blank
            "every_function_tolerates_a_stub_answer",
        })
            Assert.Contains(needed, model);
        foreach (var needed in new[]
        {
            "the_models_stay_hidden_until_the_switch_is_turned_on", "compare_asks_for_the_runs_in_a_shuffled_order_and_draws_the_table",
            "shuffling_the_columns_reorders_them_without_asking_again", "a_failed_comparison_says_why_and_tries_again_on_request",
            "a_stub_or_an_empty_answer_never_leaves_a_blank_page", "what_the_node_says_is_escaped", "a_late_answer_after_the_page_was_left_is_dropped",
        })
            Assert.Contains(needed, page);
    }

    // ── What the files are made of ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSection_UsesOnlyShoelaceComponents_AndOnlyItsOwnCssClasses()
    {
        var custom = new Regex(@"<([a-z][a-z0-9]*-[a-z0-9-]+)");
        foreach (var file in new[] { CompareJs, CompareModelJs })
            custom.Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value).Distinct().Where(tag => !tag.StartsWith("sl-", StringComparison.Ordinal))
                .Should().BeEmpty($"{Path.GetFileName(file)} may use Shoelace's components only");

        var css = File.ReadAllText(CompareCss);
        Regex.Matches(css, @"(?m)^(\.[\w-]+|#[\w-]+|sl-[\w-]+)").Select(m => m.Groups[1].Value).Distinct()
            .Where(s => !(s.StartsWith(".dream-") || s.StartsWith("#dream-") || s.StartsWith("@")))
            .Should().BeEmpty("dream-compare.css styles .dream-* and its own ids, and does not restyle the pages of others");
    }

    [Fact]
    public void TheSection_OnlyReads_AndAsksThroughTheCallsOfThePage()
    {
        // The repository-wide guard (BusyHelperGuardTests) scans every script; this says it for these two by name, and adds that nothing here sends at all.
        foreach (var file in new[] { CompareJs, CompareModelJs })
        {
            var text = File.ReadAllText(file);
            BusyHelperGuardTests.ScriptProblems(Path.GetFileName(file), text).Should().BeEmpty();
            text.Should().NotContain("fetch(").And.NotContain("XMLHttpRequest").And.NotContain("EventSource")
                .And.NotContain("bmbDream.api.send", "the comparison is a read");
        }
        File.ReadAllText(CompareJs).Should().Contain("dream.api.get('/compare'").And.Contain("dream.api.get('/runs'");
        Regex.Matches(File.ReadAllText(CompareJs), @"['""]/api-proxy").Count.Should().Be(0, "the address of the API is known to dream.js alone");
    }

    [Fact]
    public void TheMarkup_CarriesEveryPlaceTheSectionDrawsInto_AndLoadsItsFilesInOrder()
    {
        var markup = File.ReadAllText(DreamPageScripts.Markup);

        foreach (var id in new[]
        {
            "dream-compare-section", "dream-cmp-reveal", "dream-cmp-refresh", "dream-cmp-picker", "dream-cmp-go", "dream-cmp-latest", "dream-cmp-hint",
            "dream-cmp-tools", "dream-cmp-filter", "dream-cmp-sort", "dream-cmp-shuffle", "dream-cmp-result", "dream-cmp-summary", "dream-cmp-rows",
        })
            markup.Should().Contain($"id=\"{id}\"", $"the script draws into #{id}");
        markup.Should().Contain("<sl-switch id=\"dream-cmp-reveal\"", "the names of the models are shown by a switch");
        markup.Should().Contain("Compare trial runs");
        markup.Should().Contain("href=\"/css/dream-compare.css\"");

        int At(string name) => markup.IndexOf(name, StringComparison.Ordinal);
        At("/js/pages/dream-model.js").Should().BeLessThan(At("/js/pages/dream.js"));
        At("/js/pages/dream.js\"").Should().BeLessThan(At("/js/pages/dream-compare-model.js"), "the section needs bmbDream, which dream.js makes");
        At("/js/pages/dream-compare-model.js").Should().BeLessThan(At("/js/pages/dream-compare.js\""), "the script needs its model");
        At("id=\"dream-history-section\"").Should().BeLessThan(At("id=\"dream-compare-section\""), "the comparison is the last section, under the history (plan 3.9, point 6)");
    }

    [Fact]
    public void NoFileOfThePackage_HoldsACyrillicLetter()
    {
        var files = new[]
        {
            DreamPageScripts.Markup, CompareJs, CompareModelJs, CompareCss, CompareHarness, ComparePageHarness,
        }.Concat(Directory.EnumerateFiles(Path.Combine(DreamPageScripts.RepoRoot, "tests", "BeeMemoryBank.Integration.Tests", "Dream"), "DreamCompare*.cs"))
         .Concat(Directory.EnumerateFiles(Path.Combine(DreamPageScripts.RepoRoot, "tests", "BeeMemoryBank.Integration.Tests", "Dream"), "DreamSampler*.cs"))
         .Concat(Directory.EnumerateFiles(Path.Combine(DreamPageScripts.RepoRoot, "server", "BeeMemoryBank.Api", "Dream"), "DreamCompare*.cs", SearchOption.AllDirectories))
         .Concat(Directory.EnumerateFiles(Path.Combine(DreamPageScripts.RepoRoot, "server", "BeeMemoryBank.Api", "Dream"), "DreamSampler.cs", SearchOption.AllDirectories))
         .Append(Path.Combine(DreamPageScripts.RepoRoot, "server", "BeeMemoryBank.Api", "Endpoints", "DreamEndpoints.Compare.cs"));

        foreach (var file in files)
            Regex.IsMatch(File.ReadAllText(file), @"\p{IsCyrillic}").Should().BeFalse($"{Path.GetFileName(file)} is part of a public repository, in English");
    }
}
