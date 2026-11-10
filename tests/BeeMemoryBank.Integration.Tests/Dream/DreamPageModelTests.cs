using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Package U1, the Dream page: what its model (<c>wwwroot/js/pages/dream-model.js</c>) DOES, run on its own by <c>JsHarness/dream-model-harness.js</c> (node, no
/// packages): the words of a kind, an outcome and a phase, the numbers of a run, what the state card says, which buttons a card of advice has, what the Apply dialog
/// promises, how the live feed takes new advice, and how the start form becomes a request. The tests need node; where there is none they are skipped, and
/// <c>BMB_REQUIRE_NODE=1</c> makes that a failure (CI has node).
/// </summary>
public class DreamPageModelTests
{
    public static IEnumerable<object[]> Scenarios() => DreamPageScripts.ScenariosOf(DreamPageScripts.ModelHarness, isAsync: false).Select(s => new object[] { s });

    [BusyHelperBehaviorTests.NodeTheory]
    [MemberData(nameof(Scenarios))]
    public void TheModelDoesWhatItSays(string scenario)
    {
        var (ok, detail) = DreamPageScripts.Run(DreamPageScripts.ModelHarness, [DreamPageScripts.ModelJs], scenario);

        Assert.True(ok, detail);
    }

    [Fact]
    public void TheHarnessCoversTheThingsTheCardTheDialogAndTheFeedHaveToGetRight()
    {
        var names = DreamPageScripts.ScenariosOf(DreamPageScripts.ModelHarness, isAsync: false);

        Assert.True(names.Count >= 30, $"only {names.Count} scenarios");
        foreach (var needed in new[]
        {
            "bands_follow_the_score", "every_outcome_has_words_and_a_colour", "a_history_row_has_the_big_numbers", "the_card_has_the_buttons_the_api_allows",
            "apply_says_exactly_what_it_will_do", "dismiss_offers_four_reaches", "the_feed_adds_what_is_new_in_order_and_never_twice", "every_function_tolerates_a_stub_answer",
        })
            Assert.Contains(needed, names);
    }
}
