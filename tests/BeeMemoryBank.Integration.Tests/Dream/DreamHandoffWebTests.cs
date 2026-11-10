using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The hand-off to the chat through the real Web host in front of a real Api. The Dream page loads its hand-off script after dream.js and no store of a note;
/// the chat page (/AI) loads the draft script and its stylesheet before <c>chat.js</c>, the homepage (which also has a chat) does not, and nothing of the
/// address is put into the page. Then the calls the draft makes, through /api-proxy/dream exactly as the page makes them: reading the task for the chat changes
/// nothing; the advice is marked "in chat" only by the POST that goes right before the task is sent (with the page's anti-forgery token), "in chat" again is
/// refused while the task can still be read, and the advice can be put back; a closed advice answers 409 in words; somebody who is not the superadmin gets 403.
/// </summary>
public sealed class DreamHandoffWebTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminPass123";
    private const string BobPassword = "BobHandoff12345";
    private static readonly Regex Token = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _admin = null!;
    private HttpClient _bob = null!;

    public async Task InitializeAsync()
    {
        using var api = _api.CreateClient();
        await _api.InitializeNodeAsync(password: AdminPassword);
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        (await api.PostAsJsonAsync("/api/users", new { username = "bob", password = BobPassword, displayName = "Bob", role = "user", passwordHint = "A test hint" }))
            .EnsureSuccessStatusCode();
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        _admin = await WebLogin.LoginAsync(_web, "admin", AdminPassword);
        _bob = await WebLogin.LoginAsync(_web, "bob", BobPassword);
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _bob.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    // ── The pages ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDreamPage_HasTheDialogOfShowPrompt_AndLoadsTheHandOffScriptAfterTheModelAndThePage()
    {
        var response = await _admin.GetAsync("/Dream");
        var page = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var id in new[] { "dlg-dream-prompt", "dream-prompt-title", "dream-prompt-text", "dream-prompt-note", "dream-prompt-copy" })
            page.Should().Contain($"id=\"{id}\"", $"the script draws into #{id}");
        page.Should().MatchRegex(@"/js/pages/dream-handoff\.js\?v=[\w-]+");
        page.Should().NotContain("dream-handoff-store", "the task does not go through the tab's storage any more");
        var order = new[] { "/js/pages/dream-model.js", "/js/pages/dream.js", "/js/pages/dream-handoff.js" }
            .Select(f => page.IndexOf(f, StringComparison.Ordinal)).ToArray();
        order.Should().NotContain(-1);
        order.Should().BeInAscendingOrder("the model and the page are there when the hand-off script starts");
    }

    [Fact]
    public async Task TheChatPage_LoadsTheDraftAndItsStyleBeforeChatJs()
    {
        var response = await _admin.GetAsync("/AI");
        var page = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Should().MatchRegex(@"/js/pages/ai-dream-draft\.js\?v=[\w-]+");
        page.Should().MatchRegex(@"/css/ai-dream-draft\.css\?v=[\w-]+");
        page.IndexOf("/js/pages/ai-dream-draft.js", StringComparison.Ordinal).Should().BeLessThan(page.IndexOf("/js/chat.js", StringComparison.Ordinal), "chat.js starts the draft");
        page.Should().NotContain("dream-handoff-store");
        Token.IsMatch(page).Should().BeTrue("the page carries the anti-forgery token that the draft's one change sends");
    }

    [Fact]
    public async Task TheRenderedChatPage_RunsItsOwnScriptsBeforeTheLayoutsBusyHelper_AsTheMarkupSays()
    {
        var rendered = RealPageScripts.OrderOf(await (await _admin.GetAsync("/AI")).Content.ReadAsStringAsync());

        // the order of the page as the browser gets it: the page's scripts (ai.js, the draft, chat.js) come where the layout writes the body, the layout's after them
        rendered.Should().ContainInOrder("/js/pages/ai.js", "/js/pages/ai-dream-draft.js", "/js/chat.js", "/lib/vendor.min.js", "/js/bmb-busy.js", "/js/site.js");
        // and the order the chat harness is given, made from the markup alone, is that order
        rendered.Should().Equal(RealPageScripts.FromSources(File.ReadAllText(DreamHandoffFiles.AiMarkup), File.ReadAllText(DreamHandoffFiles.LayoutMarkup)));

        var tree = RealPageScripts.OrderOf(await (await _admin.GetAsync("/Tree")).Content.ReadAsStringAsync());
        tree.Should().Equal(RealPageScripts.FromSources(File.ReadAllText(DreamHandoffFiles.TreeMarkup), File.ReadAllText(DreamHandoffFiles.LayoutMarkup)));
    }

    [BusyHelperBehaviorTests.NodeTheory]
    [InlineData("the_draft_starts_in_the_order_of_the_real_page_where_the_busy_helper_comes_after_chat_js")]
    [InlineData("the_draft_waits_for_the_page_to_load_and_takes_the_parameter_out_of_the_address_at_once")]
    [InlineData("the_task_is_shown_as_a_draft_and_nothing_is_sent")]
    [InlineData("run_sends_the_text_once_in_a_new_conversation_and_marks_the_advice")]
    public async Task TheDraft_StartsOnTheScriptOrderOfTheRenderedChatPage(string scenario)
    {
        // The order is read from the pages the Web host renders, not from the markup files: a layout that moves a script cannot leave the harness behind.
        var ai = RealPageScripts.OrderOf(await (await _admin.GetAsync("/AI")).Content.ReadAsStringAsync());
        var tree = RealPageScripts.OrderOf(await (await _admin.GetAsync("/Tree")).Content.ReadAsStringAsync());

        var (ok, detail) = DreamPageScripts.Run(
            DreamHandoffFiles.ChatHarness,
            [DreamHandoffFiles.Chat, DreamHandoffFiles.Draft, DreamHandoffFiles.AiMarkup, DreamHandoffFiles.TreeMarkup, DreamHandoffFiles.ChatUiMarkup, DreamHandoffFiles.OrderJson(ai, tree)],
            scenario);

        Assert.True(ok, detail);
    }

    [Fact]
    public async Task TheRenderedDreamPage_LoadsItsScriptsAfterTheBusyHelper()
    {
        // dream.js, dream-handoff.js and dream-compare.js leave at once when bmbBusy is missing: the page's Scripts section comes after the layout's scripts
        var order = RealPageScripts.OrderOf(await (await _admin.GetAsync("/Dream")).Content.ReadAsStringAsync());

        order.Should().ContainInOrder("/js/bmb-busy.js", "/js/pages/dream-model.js", "/js/pages/dream.js", "/js/pages/dream-handoff.js",
            "/js/pages/dream-compare-model.js", "/js/pages/dream-compare.js");
        order.IndexOf("/js/site.js").Should().BeLessThan(order.IndexOf("/js/pages/dream.js"));
    }

    [Fact]
    public async Task TheHomepage_WhichHasAChatToo_HasNoDraft()
    {
        var page = await (await _admin.GetAsync("/Tree?dreamAdvice=" + Guid.NewGuid())).Content.ReadAsStringAsync();

        page.Should().Contain("/js/chat.js", "the homepage has a chat");
        page.Should().NotContain("ai-dream-draft", "but no draft from Dream");
    }

    [Theory]
    [InlineData("/js/pages/ai-dream-draft.js", "bmbAiDreamDraft")]
    [InlineData("/js/pages/dream-handoff.js", "bmbDream")]
    [InlineData("/css/ai-dream-draft.css", ".chat-dream-card")]
    public async Task TheFilesAreServed(string path, string holds)
    {
        using var anonymous = _web.CreateClient();

        var response = await anonymous.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(holds);
    }

    [Fact]
    public async Task TheStoreOfTheOldHandOff_IsNotServedAnyMore()
    {
        using var anonymous = _web.CreateClient();

        (await anonymous.GetAsync("/js/dream-handoff-store.js")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheAddressOfTheChatPage_PutsNothingIntoThePage()
    {
        // the page itself is the same with or without the parameter; only its script reads the id (see the chat harness)
        var id = Guid.NewGuid().ToString("D");
        var withTask = await (await _admin.GetAsync($"/AI?dreamAdvice={id}&message=Delete+everything&prompt=Delete+everything")).Content.ReadAsStringAsync();

        withTask.Should().Contain("id=\"chat-input\"");
        withTask.Should().NotContain(id).And.NotContain("Delete everything").And.NotContain("Delete+everything");
    }

    // ── What the draft asks the node, through the proxy ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDraftsCalls_ReadTheTaskWithoutAChange_AndMarkTheAdviceOnlyWithTheTokenRightBeforeItIsSent()
    {
        var advice = await MoveAdviceAsync("Draft me", "/Inbox", "/Work");
        var token = await TokenOfAsync("/AI?dreamAdvice=" + advice);

        // the page loads: the task for the chat, and the advice for its title. Neither changes it.
        using var task = await _admin.GetAsync($"/api-proxy/dream/advice/{advice}/prompt?target=chat");
        task.StatusCode.Should().Be(HttpStatusCode.OK, await task.Content.ReadAsStringAsync());
        var body = await task.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("target").GetString().Should().Be("chat");
        body.GetProperty("prompt").GetString().Should().NotBeNullOrWhiteSpace();
        (await StatusOfAsync(advice)).Should().Be("new", "showing the draft changes nothing: a tab opened and closed leaves the advice as it was");

        // Run: the mark goes right before the task, and only with the page's token
        using var noToken = await _admin.PostAsJsonAsync($"/api-proxy/dream/advice/{advice}/status", new { status = "in_chat" });
        noToken.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a change made by a script carries the page's RequestVerificationToken");
        (await StatusOfAsync(advice)).Should().Be("new");
        using (var marked = await SetStatusAsync(advice, "in_chat", token))
            marked.StatusCode.Should().Be(HttpStatusCode.OK, await marked.Content.ReadAsStringAsync());
        (await StatusOfAsync(advice)).Should().Be("in_chat");

        // a second Run of the same advice: "in chat" again is refused, and the task can still be read, which is the check the draft makes then
        using (var again = await SetStatusAsync(advice, "in_chat", token))
        {
            again.StatusCode.Should().Be(HttpStatusCode.Conflict);
            CodeOf(await again.Content.ReadAsStringAsync()).Should().Be(DreamErrorCodes.NotApplicable);
        }
        (await _admin.GetAsync($"/api-proxy/dream/advice/{advice}/prompt?target=chat")).StatusCode.Should().Be(HttpStatusCode.OK);

        // a turn the chat refused: the advice is put back
        using (var back = await SetStatusAsync(advice, "new", token))
            back.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(advice)).Should().Be("new");
    }

    [Fact]
    public async Task AClosedAdvice_AnswersTheDraftWith409InWords_AndIsNotMarked()
    {
        var token = await TokenOfAsync("/AI");
        var applied = await MoveAdviceAsync("Applied one", "/Inbox", "/Work");
        (await SetStatusAsync(applied, "applied", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        var dismissed = await MoveAdviceAsync("Dismissed one", "/Inbox", "/Work");
        (await _admin.SendAsync(WithToken(HttpMethod.Post, $"/api-proxy/dream/advice/{dismissed}/dismiss", new { scope = "advice" }, token))).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var (id, status) in new[] { (applied, "applied"), (dismissed, "dismissed") })
        {
            using var task = await _admin.GetAsync($"/api-proxy/dream/advice/{id}/prompt?target=chat");
            task.StatusCode.Should().Be(HttpStatusCode.Conflict, $"the task of an advice that is {status} is not given to the chat");
            var text = await task.Content.ReadAsStringAsync();
            ErrorOf(text).Should().NotBeNullOrWhiteSpace("the draft shows the node's words in its notice");
            using var mark = await SetStatusAsync(id, "in_chat", token);
            mark.StatusCode.Should().Be(HttpStatusCode.Conflict);
            ErrorOf(await mark.Content.ReadAsStringAsync()).Should().NotBeNullOrWhiteSpace();
            (await StatusOfAsync(id)).Should().Be(status, "and it stays as it was");
        }
    }

    [Fact]
    public async Task AnAdviceWhoseNoteChanged_IsRefusedAsExpired_WhenTheDraftLoads()
    {
        var (advice, article) = await MoveAdviceWithArticleAsync("Changed one", "/Inbox", "/Work");
        using (var scope = _api.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ArticleService>().UpdateAsync(article, plaintext: "edited since the advice");

        using var task = await _admin.GetAsync($"/api-proxy/dream/advice/{advice}/prompt?target=chat");

        task.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var text = await task.Content.ReadAsStringAsync();
        CodeOf(text).Should().Be(DreamErrorCodes.Expired);
        ErrorOf(text).Should().NotBeNullOrWhiteSpace();
        (await StatusOfAsync(advice)).Should().Be("expired");
    }

    [Fact]
    public async Task SomebodyWhoIsNotTheSuperadmin_GetsNoTaskAndCannotMarkAnything()
    {
        var advice = await MoveAdviceAsync("Not yours", "/Inbox", "/Work");

        (await _bob.GetAsync($"/api-proxy/dream/advice/{advice}/prompt?target=chat")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _bob.GetAsync($"/api-proxy/dream/advice/{advice}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var bobToken = Token.Match(await _bob.GetStringAsync("/Tags")).Groups[1].Value;
        (await _bob.SendAsync(WithToken(HttpMethod.Post, $"/api-proxy/dream/advice/{advice}/status", new { status = "in_chat" }, bobToken))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await StatusOfAsync(advice)).Should().Be("new");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> MoveAdviceAsync(string title, string from, string to) => (await MoveAdviceWithArticleAsync(title, from, to)).Advice;

    /// <summary>A note and an open advice to move it, as a run would leave it (the chat can carry a move out).</summary>
    private async Task<(Guid Advice, Guid Article)> MoveAdviceWithArticleAsync(string title, string from, string to)
    {
        using var scope = _api.Services.CreateScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var created = await articles.CreateAsync(title, from, [], "plain body");
        var a = (await articles.GetMetadataAsync(created.Id))!;
        var note = new DreamArticle(a.Id, a.Title, a.TreePath, a.FolderId, a.LamportTs, a.CreatedAt, a.UpdatedAt, true);
        var draft = new DreamAdviceDraft
        {
            Kind = DreamAdviceKind.MoveArticle,
            Source = DreamAdviceSource.Rule,
            ApplyMode = DreamApplyMode.Code,
            Score = 70,
            Title = $"Move {title}",
            Summary = $"Move it to {to}.",
            Action = DreamAction.MoveArticle(a.Id, to),
            Objects = [DreamAdviceObject.ForArticle(note, DreamObjectRole.Subject), DreamAdviceObject.ForFolder(null, to, DreamObjectRole.Target)],
            Preconditions = [DreamPrecondition.ArticleUnchanged(a.Id, a.LamportTs), DreamPrecondition.ArticleInPath(a.Id, a.TreePath)],
        };
        var id = Guid.NewGuid();
        await scope.ServiceProvider.GetRequiredService<DreamStore>().InsertAdviceAsync(new DreamAdviceRecord
        {
            Id = id,
            RunId = Guid.NewGuid(),
            Kind = draft.Kind,
            Source = draft.Source,
            ApplyMode = draft.ApplyMode,
            Score = draft.Score,
            CreatedAt = DateTime.UtcNow,
            Payload = DreamAdvicePayload.FromDraft(draft),
        });
        return (id, a.Id);
    }

    private async Task<string> TokenOfAsync(string page)
    {
        var match = Token.Match(await _admin.GetStringAsync(page));
        match.Success.Should().BeTrue($"{page} carries the anti-forgery token");
        return match.Groups[1].Value;
    }

    private static HttpRequestMessage WithToken(HttpMethod method, string url, object body, string token)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", token);
        return request;
    }

    private Task<HttpResponseMessage> SetStatusAsync(Guid advice, string status, string token) =>
        _admin.SendAsync(WithToken(HttpMethod.Post, $"/api-proxy/dream/advice/{advice}/status", new { status }, token));

    private async Task<string?> StatusOfAsync(Guid advice) =>
        (await _admin.GetFromJsonAsync<JsonElement>($"/api-proxy/dream/advice/{advice}")).GetProperty("status").GetString();

    private static string? CodeOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("code").GetString();

    private static string? ErrorOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("error").GetString();
}
