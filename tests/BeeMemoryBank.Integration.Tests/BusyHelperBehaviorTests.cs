using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-311 fix round (F2, F3, F5, F6, F13, F14): what the shared busy helper of the Web UI (<c>wwwroot/js/bmb-busy.js</c>) DOES, run in a small fake page by
/// <c>JsHarness/bmb-busy-harness.js</c> (node, no packages, timers that only move when the test says so). A source scan cannot tell that a lock is let go
/// after thirty minutes; running the file with the clock moved can. The tests need node; where there is none they are skipped, and
/// <c>BMB_REQUIRE_NODE=1</c> makes that a failure (CI has node).
/// </summary>
public class BusyHelperBehaviorTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Helper = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web", "wwwroot", "js", "bmb-busy.js");
    private static readonly string Harness = Path.Combine(RepoRoot, "tests", "BeeMemoryBank.Integration.Tests", "JsHarness", "bmb-busy-harness.js");

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

    private static readonly Lazy<bool> HasNode = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("node", "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            return p is not null && p.WaitForExit(15000) && p.ExitCode == 0;
        }
        catch { return false; }
    });

    /// <summary>A fact that needs node: skipped where there is none, a failure when <c>BMB_REQUIRE_NODE</c> is set.</summary>
    public sealed class NodeTheoryAttribute : TheoryAttribute
    {
        public NodeTheoryAttribute()
        {
            if (!HasNode.Value && Environment.GetEnvironmentVariable("BMB_REQUIRE_NODE") is not "1")
                Skip = "node is not installed here (the behavior of bmb-busy.js is run with it); set BMB_REQUIRE_NODE=1 to make this fail";
        }
    }

    internal static (bool Ok, string Detail) RunScenario(string scriptPath, string scenario)
    {
        var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(Harness);
        info.ArgumentList.Add(scriptPath);
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

    [NodeTheory]
    // F3: the time limit says "still running" and never gives the control back
    [InlineData("long_work_keeps_the_lock_after_the_notice_time")]
    [InlineData("short_work_says_it_is_still_running_and_keeps_the_lock")]
    [InlineData("a_form_stays_locked_after_two_minutes")]
    // F6: the reservation of a form is made in the task of the first submit
    [InlineData("two_submits_in_one_task_send_once")]
    [InlineData("a_declined_confirmation_ends_the_reservation")]
    // F5: what the node said reaches the person
    [InlineData("errors_carry_what_the_node_said")]
    // BMB-311 fix2: R6 (the whole text of a long error), R7 (the first submit event is seen busy)
    [InlineData("a_long_error_keeps_its_whole_text_in_an_area_that_opens")]
    [InlineData("the_first_submit_event_is_seen_busy_at_once_and_a_declined_one_is_undone")]
    // BMB-313 Q3: the header badge of the sync status and the check of a node
    [InlineData("the_header_badge_follows_the_status_when_the_page_loads")]
    [InlineData("a_check_that_is_rejected_gives_the_node_row_back")]
    // BMB-317: two lines a node, the rest under a chevron
    [InlineData("the_sync_dropdown_is_two_lines_with_the_rest_under_a_chevron")]
    [InlineData("the_chevron_opens_the_details_and_a_check_opens_a_closed_row")]
    // BMB-316: the password dialog of the save of a protected article
    [InlineData("the_save_of_a_protected_article_tells_a_missing_password_from_other_failures")]
    [InlineData("the_password_dialog_saves_with_what_is_typed_and_stays_open_when_it_fails")]
    [InlineData("the_unlock_strip_over_the_editor_counts_down_and_says_when_the_time_is_out")]
    // BMB-319: the time one article stays unlocked for, chosen on a slider
    [InlineData("the_strip_says_hours_for_a_long_time_and_closes_the_slider_when_the_time_is_out")]
    [InlineData("the_unlock_time_of_one_article_is_chosen_on_a_slider_that_applies_when_it_is_let_go")]
    // F13: a page that is replaced while its operation runs
    [InlineData("an_operation_of_a_replaced_page_closes_its_popup_and_says_its_result_as_a_notice")]
    [InlineData("an_operation_of_a_control_that_stays_keeps_its_popup_through_a_navigation")]
    // F14: the dialog has a name, a description, and gives the focus back
    [InlineData("the_dialog_is_named_described_and_gives_the_focus_back")]
    [InlineData("two_long_operations_share_one_popup")]
    public void TheHelperDoes(string scenario)
    {
        var (ok, detail) = RunScenario(Helper, scenario);

        Assert.True(ok, detail);
    }

    [NodeTheory]
    [InlineData("seal_form_shows_starting_only_when_it_is_sent")]
    public void TheEncryptionPage_DoesNotSayItIsStartingWhenTheQuestionWasDeclined(string scenario)
    {
        // BMB-305 fix round, B2: the form asks first (site.js, capture phase) and the page's own handler ran anyway and put up "Starting...".
        var (ok, detail) = RunScenario(Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web", "wwwroot", "js", "pages", "vault-encryption.js"), scenario);

        Assert.True(ok, detail);
    }

    [Fact]
    public void ThereIsOneOwnerOfTheBusyStateOfAForm_AndTheLegacyBlockOfSiteJsIsGone()
    {
        // F2: bmb-busy.js loads first, then site.js; a second implementation in site.js took window.bmbSetFormBusy over, added a second capture-phase submit
        // listener and let the form go after two minutes. One owner: nothing else under wwwroot/js touches the busy state of a form.
        var js = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web", "wwwroot", "js");
        foreach (var file in Directory.EnumerateFiles(js, "*.js", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "bmb-busy.js"))
        {
            var text = File.ReadAllText(file);
            Assert.False(Regex.IsMatch(text, @"window\.bmbSetFormBusy\s*="), $"{Path.GetRelativePath(RepoRoot, file)} assigns window.bmbSetFormBusy: bmb-busy.js is the one owner");
            Assert.False(text.Contains("busyNow", StringComparison.Ordinal), $"{Path.GetRelativePath(RepoRoot, file)} keeps its own busy state of a form (data-busy-now)");
            Assert.False(Regex.IsMatch(text, @"matches\(\s*'form\[data-busy\]'\s*\)"), $"{Path.GetRelativePath(RepoRoot, file)} listens for the submit of data-busy forms: bmb-busy.js does");
        }
    }

    [Fact]
    public void TheHelperHasNoTimerThatGivesTheControlBack()
    {
        // The source side of F3, for the machines without node: the only timers of the helper change what the page says; none of them calls release/reset.
        var text = File.ReadAllText(Helper);

        Assert.DoesNotContain("LONG_LIMIT_MS", text);
        Assert.DoesNotContain("BUSY_LIMIT_MS", text);
        Assert.DoesNotContain("setTimeout(release", text);
        Assert.Contains("do not start it again", text);
    }
}
