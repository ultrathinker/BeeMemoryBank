using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>Where the files of the hand-off to the chat ("Open in AI chat", the draft on /AI) and of "Show prompt" are.</summary>
internal static class DreamHandoffFiles
{
    internal static readonly string Js = Path.Combine(DreamPageScripts.Web, "wwwroot", "js");
    internal static readonly string Draft = Path.Combine(Js, "pages", "ai-dream-draft.js");
    internal static readonly string DraftCss = Path.Combine(DreamPageScripts.Web, "wwwroot", "css", "ai-dream-draft.css");
    internal static readonly string PageScript = Path.Combine(Js, "pages", "dream-handoff.js");
    internal static readonly string Chat = Path.Combine(Js, "chat.js");
    internal static readonly string AiMarkup = Path.Combine(DreamPageScripts.Web, "Pages", "AI.cshtml");
    internal static readonly string TreeMarkup = Path.Combine(DreamPageScripts.Web, "Pages", "Tree.cshtml");
    internal static readonly string ChatUiMarkup = Path.Combine(DreamPageScripts.Web, "Pages", "Shared", "_ChatUi.cshtml");
    internal static readonly string LayoutMarkup = Path.Combine(DreamPageScripts.Web, "Pages", "Shared", "_Layout.cshtml");
    internal static readonly string ChatHarness = Path.Combine(DreamPageScripts.HarnessDir, "chat-handoff-harness.js");

    /// <summary>The scripts of /AI and of /Tree in the order the browser runs them (see <see cref="RealPageScripts"/>), as the JSON the chat harness takes.</summary>
    internal static string OrderJson() => OrderJson(
        RealPageScripts.FromSources(File.ReadAllText(AiMarkup), File.ReadAllText(LayoutMarkup)),
        RealPageScripts.FromSources(File.ReadAllText(TreeMarkup), File.ReadAllText(LayoutMarkup)));

    internal static string OrderJson(IReadOnlyList<string> ai, IReadOnlyList<string> tree) => JsonSerializer.Serialize(new { ai, tree });

    /// <summary>The scenarios the hand-off adds to the harness of the Dream page (they run with the rest of its scenarios in <see cref="DreamPageScriptTests"/>).</summary>
    internal static readonly string[] PageScenarios =
    [
        "show_prompt_asks_for_the_text_for_an_agent_and_shows_it_with_a_copy_button",
        "the_text_of_a_prompt_goes_in_as_text_and_never_as_markup",
        "copy_puts_the_text_on_the_clipboard_and_says_so",
        "copy_falls_back_to_selecting_the_text_where_the_page_has_no_clipboard",
        "a_clipboard_that_refuses_is_followed_by_the_fallback",
        "the_prompt_dialog_is_emptied_when_it_closes",
        "a_prompt_the_node_refuses_is_said_in_its_words_and_the_card_is_read_again",
        "a_prompt_that_comes_back_empty_is_not_shown_as_a_blank_dialog",
        "open_in_ai_chat_opens_a_new_tab_with_only_the_id_and_changes_nothing",
        "the_tab_is_opened_in_the_click_itself_so_no_pop_up_blocker_stops_it",
        "each_press_opens_the_tab_of_its_own_advice",
        "an_applied_advice_is_not_opened_and_the_button_says_when_it_was_applied",
        "a_dismissed_advice_is_not_opened_and_the_button_says_when_it_was_dismissed",
        "an_expired_advice_is_not_opened_and_the_button_gives_the_reason",
        "an_advice_being_applied_is_not_opened",
        "an_advice_that_is_already_in_chat_opens_again",
        "without_a_chat_on_the_node_nothing_is_opened_and_the_person_is_pointed_at_the_prompt",
        "the_two_buttons_keep_their_placeholders_when_the_hand_off_script_is_not_there",
        "the_hand_off_calls_only_the_routes_of_the_contract",
    ];
}

/// <summary>
/// The hand-off from the Dream page to the built-in chat, and "Show prompt". "Open in AI chat" opens /AI?dreamAdvice=&lt;id&gt; in a new tab and changes nothing;
/// the chat page reads only that id (a malformed one is ignored), takes it out of the address at once, asks the node for the task and shows it as a draft that
/// is not sent: Run sends it once in a new conversation and marks the advice "in chat", Edit puts it into the composer and sends nothing, Cancel removes it, and
/// a closed advice gives a notice in the node's words with nothing sent. The homepage chat has no draft. All of it is run in fake pages by node (no packages):
/// <c>JsHarness/chat-handoff-harness.js</c> (the real <c>chat.js</c> and <c>pages/ai-dream-draft.js</c> on the real markup of /AI and /Tree) and the scenarios
/// of <c>JsHarness/dream-page-harness.js</c> (the real <c>dream.js</c> with the hand-off script). Where there is no node the scenarios are skipped, and
/// <c>BMB_REQUIRE_NODE=1</c> makes that a failure.
/// </summary>
public class DreamHandoffTests
{
    public static IEnumerable<object[]> ChatScenarios() => DreamPageScripts.ScenariosOf(DreamHandoffFiles.ChatHarness, isAsync: true).Select(s => new object[] { s });

    [BusyHelperBehaviorTests.NodeTheory]
    [MemberData(nameof(ChatScenarios))]
    public void TheChatPage_ShowsTheTaskAsADraft_AndSendsItOnlyWhenThePersonDoes(string scenario)
    {
        var (ok, detail) = DreamPageScripts.Run(
            DreamHandoffFiles.ChatHarness,
            [DreamHandoffFiles.Chat, DreamHandoffFiles.Draft, DreamHandoffFiles.AiMarkup, DreamHandoffFiles.TreeMarkup, DreamHandoffFiles.ChatUiMarkup, DreamHandoffFiles.OrderJson()], scenario);

        Assert.True(ok, detail);
    }

    [Fact]
    public void TheHarnesses_CoverWhatTheBriefAsksFor()
    {
        var page = DreamPageScripts.ScenariosOf(DreamPageScripts.PageHarness, isAsync: true);
        foreach (var needed in DreamHandoffFiles.PageScenarios) Assert.Contains(needed, page);

        var chat = DreamPageScripts.ScenariosOf(DreamHandoffFiles.ChatHarness, isAsync: true);
        foreach (var needed in new[]
        {
            // the order of the real page: the page's scripts before the layout's busy helper; the draft waits for the load, and does nothing without the helper
            "the_draft_starts_in_the_order_of_the_real_page_where_the_busy_helper_comes_after_chat_js",
            "the_draft_waits_for_the_page_to_load_and_takes_the_parameter_out_of_the_address_at_once",
            "a_page_swapped_in_by_the_spa_navigation_starts_the_draft_at_once", "a_page_without_the_busy_helper_shows_no_draft_and_sends_nothing",
            // the draft is shown and not sent; the parameter leaves the address; a bad or missing id does nothing; the homepage chat has none
            "the_task_is_shown_as_a_draft_and_nothing_is_sent", "the_parameter_leaves_the_address_at_once_and_a_reload_brings_no_draft",
            "a_bad_or_missing_id_does_nothing", "the_rest_of_the_address_never_starts_a_task", "the_task_goes_on_the_page_as_text_never_as_markup",
            "the_homepage_chat_shows_no_draft",
            // Run sends once and marks the advice; Edit fills the composer and sends nothing; Cancel
            "run_sends_the_text_once_in_a_new_conversation_and_marks_the_advice", "edit_puts_the_whole_text_in_the_composer_and_sends_nothing",
            "the_edited_text_is_sent_with_enter_and_marks_the_advice", "cancel_removes_the_draft_and_changes_nothing",
            // a closed advice gives the notice and sends nothing, at load, at Run and at the Send of the edited text
            "an_advice_that_is_closed_when_the_tab_loads_gives_a_notice_and_sends_nothing", "an_advice_closed_before_run_gives_the_notice_in_place_of_the_draft",
            "an_advice_closed_before_the_edited_text_is_sent_gives_the_notice_and_keeps_the_text",
            // the chat is what it was, and its own cards still guard every change
            "enter_still_sends_what_was_typed_and_an_empty_field_sends_nothing", "a_turn_of_the_task_stops_at_the_chats_own_card_and_changes_nothing_until_allow",
            // the review: a check on its way is not raced by Cancel, Edit, New chat or unlink; a late answer sends nothing and takes its mark back
            "cancel_and_edit_wait_while_run_is_checking_the_advice", "cancel_during_run_s_check_sends_nothing_and_takes_the_mark_back",
            "edit_during_run_s_check_sends_nothing_and_leaves_the_text_in_the_composer", "a_new_chat_during_run_s_check_sends_nothing_and_takes_the_mark_back",
            "the_unlink_control_waits_while_the_edited_text_is_checked_and_a_late_answer_sends_nothing",
            // the review: only a clear refusal gives the task back; a broken or stopped stream leaves it "in chat" with a warning and the text
            "a_turn_the_chat_refuses_puts_the_advice_back_and_the_draft_returns", "a_clear_refusal_of_the_edited_text_gives_it_back_and_takes_the_mark_back",
            "a_stream_that_breaks_before_the_node_confirms_leaves_the_advice_in_chat_and_offers_no_run", "a_send_stopped_before_the_node_confirms_is_not_given_back_either",
            "an_edited_text_whose_stream_breaks_is_kept_in_the_warning_and_not_linked_again",
            // the review: a closed advice seen by the load gets the notice; the composer cap is only for the task
            "an_advice_closed_between_the_two_answers_of_the_load_gets_the_notice_not_a_draft", "the_composer_is_capped_only_while_it_holds_the_task",
            // the re-review: which answers that are not a stream are refusals
            "an_error_answer_the_node_gives_before_saving_is_a_refusal", "a_non_stream_answer_with_a_success_status_is_not_a_refusal",
            "the_sign_in_redirect_is_a_refusal", "a_bare_502_of_the_passthrough_is_not_a_refusal", "a_500_of_a_turn_with_images_is_not_a_refusal_and_one_without_is",
        })
            Assert.Contains(needed, chat);
    }

    [Fact]
    public void TheChatScript_ReadsNothingOfTheAddress_AndTheDraftReadsOnlyTheAdviceId()
    {
        var chat = File.ReadAllText(DreamHandoffFiles.Chat);
        var draft = File.ReadAllText(DreamHandoffFiles.Draft);

        // chat.js itself takes nothing from where the page was opened: the one thing that does is the draft script, and it reads one parameter
        foreach (var banned in new[] { "location.search", "location.hash", "location.href", "URLSearchParams", "document.referrer", "window.name", "postMessage", "sessionStorage" })
            chat.Should().NotContain(banned, $"chat.js must not take anything from {banned}");
        draft.Should().Contain("var PARAM = 'dreamAdvice';");
        Regex.Matches(draft, @"params\.get\(").Count.Should().Be(1, "the draft reads one parameter of the address");
        draft.Should().Contain("params.get(PARAM)");
        draft.Should().Contain("GUID.test(raw)", "and uses it only when it is an id");
        draft.Should().Contain("window.history.replaceState(", "and takes it out of the address");
        foreach (var banned in new[] { "document.referrer", "window.name", "postMessage", "sessionStorage", "localStorage", "location.assign", "location.href", "window.open" })
            draft.Should().NotContain(banned);
        chat.Should().Contain("if (chatSidebar && window.bmbAiDreamDraft) {", "the draft is started on /AI only, where the chat sidebar is");
    }

    [Fact]
    public void TheDreamPage_OpensTheChatInANewTab_WithOnlyTheId()
    {
        var page = File.ReadAllText(DreamHandoffFiles.PageScript);

        Regex.Matches(page, @"window\.open\(").Count.Should().Be(1);
        page.Should().Contain("window.open(CHAT_PAGE + '?' + CHAT_PARAM + '=' + enc(advice.id), '_blank', 'noopener');", "the id and nothing else, and no handle on the Dream page");
        page.Should().Contain("var CHAT_PARAM = 'dreamAdvice';", "the name the draft reads");
        page.Should().NotContain("'/status'", "opening the tab does not mark the advice");
        page.Should().NotContain("location.assign", "the Dream tab stays where it is");
    }

    [Fact]
    public void TheSessionStorageHandOff_IsGone()
    {
        File.Exists(Path.Combine(DreamHandoffFiles.Js, "dream-handoff-store.js")).Should().BeFalse("the task goes through the node, not through the tab's storage");
        File.Exists(Path.Combine(DreamPageScripts.HarnessDir, "dream-handoff-store-harness.js")).Should().BeFalse();

        var sources = Directory.EnumerateFiles(Path.Combine(DreamPageScripts.Web, "wwwroot", "js"), "*.js", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(DreamPageScripts.Web, "Pages"), "*.cshtml", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(DreamPageScripts.RepoRoot, "server", "BeeMemoryBank.Api", "Dream"), "*.cs", SearchOption.AllDirectories));
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            text.Should().NotContain("bmb.dream.handoff", Path.GetFileName(file));
            text.Should().NotContain("bmbDreamHandoff", Path.GetFileName(file));
            text.Should().NotContain("dream-handoff-store", Path.GetFileName(file));
        }
        foreach (var file in new[] { DreamHandoffFiles.Chat, DreamHandoffFiles.Draft, DreamHandoffFiles.PageScript })
            File.ReadAllText(file).Should().NotContain("sessionStorage", Path.GetFileName(file));
    }

    [Fact]
    public void TheChatPage_LoadsTheDraftBeforeChatJs_AndTheHomepageDoesNot()
    {
        var ai = File.ReadAllText(DreamHandoffFiles.AiMarkup);
        var tree = File.ReadAllText(DreamHandoffFiles.TreeMarkup);

        ai.Should().Contain("<script src=\"/js/pages/ai-dream-draft.js\" asp-append-version=\"true\"></script>");
        ai.Should().Contain("<link rel=\"stylesheet\" href=\"/css/ai-dream-draft.css\" asp-append-version=\"true\" />");
        ai.IndexOf("/js/pages/ai-dream-draft.js", StringComparison.Ordinal).Should().BeLessThan(ai.IndexOf("/js/chat.js", StringComparison.Ordinal), "chat.js starts the draft");
        tree.Should().NotContain("ai-dream-draft", "the chat of the homepage has no draft from Dream");
    }

    [Fact]
    public void TheDraft_IsStartedWithoutTheBusyHelper_AndAsksTheNodeOnlyOnceThePageHasLoaded()
    {
        var draft = File.ReadAllText(DreamHandoffFiles.Draft);

        // /AI runs its own scripts (ai.js, the draft, chat.js) before the layout's bmb-busy.js, so start() must not look for the helper
        draft.Should().NotContain("!window.bmbBusy) return null", "the helper is not there yet when chat.js starts the draft");
        draft.Should().Contain("if (id) whenLoaded(function () { if (window.bmbBusy) load(id); });", "the first call to the node waits until the page has loaded; without the helper it stays quiet");
        Regex.Matches(draft, @"\bload\(id\)").Count.Should().Be(1, "nothing else starts the load");
        draft.Should().Contain("document.readyState === 'loading'");
        draft.Should().Contain("document.addEventListener('DOMContentLoaded', fn, { once: true });");
    }

    [Fact]
    public void ARealPage_ThatNeedsTheBusyHelperWhileItLoads_LoadsItsScriptAfterTheLayout()
    {
        // A script that leaves at once when window.bmbBusy is missing must come after bmb-busy.js, i.e. from the page's Scripts section; a script in the
        // page's own body runs before the layout's scripts (the chat page's draft was bitten by it: the helper was never there when it started).
        var guarded = Directory.EnumerateFiles(DreamHandoffFiles.Js, "*.js", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("bmb-busy.js", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"!window\.bmbBusy\b"))
            .Select(f => "/" + Path.GetRelativePath(Path.Combine(DreamPageScripts.Web, "wwwroot"), f).Replace('\\', '/'))
            .ToList();
        guarded.Should().Contain("/js/pages/dream.js", "the Dream page's scripts are such scripts");

        foreach (var page in Directory.EnumerateFiles(Path.Combine(DreamPageScripts.Web, "Pages"), "*.cshtml", SearchOption.AllDirectories))
        {
            var (body, _) = RealPageScripts.SplitScriptsSection(File.ReadAllText(page));
            foreach (var script in guarded)
                RealPageScripts.OrderOf(body).Should().NotContain(script,
                    $"{Path.GetFileName(page)} loads {script}, which needs bmbBusy as it starts, in its own body, before the layout's bmb-busy.js; put it in @section Scripts");
        }
    }

    [Fact]
    public void TheOrderOfTheChatPage_ComesFromItsMarkup_WithThePageBeforeTheLayout()
    {
        var order = RealPageScripts.FromSources(File.ReadAllText(DreamHandoffFiles.AiMarkup), File.ReadAllText(DreamHandoffFiles.LayoutMarkup));

        order.Should().Contain(["/js/pages/ai.js", "/js/pages/ai-dream-draft.js", "/js/chat.js", "/js/bmb-busy.js", "/js/site.js"]);
        order.IndexOf("/js/chat.js").Should().BeLessThan(order.IndexOf("/js/bmb-busy.js"), "RenderBody comes before the layout's scripts");
        order.IndexOf("/js/pages/ai-dream-draft.js").Should().BeLessThan(order.IndexOf("/js/chat.js"));
        order.IndexOf("/js/bmb-busy.js").Should().BeLessThan(order.IndexOf("/js/site.js"));
    }

    [Fact]
    public void TheComposerIsCapped_OnlyWhileItHoldsTheTaskFromDream()
    {
        var css = File.ReadAllText(DreamHandoffFiles.DraftCss);

        css.Should().Contain("body.ai-chat-page #chat-input.chat-dream-task::part(textarea)", "the cap needs the class the draft sets while the composer holds the task");
        Regex.Matches(css, @"#chat-input[^{]*\{").Select(m => m.Value).Should().OnlyContain(rule => rule.Contains(".chat-dream-task"), "an ordinary message grows the composer as before");
        File.ReadAllText(DreamHandoffFiles.Draft).Should().Contain("var TASK_CLASS = 'chat-dream-task';");
    }

    [Fact]
    public void OnlyAClearRefusalOfTheTurn_GivesTheTaskBack()
    {
        var chat = File.ReadAllText(DreamHandoffFiles.Chat);
        var draft = File.ReadAllText(DreamHandoffFiles.Draft);

        // chat.js marks a turn refused only in the branch of an answer that is not a stream: the sign-in redirect, and what refusedBeforeSaving says
        var nonStream = chat.IndexOf("if (ct.indexOf('text/event-stream') >= 0) {", StringComparison.Ordinal);
        Regex.Matches(chat, @"turn\.refused = ").Count.Should().Be(2);
        foreach (Match m in Regex.Matches(chat, @"turn\.refused = ")) m.Index.Should().BeGreaterThan(nonStream);
        chat.Should().Contain("turn.refused = true; // the Web's sign-in check answered before the request reached the Api: nothing was saved");
        chat.Should().Contain("turn.refused = refusedBeforeSaving(resp.status, msg !== null, !!(bodyObj && bodyObj.attachments && bodyObj.attachments.length));");
        chat.Should().Contain("if (status >= 400 && status < 500) return true;", "every 4xx of the endpoint and the filters in front of it comes before the save");
        chat.Should().Contain("if (status >= 500 && status < 600 && nodeSaidWhy) return !(status === 500 && hadImages);", "a 5xx only with the node's words, and a 500 not on the path of images");
        draft.Should().Contain("if (t && !t.refused) { unconfirmed(task, !!t.aborted); return; }", "anything else leaves the advice in chat and offers no second send");
        draft.Should().Contain("'The connection broke while sending. Check the conversation list before sending again.'");
    }

    [Fact]
    public void TheHandOffScripts_UseOnlyShoelaceComponents_AndSendOnlyBehindTheBusyHelper()
    {
        var custom = new Regex(@"(?:<|createElement\(')([a-z][a-z0-9]*-[a-z0-9-]+)");
        foreach (var file in new[] { DreamHandoffFiles.Draft, DreamHandoffFiles.PageScript })
        {
            var text = File.ReadAllText(file);
            custom.Matches(text).Select(m => m.Groups[1].Value).Distinct().Where(tag => !tag.StartsWith("sl-", StringComparison.Ordinal))
                .Should().BeEmpty($"{Path.GetFileName(file)} may use Shoelace's components only");
            BusyHelperGuardTests.ScriptProblems(Path.GetFileName(file), text).Should().BeEmpty();
            text.Should().NotContain("innerHTML", "what the node wrote goes in as text or as the value of a field, never as markup");
            text.Should().NotContain("console.", "no text of a task, a prompt or an advice is written to the console");
        }

        File.ReadAllText(DreamHandoffFiles.PageScript).Should().NotContain("/api-proxy", "the one place of the Dream page that knows the address of the API is dream.js (bmbDream.api)");
        var draft = File.ReadAllText(DreamHandoffFiles.Draft);
        Regex.Matches(draft, @"/api-proxy/[a-z-]+").Select(m => m.Value).Distinct().Should().Equal(["/api-proxy/dream"], "the draft talks to Dream only; the chat sends through chat.js");
        draft.Should().Contain("'RequestVerificationToken': antiforgery()", "its one change carries the page's anti-forgery token");
    }

    [Fact]
    public void TheDreamMarkup_HasTheDialogOfTheScript_AndLoadsTheScriptsInOrder()
    {
        var markup = File.ReadAllText(DreamPageScripts.Markup);

        foreach (var id in new[] { "dlg-dream-prompt", "dream-prompt-title", "dream-prompt-text", "dream-prompt-note", "dream-prompt-copy" })
            markup.Should().Contain($"id=\"{id}\"", $"pages/dream-handoff.js draws into #{id}");
        markup.Should().Contain("<sl-textarea id=\"dream-prompt-text\"").And.MatchRegex(@"id=""dream-prompt-text""[^>]*\sreadonly\s", "the prompt is read, copied, and not edited here");
        markup.Should().Contain("data-dlg-cancel=\"dlg-dream-prompt\"", "the dialog can be closed");

        var order = new[] { "/js/pages/dream-model.js", "/js/pages/dream.js", "/js/pages/dream-handoff.js" }
            .Select(f => markup.IndexOf(f, StringComparison.Ordinal)).ToArray();
        order.Should().NotContain(-1);
        order.Should().BeInAscendingOrder("the model and the page are there when the hand-off script starts");
        markup.Should().Contain("<script src=\"/js/pages/dream-handoff.js\" asp-append-version=\"true\"></script>");
        markup.Should().NotContain("dream-handoff-store", "the store of the old hand-off is gone");
    }

    [Fact]
    public void NoFileOfThePackage_HoldsACyrillicLetter()
    {
        var files = new[]
        {
            DreamHandoffFiles.Draft, DreamHandoffFiles.DraftCss, DreamHandoffFiles.PageScript, DreamHandoffFiles.Chat, DreamHandoffFiles.AiMarkup, DreamPageScripts.Markup,
            DreamPageScripts.PageJs, DreamPageScripts.Css, DreamHandoffFiles.ChatHarness, DreamPageScripts.PageHarness,
        }.Concat(Directory.EnumerateFiles(Path.Combine(DreamPageScripts.RepoRoot, "tests", "BeeMemoryBank.Integration.Tests", "Dream"), "DreamHandoff*.cs"));

        foreach (var file in files)
            Regex.IsMatch(File.ReadAllText(file), @"\p{IsCyrillic}").Should().BeFalse($"{Path.GetFileName(file)} is part of a public repository, in English");
    }
}
