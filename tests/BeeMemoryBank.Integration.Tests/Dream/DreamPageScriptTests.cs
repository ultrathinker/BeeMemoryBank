using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Package U1, the Dream page: what its script (<c>wwwroot/js/pages/dream.js</c>) DOES, run in a fake page by <c>JsHarness/dream-page-harness.js</c> (node, no
/// packages): a small DOM that records what is written into it, timers that move when the test says so, and a scripted <c>fetch</c> that answers like the API,
/// like a stub, or with an error. Nobody can look at the page before the owner does, so this is where it is shown that the page asks the node for the right things
/// (every request of every scenario is checked against the API contract of plan 3.8), that advice appears one by one, that every button sends what it should,
/// that what a model wrote is drawn as text, and that no part of the page is ever left blank when an endpoint answers with a stub or an error.
/// The drawing itself (colours, spacing) is not checked here. The tests need node; where there is none they are skipped, and <c>BMB_REQUIRE_NODE=1</c> makes that a failure.
/// </summary>
public class DreamPageScriptTests
{
    public static IEnumerable<object[]> Scenarios() => DreamPageScripts.ScenariosOf(DreamPageScripts.PageHarness, isAsync: true).Select(s => new object[] { s });

    [BusyHelperBehaviorTests.NodeTheory]
    [MemberData(nameof(Scenarios))]
    public void ThePageDoesWhatItSays(string scenario)
    {
        var (ok, detail) = DreamPageScripts.Run(
            DreamPageScripts.PageHarness, [DreamPageScripts.PageJs, DreamPageScripts.ModelJs, DreamPageScripts.Markup], scenario);

        Assert.True(ok, detail);
    }

    [Fact]
    public void TheHarnessCoversTheBehavioursTheBriefAsksFor()
    {
        var names = DreamPageScripts.ScenariosOf(DreamPageScripts.PageHarness, isAsync: true);

        foreach (var needed in new[]
        {
            // never a blank screen
            "a_stub_answer_never_leaves_a_part_blank", "a_node_that_fails_is_said_in_every_part", "a_node_that_does_not_answer_is_said_too", "retry_asks_again_and_the_part_comes_back",
            // advice one by one during a run
            "a_run_in_progress_is_followed_and_its_advice_comes_one_card_at_a_time", "a_run_that_is_lost_is_not_followed_for_ever",
            // the buttons
            "start_sends_the_form_and_follows_the_new_run", "stop_posts_to_the_run_that_is_going", "apply_asks_first_and_then_changes_the_note",
            "dismiss_sends_the_reach_the_reason_and_the_time", "the_two_buttons_of_the_hand_off_are_placeholders_that_can_be_replaced",
            // history, text safety
            "a_history_row_opens_and_asks_for_its_advice_and_its_log", "markup_in_what_a_model_wrote_is_drawn_as_text",
            // leaving the page by the single-page navigation
            "leaving_the_page_stops_every_timer_and_listener_and_ignores_what_is_still_on_its_way",
            // package ux2a: two switches, the rule book, the cards, advice closed elsewhere, the tab that comes back, a link to one advice
            "the_rules_and_the_model_are_two_switches_and_start_needs_one", "a_model_that_is_not_set_up_is_a_disabled_switch_with_the_reason_and_the_links",
            "view_all_rules_opens_the_book_of_the_node_and_its_link_in_the_details_does_too",
            "a_card_says_what_each_object_is_who_found_it_and_its_links_open_a_new_tab",
            "every_button_on_an_advice_closed_elsewhere_says_what_happened_and_redraws_the_card",
            "the_tab_that_comes_back_reads_the_page_again_in_place_at_most_every_few_seconds",
            "an_open_history_row_reads_its_advice_again_when_the_tab_comes_back_card_by_card_without_a_blink", "a_row_that_was_closed_is_not_asked_for_its_advice_again",
            "the_state_card_takes_the_last_run_from_the_state_when_the_tab_comes_back",
            "a_link_to_one_advice_shows_it_on_top_whatever_its_status_with_a_way_back", "a_link_to_an_advice_dream_does_not_know_gives_a_short_notice",
        })
            Assert.Contains(needed, names);
    }
}
