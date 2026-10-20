using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-322: what the model of the network map (<c>wwwroot/js/pages/network-model.js</c>) DOES, run on its own by <c>JsHarness/network-model-harness.js</c>
/// (node, no packages): where the nodes stand (one in the middle, two on a line, three a triangle, more on a circle), what each is drawn as, how the calls
/// group into one-way and two-way connections, and what the cards say. The drawing itself (network-map.js) is checked in a browser on the stand. The tests need
/// node; where there is none they are skipped, and <c>BMB_REQUIRE_NODE=1</c> makes that a failure (CI has node).
/// </summary>
public class NetworkMapModelTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Model = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web", "wwwroot", "js", "pages", "network-model.js");
    private static readonly string Harness = Path.Combine(RepoRoot, "tests", "BeeMemoryBank.Integration.Tests", "JsHarness", "network-model-harness.js");

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

    private static (bool Ok, string Detail) RunScenario(string scenario)
    {
        var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(Harness);
        info.ArgumentList.Add(Model);
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

    [BusyHelperBehaviorTests.NodeTheory]
    // where the nodes stand
    [InlineData("one_node_is_in_the_middle")]
    [InlineData("two_nodes_are_on_one_line")]
    [InlineData("three_nodes_are_a_triangle_with_a_corner_up")]
    [InlineData("four_and_more_stand_on_a_circle")]
    [InlineData("a_tall_narrow_room_makes_an_ellipse_that_still_fits")]
    // what a node is drawn as
    [InlineData("every_platform_has_its_drawing_and_word")]
    [InlineData("a_node_that_said_nothing_is_drawn_from_what_is_known")]
    [InlineData("docker_and_blind_are_badges_and_in_the_caption")]
    [InlineData("a_platform_word_that_is_not_known_never_becomes_a_label")]
    [InlineData("the_ring_of_a_node_says_how_it_stands")]
    // who calls whom
    [InlineData("two_calls_between_a_pair_are_one_two_way_connection")]
    [InlineData("the_colour_of_a_call_follows_what_is_known_of_it")]
    // the words
    [InlineData("times_are_told_as_a_person_says_them")]
    [InlineData("an_address_is_told_as_scheme_host_and_port")]
    [InlineData("the_line_of_a_node_says_how_it_stands")]
    [InlineData("what_this_node_knows_of_the_notes_on_a_node")]
    [InlineData("the_card_of_a_node_has_its_address_version_and_where_it_called_from")]
    [InlineData("a_node_without_an_address_says_it_calls_in")]
    [InlineData("the_card_of_a_call_says_where_it_goes_and_what_is_known")]
    [InlineData("a_connection_is_told_in_one_line")]
    public void TheModelDoesWhatItSays(string scenario)
    {
        var (ok, detail) = RunScenario(scenario);

        Assert.True(ok, detail);
    }

    [Fact]
    public void EveryScenarioOfTheHarnessIsRunByATest()
    {
        var harness = File.ReadAllText(Harness);
        var defined = Regex.Matches(harness, @"^    (\w+)\(\) \{", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        var tests = File.ReadAllText(Path.Combine(RepoRoot, "tests", "BeeMemoryBank.Integration.Tests", "NetworkMapModelTests.cs"));
        var run = Regex.Matches(tests, @"InlineData\(""(\w+)""\)").Select(m => m.Groups[1].Value).ToHashSet();

        defined.Should().NotBeEmpty();
        defined.Except(run).Should().BeEmpty("a scenario nobody runs checks nothing");
        run.Except(defined).Should().BeEmpty("a test that names a scenario the harness does not have fails only when node is there");
    }
}
