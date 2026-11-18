using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What the script of the Users page DOES, run on its own by <c>JsHarness/users-page-harness.js</c> (node, no packages) in a small fake page (release 2.12, F5): the Edit
/// dialog's role choice never crosses the superadmin line in either direction, "Make superadmin..." asks first and says what the person gets, and "Remove superadmin rights..."
/// cannot be confirmed until "what may they see afterwards?" has an answer. The tests need node; where there is none they are skipped, and <c>BMB_REQUIRE_NODE=1</c> makes
/// that a failure (CI has node).
/// </summary>
public class UsersPageScriptTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Script = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web", "wwwroot", "js", "pages", "users.js");
    private static readonly string Harness = Path.Combine(RepoRoot, "tests", "BeeMemoryBank.Integration.Tests", "JsHarness", "users-page-harness.js");

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
        info.ArgumentList.Add(Script);
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
    [InlineData("the_edit_dialog_of_a_superadmin_shows_the_role_and_sends_no_role")]
    [InlineData("the_edit_dialog_of_an_ordinary_user_offers_no_superadmin")]
    [InlineData("a_superadmin_picked_in_the_edit_dialog_is_not_sent")]
    [InlineData("choosing_superadmin_in_the_add_dialog_shows_what_it_is")]
    public void TheEditDialogDoesNotMoveAnybodyAcrossTheSuperadminLine(string scenario)
    {
        var (ok, detail) = RunScenario(scenario);

        Assert.True(ok, detail);
    }

    [BusyHelperBehaviorTests.NodeTheory]
    [InlineData("make_superadmin_asks_first_and_then_puts_the_role")]
    [InlineData("make_superadmin_that_is_refused_says_so_in_the_dialog_and_does_not_reload")]
    public void MakeSuperadminAsksFirst(string scenario)
    {
        var (ok, detail) = RunScenario(scenario);

        Assert.True(ok, detail);
    }

    [BusyHelperBehaviorTests.NodeTheory]
    [InlineData("remove_cannot_be_confirmed_before_a_choice_is_made")]
    [InlineData("remove_with_folders_needs_a_folder_and_sends_the_rules")]
    [InlineData("remove_with_a_closed_role_sends_that_role_and_offers_only_closed_roles")]
    [InlineData("remove_with_the_open_role_needs_the_tick_and_says_so_to_the_api")]
    [InlineData("opening_the_dialog_again_forgets_the_last_choice")]
    [InlineData("a_removal_that_is_refused_says_so_in_the_dialog_and_does_not_reload")]
    [InlineData("a_removal_that_goes_through_tells_the_page")]
    [InlineData("a_deletion_needs_no_access_choice_and_sends_a_delete_with_the_password")]
    public void RemoveSuperadminRightsCannotBeConfirmedBeforeTheAccessChoiceIsComplete(string scenario)
    {
        var (ok, detail) = RunScenario(scenario);

        Assert.True(ok, detail);
    }

    [Fact]
    public void EveryScenarioOfTheHarnessIsRunByATest()
    {
        var source = File.ReadAllText(Harness);
        var defined = System.Text.RegularExpressions.Regex.Matches(source, @"^    async (\w+)\(\)", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var tested = typeof(UsersPageScriptTests).GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>())
            .Select(a => (string)a.GetData(null!).First()[0]).ToList();

        defined.Should().NotBeEmpty();
        tested.Should().BeEquivalentTo(defined, "a scenario nobody runs checks nothing");
    }
}
