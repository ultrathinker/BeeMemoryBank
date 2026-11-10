using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The run from start to history (plan 4, package P0b), with a training detector, a training model task and a model that
/// never leaves the process. In the heavy-operation collection: a run stops for HeavyOperationLock, which is process-wide.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class DreamEngineTests : IAsyncLifetime
{
    private const string Password = "dreamEnginePw1!";
    private static readonly byte[] Vector = Enumerable.Range(0, 16).SelectMany(i => BitConverter.GetBytes((float)(i + 1))).ToArray();

    private DreamEngineTestNode _node = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync() => await StartNodeAsync(new DreamEngineTestNode());

    private async Task StartNodeAsync(DreamEngineTestNode node, bool encrypted = false)
    {
        _node = node;
        if (encrypted) await _node.InitializeEncryptedNodeAsync(password: Password);
        else await _node.InitializeNodeAsync(password: Password);
        (await Session.UnlockAsync(Password)).Should().BeTrue();
        _client = _node.CreateClient();
    }

    private async Task ReplaceNodeAsync(DreamEngineTestNode node, bool encrypted = false)
    {
        await StopNodeAsync();
        await StartNodeAsync(node, encrypted);
    }

    public async Task DisposeAsync() => await StopNodeAsync();

    private async Task StopNodeAsync()
    {
        _client.Dispose();
        await Coordinator.WaitForIdleAsync();
        SqliteConnection.ClearAllPools();
        _node.Dispose();
    }

    private SessionService Session => _node.Services.GetRequiredService<SessionService>();
    private DreamCoordinator Coordinator => _node.Services.GetRequiredService<DreamCoordinator>();
    private DreamScheduler Scheduler => _node.Services.GetServices<IHostedService>().OfType<DreamScheduler>().Single();

    private T InScope<T>(Func<IServiceProvider, T> use)
    {
        using var scope = _node.Services.CreateScope();
        return use(scope.ServiceProvider);
    }

    private async Task WithStoreAsync(Func<DreamStore, Task> use)
    {
        using var scope = _node.Services.CreateScope();
        await use(scope.ServiceProvider.GetRequiredService<DreamStore>());
    }

    private SqliteConnection ChatDb() => (SqliteConnection)_node.Services.GetRequiredService<ChatDbConnectionFactory>().CreateConnection();

    // ── Seeding ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An article with a vector, so the embeddings phase has nothing to do.</summary>
    private async Task<Article> NewArticleAsync(string title, string path = "/Notes", string body = "plain body", params string[] tags)
    {
        using var scope = _node.Services.CreateScope();
        var article = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(title, path, tags.ToList(), body);
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        conn.Execute("UPDATE tbl_article SET embedding_projection = @v, embedding_pending = 0 WHERE id = @id", new { v = Vector, id = article.Id }).Should().Be(1);
        return article;
    }

    private static DreamAdviceDraft MoveDraft(DreamArticle article, int score, string? title = null) => new()
    {
        Kind = DreamAdviceKind.MoveArticle,
        Source = DreamAdviceSource.Rule,
        ApplyMode = DreamApplyMode.Code,
        Score = score,
        Title = title ?? $"Move {article.Title}",
        Summary = $"Summary of {article.Title}",
        Action = DreamAction.MoveArticle(article.Id, "/Target"),
        Objects = [DreamAdviceObject.ForArticle(article, DreamObjectRole.Subject)],
        Preconditions = [DreamPrecondition.ArticleUnchanged(article.Id, article.LamportTs)],
    };

    /// <summary>One draft per article, with these scores in order.</summary>
    private void DetectMoves(params int[] scores) => _node.Script.Detect = (corpus, _, ct) => Yield(corpus.Articles.OrderBy(a => a.Title, StringComparer.Ordinal).Zip(scores, (a, s) => MoveDraft(a, s)), ct);

    private static async IAsyncEnumerable<T> Yield<T>(IEnumerable<T> items, [EnumeratorCancellation] CancellationToken ct, TimeSpan? between = null)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
            if (between is { } delay) await Task.Delay(delay, ct);
        }
    }

    private async Task AddTextModelAsync()
    {
        (await _client.PostAsJsonAsync("/api/chat/models", new { modelId = "fake/model", label = "Fake" })).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync("/api/chat/keys", new { label = "k", apiKey = "sk-test-key" })).EnsureSuccessStatusCode();
    }

    // ── Driving a run through the API ────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> StartAsync(object? body = null)
    {
        var response = await _client.PostAsJsonAsync("/api/dream/runs", body ?? new { });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
    }

    private Task<JsonElement> GetRunAsync(Guid id) => _client.GetFromJsonAsync<JsonElement>($"/api/dream/runs/{id}");

    private async Task<JsonElement> WaitFinishedAsync(Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var run = await GetRunAsync(id);
            if (run.GetProperty("state").GetString() == "finished")
            {
                await Coordinator.WaitForIdleAsync();
                return run;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"Run {id} did not finish.");
    }

    private async Task<JsonElement> RunToEndAsync(object? body = null) => await WaitFinishedAsync(await StartAsync(body));

    private static string Outcome(JsonElement run) => run.GetProperty("outcome").GetString()!;
    private static int Counter(JsonElement run, string name) => run.GetProperty("counters").GetProperty(name).GetInt32();

    private Task<JsonElement> ListAdviceAsync(string query = "status=open") => _client.GetFromJsonAsync<JsonElement>($"/api/dream/advice?{query}");

    private async Task<List<JsonElement>> AllEventsAsync(Guid id)
    {
        var page = await _client.GetFromJsonAsync<JsonElement>($"/api/dream/runs/{id}/events?limit=1000");
        return page.GetProperty("items").EnumerateArray().ToList();
    }

    // ── Start, the feed, stop ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheFeed_ShowsAdviceOneAtATimeInSeqOrder_BestFirst_AndStopEndsTheRun()
    {
        await ReplaceNodeAsync(new DreamEngineTestNode(new DreamEngineOptions { EmitPause = TimeSpan.FromMilliseconds(400) }));
        for (var i = 0; i < 8; i++) await NewArticleAsync($"Note {i}");
        DetectMoves(40, 90, 50, 80, 60, 70, 45, 85);

        var id = await StartAsync();

        var seen = new List<JsonElement>();
        var batches = new List<int>();
        var after = 0L;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (seen.Count < 3 && DateTime.UtcNow < deadline)
        {
            var feed = await _client.GetFromJsonAsync<JsonElement>($"/api/dream/runs/{id}/advice?afterSeq={after}");
            var items = feed.GetProperty("items").EnumerateArray().ToList();
            if (items.Count > 0)
            {
                batches.Add(items.Count);
                seen.AddRange(items);
                feed.GetProperty("lastSeq").GetInt64().Should().Be(items[^1].GetProperty("seq").GetInt64());
                after = feed.GetProperty("lastSeq").GetInt64();
                feed.GetProperty("activity").GetString().Should().NotBeNullOrEmpty();
            }
            feed.GetProperty("run").GetProperty("id").GetGuid().Should().Be(id);
            await Task.Delay(20);
        }

        batches.Should().OnlyContain(n => n == 1, "with a pause between writes a quick poll sees one new advice at a time");
        var seqs = seen.Select(a => a.GetProperty("seq").GetInt64()).ToList();
        seqs.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        seen.Select(a => a.GetProperty("score").GetInt32()).Should().Equal(90, 85, 80);

        var status = await _client.GetFromJsonAsync<JsonElement>("/api/dream/status");
        status.GetProperty("activeRun").GetProperty("id").GetGuid().Should().Be(id);
        status.GetProperty("activeRun").GetProperty("trigger").GetString().Should().Be("manual");

        (await _client.PostAsync($"/api/dream/runs/{id}/stop", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var run = await WaitFinishedAsync(id);

        Outcome(run).Should().Be("stopped");
        Counter(run, "adviceCreated").Should().BeLessThan(8);
        (await _client.PostAsync($"/api/dream/runs/{id}/stop", null)).StatusCode.Should().Be(HttpStatusCode.Accepted, "a second stop is harmless");

        var after2 = await _client.GetFromJsonAsync<JsonElement>("/api/dream/status");
        after2.GetProperty("activeRun").ValueKind.Should().Be(JsonValueKind.Null);
        after2.GetProperty("lastRun").GetProperty("id").GetGuid().Should().Be(id);

        var history = await _client.GetFromJsonAsync<JsonElement>("/api/dream/runs?page=1&pageSize=10&mode=all");
        history.GetProperty("total").GetInt32().Should().Be(1);
        history.GetProperty("items")[0].GetProperty("outcome").GetString().Should().Be("stopped");

        InScope(sp => sp.GetRequiredService<IDbConnectionFactory>().CreateConnection()
            .ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_audit_log WHERE entity_type = 'dream' AND action IN ('dream_run_started', 'dream_run_stopped')"))
            .Should().Be(3, "a start and two stops");
    }

    [Fact]
    public async Task ASlowDetector_IsCutShortByStop()
    {
        var article = await NewArticleAsync("Slow");
        _node.Script.Detect = (corpus, _, ct) => Slow(corpus, ct);

        static async IAsyncEnumerable<DreamAdviceDraft> Slow(IDreamCorpus corpus, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return MoveDraft(corpus.Articles[0], 90);
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            yield return MoveDraft(corpus.Articles[0], 91);
        }

        var id = await StartAsync();
        await _node.Script.DetectorStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        (await _client.PostAsync($"/api/dream/runs/{id}/stop", null)).EnsureSuccessStatusCode();
        var run = await WaitFinishedAsync(id);

        Outcome(run).Should().Be("stopped");
        article.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task ASecondStart_Answers409AlreadyRunning()
    {
        await NewArticleAsync("One");
        _node.Script.Detect = (_, _, ct) => Forever(ct);

        var id = await StartAsync();
        await _node.Script.DetectorStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await _client.PostAsJsonAsync("/api/dream/runs", new { });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be(DreamErrorCodes.AlreadyRunning);

        var operations = await _client.GetFromJsonAsync<JsonElement>("/api/admin/operations");
        operations.ToString().Should().Contain(DreamNames.LongOperationKey);

        await _client.PostAsync($"/api/dream/runs/{id}/stop", null);
        await WaitFinishedAsync(id);
    }

    private static async IAsyncEnumerable<DreamAdviceDraft> Forever([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), ct);
        yield break;
    }

    [Fact]
    public async Task TheDetector_RunsUnderTheSystemScope_OutsideTheRequest()
    {
        await NewArticleAsync("One");
        await RunToEndAsync();
        _node.Script.ScopeSeen.Should().BeOfType<SystemCallerScope>();
    }

    [Fact]
    public async Task TheRealRegistrations_FinishARun()
    {
        await ReplaceNodeAsync(new DreamEngineTestNode(keepRealServices: true));
        await NewArticleAsync("One");

        var run = await RunToEndAsync();

        Outcome(run).Should().Be("completed", run.ToString());
        Counter(run, "articlesInScope").Should().Be(1);
    }

    // ── What ends a run ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LockingTheVault_EndsARunInProgress_WithLocked()
    {
        await NewArticleAsync("One");
        _node.Script.Detect = (_, _, ct) => Forever(ct);

        var id = await StartAsync();
        await _node.Script.DetectorStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Session.Lock();
        var run = await WaitFinishedAsync(id);

        Outcome(run).Should().Be("locked");
        (await Session.UnlockAsync(Password)).Should().BeTrue();
    }

    [Fact]
    public async Task OnASealedNode_ARunEndedByALock_StillSaysLocked_AfterTheNextUnlock()
    {
        await ReplaceNodeAsync(new DreamEngineTestNode(), encrypted: true);
        await NewArticleAsync("One");
        _node.Script.Detect = (_, _, ct) => Forever(ct);

        var id = await StartAsync();
        await _node.Script.DetectorStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Session.Lock();
        await Coordinator.WaitForIdleAsync();
        (await Session.UnlockAsync(Password)).Should().BeTrue();
        await Scheduler.StepAsync(CancellationToken.None);

        Outcome(await GetRunAsync(id)).Should().Be("locked", "chat.db closed with the vault, and the outcome is written once it is open again");
    }

    [Fact]
    public async Task APendingNote_GoesThroughTheEmbeddingsPhase_AndTheRunStillFinishes()
    {
        using (var scope = _node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("Waiting", "/Notes", [], "body");

        var run = await RunToEndAsync();

        Outcome(run).Should().Be("completed", run.ToString());
        (await AllEventsAsync(run.GetProperty("id").GetGuid())).Select(e => e.GetProperty("text").GetString())
            .Should().Contain(t => t!.StartsWith("Preparing vectors", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhileLocked_TheFeedStillAnswersWithTheRun()
    {
        await NewArticleAsync("One");
        DetectMoves(80);
        var id = (await RunToEndAsync()).GetProperty("id").GetGuid();

        Session.Lock();
        var feed = await _client.GetFromJsonAsync<JsonElement>($"/api/dream/runs/{id}/advice?afterSeq=0");
        feed.GetProperty("items").GetArrayLength().Should().Be(0);
        feed.GetProperty("run").GetProperty("state").GetString().Should().Be("finished");

        (await _client.GetAsync("/api/dream/advice")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Session.UnlockAsync(Password)).Should().BeTrue();
    }

    [Fact]
    public async Task AHeavyOperation_EndsARunInProgress_WithHeavyOperation()
    {
        await NewArticleAsync("One");
        var hold = new TaskCompletionSource();
        _node.Script.Detect = (corpus, _, ct) => HoldThenMove(corpus, hold, ct);

        var id = await StartAsync();
        await _node.Script.DetectorStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await HeavyOperationLock.Instance.WaitAsync();
        try
        {
            hold.SetResult();
            Outcome(await WaitFinishedAsync(id)).Should().Be("heavy_operation");
        }
        finally
        {
            HeavyOperationLock.Instance.Release();
        }
    }

    private static async IAsyncEnumerable<DreamAdviceDraft> HoldThenMove(IDreamCorpus corpus, TaskCompletionSource hold, [EnumeratorCancellation] CancellationToken ct)
    {
        await hold.Task.WaitAsync(ct);
        yield return MoveDraft(corpus.Articles[0], 90);
    }

    [Fact]
    public async Task AHeavyOperation_RefusesAStart_With409Busy()
    {
        await HeavyOperationLock.Instance.WaitAsync();
        try
        {
            var response = await _client.PostAsJsonAsync("/api/dream/runs", new { });
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be(DreamErrorCodes.Busy);
        }
        finally
        {
            HeavyOperationLock.Instance.Release();
        }
    }

    [Fact]
    public async Task TheAdviceLimit_KeepsTheBestAndEndsWithAdviceLimit()
    {
        for (var i = 0; i < 5; i++) await NewArticleAsync($"Note {i}");
        DetectMoves(50, 90, 60, 80, 70);

        var run = await RunToEndAsync(new { maxAdvice = 2 });

        Outcome(run).Should().Be("advice_limit");
        Counter(run, "adviceCreated").Should().Be(2);
        var open = await ListAdviceAsync();
        open.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("score").GetInt32()).Should().Equal(90, 80);
    }

    [Fact]
    public async Task TheTimeLimit_EndsARunBetweenAdvice()
    {
        var clock = new ManualDreamClock(DateTimeOffset.UtcNow);
        await ReplaceNodeAsync(new DreamEngineTestNode(new DreamEngineOptions { Clock = clock }));
        for (var i = 0; i < 3; i++) await NewArticleAsync($"Note {i}");
        _node.Script.Detect = (corpus, _, ct) => Advancing(corpus, clock, ct);

        static async IAsyncEnumerable<DreamAdviceDraft> Advancing(IDreamCorpus corpus, ManualDreamClock clock, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var article in corpus.Articles)
            {
                ct.ThrowIfCancellationRequested();
                yield return MoveDraft(article, 80);
                clock.Advance(TimeSpan.FromMinutes(4));
            }
        }

        var run = await RunToEndAsync(new { maxMinutes = 5 });

        Outcome(run).Should().Be("time_limit", "the detector used up twelve minutes of a five-minute limit");
        run.GetProperty("limits").GetProperty("maxMinutes").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task TheTokenBudget_EndsARunBeforeItOverspends()
    {
        await AddTextModelAsync();
        await SetSettingsAsync(s => s with { MaxTokens = 2000 });
        var articles = new List<Article>();
        for (var i = 0; i < 8; i++) articles.Add(await NewArticleAsync($"Note {i}"));
        _node.Script.Pairs = _ => articles.Skip(1).Select(a => (articles[0].Id, a.Id)).ToList();
        _node.Model.PromptTokens = 800;
        _node.Model.CompletionTokens = 100;

        var run = await RunToEndAsync(new { useModel = true, phases = new[] { "pairs" } });

        Outcome(run).Should().Be("budget_limit", run.ToString());
        Counter(run, "modelCalls").Should().BeLessThan(7);
        (Counter(run, "promptTokens") + Counter(run, "completionTokens")).Should().BeLessThanOrEqualTo(2000 + 900);
        run.GetProperty("limits").GetProperty("maxTokens").GetInt64().Should().Be(2000);
    }

    [Fact]
    public async Task ProviderFailure_EndsARunWithProviderError_AndTheMessageStaysClean()
    {
        await AddTextModelAsync();
        var a = await NewArticleAsync("A");
        var b = await NewArticleAsync("B");
        _node.Script.Pairs = _ => [(a.Id, b.Id)];
        _node.Script.Examine = (_, _, _) => throw new DreamProviderException("Every key failed.");

        var run = await RunToEndAsync(new { useModel = true, phases = new[] { "pairs" } });

        Outcome(run).Should().Be("provider_error");
        run.GetProperty("errorText").GetString().Should().NotContain("Every key failed", "the exception's own text stays in the log");
    }

    [Fact]
    public async Task ADetectorThatThrows_CountsAsAnError_AndTheRunGoesOn()
    {
        await NewArticleAsync("One");
        _node.Script.Detect = (_, _, _) => throw new InvalidOperationException("boom");

        var run = await RunToEndAsync();

        Outcome(run).Should().Be("completed");
        Counter(run, "errors").Should().Be(1);
    }

    [Fact]
    public async Task AnAbandonedRun_BecomesInterrupted_ButAFreshRowIsLeftForItsOwner()
    {
        var stale = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        using (var conn = ChatDb())
        {
            const string sql = """
                INSERT INTO dream_run (id, "trigger", mode, state, created_at, heartbeat_at)
                VALUES (@id, 'manual', 'normal', 'running', @at, @at)
                """;
            conn.Execute(sql, new { id = stale.ToString("D"), at = DateTime.UtcNow.AddHours(-1).ToString("o") });
            conn.Execute(sql, new { id = fresh.ToString("D"), at = DateTime.UtcNow.ToString("o") });
        }

        await Scheduler.StepAsync(CancellationToken.None);

        var interrupted = await GetRunAsync(stale);
        interrupted.GetProperty("state").GetString().Should().Be("finished");
        Outcome(interrupted).Should().Be("interrupted");
        (await GetRunAsync(fresh)).GetProperty("state").GetString().Should().Be("running");
    }

    // ── What the engine does with a proposal ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARepeat_OfAnOpenAdvice_IsSuppressed_AndAClosedOneIsNot()
    {
        await NewArticleAsync("One");
        DetectMoves(80);

        var first = await RunToEndAsync();
        Counter(first, "adviceCreated").Should().Be(1);

        var second = await RunToEndAsync();
        Counter(second, "adviceCreated").Should().Be(0);
        Counter(second, "adviceSuppressed").Should().Be(1);

        var advice = (await ListAdviceAsync()).GetProperty("items")[0].GetProperty("id").GetGuid();
        await WithStoreAsync(store => store.TrySetAdviceStatusAsync(advice, [DreamAdviceStatus.New], DreamAdviceStatus.Applied, null, 1, DateTime.UtcNow));

        Counter(await RunToEndAsync(), "adviceCreated").Should().Be(1, "only new and in_chat advice count as repeats");
    }

    [Fact]
    public async Task ADismissRule_SuppressesWhatItCovers()
    {
        await NewArticleAsync("One");
        var other = await NewArticleAsync("Other");
        DetectMoves(80, 75);
        await WithStoreAsync(store => store.CreateRuleAsync(new DreamRuleRecord
        {
            Id = Guid.NewGuid(),
            Scope = DreamRuleScope.Object,
            Kind = DreamAdviceKind.MoveArticle,
            CreatedAt = DateTime.UtcNow,
            Payload = new DreamRulePayload(ObjectId: other.Id),
        }));

        var run = await RunToEndAsync();

        Counter(run, "adviceCreated").Should().Be(1);
        Counter(run, "adviceSuppressed").Should().Be(1);
    }

    [Fact]
    public async Task AnExpiredRule_NoLongerSuppresses()
    {
        await NewArticleAsync("One");
        DetectMoves(80);
        await WithStoreAsync(store => store.CreateRuleAsync(new DreamRuleRecord
        {
            Id = Guid.NewGuid(),
            Scope = DreamRuleScope.Kind,
            Kind = DreamAdviceKind.MoveArticle,
            CreatedAt = DateTime.UtcNow.AddDays(-200),
            ExpiresAt = DateTime.UtcNow.AddDays(-20),
            Payload = new DreamRulePayload(),
        }));

        Counter(await RunToEndAsync(), "adviceCreated").Should().Be(1);
    }

    [Fact]
    public async Task AdviceBelowMinScore_IsNotKept()
    {
        await NewArticleAsync("One");
        await NewArticleAsync("Two");
        DetectMoves(39, 40);

        Counter(await RunToEndAsync(), "adviceCreated").Should().Be(1, "the default minimum is 40");
    }

    [Fact]
    public async Task AProposalNamingAnArticleOutsideTheRun_IsRefused()
    {
        await NewArticleAsync("One");
        var outsider = new DreamArticle(Guid.NewGuid(), "Outsider", "/Notes", null, 1, DateTime.UtcNow, DateTime.UtcNow, false);
        _node.Script.Detect = (corpus, _, ct) => Yield(new[] { MoveDraft(outsider, 90), MoveDraft(corpus.Articles[0], 80) }, ct);

        var run = await RunToEndAsync();

        Counter(run, "adviceCreated").Should().Be(1);
        Counter(run, "errors").Should().Be(1);
    }

    [Fact]
    public async Task AProposalThatMisusesItsKind_IsRefused()
    {
        await NewArticleAsync("One");
        _node.Script.Detect = (corpus, _, ct) => Yield(new[]
        {
            MoveDraft(corpus.Articles[0], 90) with { ApplyMode = DreamApplyMode.Prompt },
            MoveDraft(corpus.Articles[0], 90) with { Action = DreamAction.Note() },
            MoveDraft(corpus.Articles[0], 101),
            MoveDraft(corpus.Articles[0], 90) with { Title = " " },
        }, ct);

        var run = await RunToEndAsync();

        Counter(run, "adviceCreated").Should().Be(0);
        Counter(run, "errors").Should().Be(4);
    }

    [Fact]
    public async Task AModelVerdict_ExpiresTheOpenNearDuplicateOfThePair_AsSuperseded()
    {
        await AddTextModelAsync();
        var a = await NewArticleAsync("A");
        var b = await NewArticleAsync("B");
        var near = Guid.NewGuid();
        await WithStoreAsync(store => store.InsertAdviceAsync(new DreamAdviceRecord
        {
            Id = near,
            RunId = Guid.NewGuid(),
            Kind = DreamAdviceKind.NearDuplicate,
            Source = DreamAdviceSource.Rule,
            ApplyMode = DreamApplyMode.Chat,
            Score = 60,
            CreatedAt = DateTime.UtcNow,
            Payload = new DreamAdvicePayload
            {
                Fingerprint = "feed",
                FamilyKey = DreamNames.PairFamilyKey(a.Id, b.Id),
                Title = "Near",
                Summary = "Near",
                Action = DreamAction.ReviewPair(a.Id, b.Id),
            },
        }));
        _node.Script.Pairs = _ => [(a.Id, b.Id)];

        await RunToEndAsync(new { useModel = true, phases = new[] { "pairs" } });

        var old = await _client.GetFromJsonAsync<JsonElement>($"/api/dream/advice/{near}");
        old.GetProperty("status").GetString().Should().Be("expired");
        old.GetProperty("expiredReason").GetString().Should().Be(DreamStatusNotes.Superseded);
    }

    // ── The registry and trial runs ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheRegistry_MeansTheSameCandidateIsNotPaidForTwice_UntilItChanges()
    {
        await AddTextModelAsync();
        var a = await NewArticleAsync("A");
        var b = await NewArticleAsync("B");
        _node.Script.Pairs = _ => [(a.Id, b.Id)];

        await RunToEndAsync(new { useModel = true, phases = new[] { "pairs" } });
        _node.Model.Requests.Should().HaveCount(1);

        await RunToEndAsync(new { useModel = true, phases = new[] { "pairs" } });
        _node.Model.Requests.Should().HaveCount(1, "already examined, same content, same prompt");

        using (var scope = _node.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection()
                .Execute("UPDATE tbl_article SET lamport_ts = lamport_ts + 1 WHERE id = @id", new { id = a.Id });

        await RunToEndAsync(new { useModel = true, phases = new[] { "pairs" } });
        _node.Model.Requests.Should().HaveCount(2, "the article changed");
    }

    [Fact]
    public async Task ATrialRun_NeitherReadsNorWritesTheRegistry_AndStaysOutOfTheOpenList()
    {
        await AddTextModelAsync();
        var a = await NewArticleAsync("A");
        var b = await NewArticleAsync("B");
        _node.Script.Pairs = _ => [(a.Id, b.Id)];

        var trial = await RunToEndAsync(new { mode = "trial", useModel = true, phases = new[] { "pairs" } });
        var trialId = trial.GetProperty("id").GetGuid();

        Counter(trial, "adviceCreated").Should().Be(1);
        using (var conn = ChatDb())
            conn.ExecuteScalar<long>("SELECT COUNT(*) FROM dream_examined").Should().Be(0);
        (await ListAdviceAsync()).GetProperty("total").GetInt32().Should().Be(0, "trial advice is not in the shared list");
        (await _client.GetFromJsonAsync<JsonElement>("/api/dream/status")).GetProperty("openAdvice").GetProperty("total").GetInt32().Should().Be(0);

        var feed = await _client.GetFromJsonAsync<JsonElement>($"/api/dream/runs/{trialId}/advice?afterSeq=0");
        feed.GetProperty("items")[0].GetProperty("trial").GetBoolean().Should().BeTrue("its own run still shows it");
        feed.GetProperty("items")[0].GetProperty("can").GetProperty("chat").GetBoolean().Should().BeTrue("trial advice can be acted on");

        await RunToEndAsync(new { mode = "trial", useModel = true, phases = new[] { "pairs" } });
        _node.Model.Requests.Should().HaveCount(2, "a trial does not read the registry");

        var normal = await RunToEndAsync(new { useModel = true, phases = new[] { "pairs" } });
        Counter(normal, "adviceCreated").Should().Be(1, "a trial's advice does not make a normal one a repeat");
    }

    [Fact]
    public async Task ASample_IsTheSameForTheSameSeed_AndHonoursItsCap()
    {
        await AddTextModelAsync();
        var articles = new List<Article>();
        for (var i = 0; i < 7; i++) articles.Add(await NewArticleAsync($"Note {i}"));
        _node.Script.Pairs = _ => articles.Skip(1).Select(a => (articles[0].Id, a.Id)).ToList();

        var body = new { mode = "trial", useModel = true, phases = new[] { "pairs" }, sample = new { seed = 7, pairs = 3 } };
        await RunToEndAsync(body);
        var first = _node.Model.Requests.Select(r => r.User).ToList();
        first.Should().HaveCount(3);

        await RunToEndAsync(body);
        _node.Model.Requests.Skip(3).Select(r => r.User).Should().Equal(first);
    }

    // ── What Dream may read ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AProtectedNote_AppearsInNoAdviceEventOrModelRequest()
    {
        const string canary = "CANARY-PROTECTED-TEXT-7f3a";
        await AddTextModelAsync();
        var plain = await NewArticleAsync("Plain note", body: "ordinary words");
        var secret = await NewArticleAsync("Secret note", body: canary);
        using (var scope = _node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ArticleService>().ProtectAsync(secret.Id, "second-password-1", null);

        string? readThroughCorpus = "not called";
        _node.Script.Detect = (corpus, _, ct) => ReadEverything(corpus, secret.Id, value => readThroughCorpus = value, ct);
        _node.Script.Pairs = corpus => corpus.Articles.Count >= 2 ? [(corpus.Articles[0].Id, corpus.Articles[1].Id)] : [];

        var run = await RunToEndAsync(new { useModel = true });

        readThroughCorpus.Should().BeNull("the corpus refuses to read a protected article even when asked by id");
        Counter(run, "articlesInScope").Should().Be(1, "a protected article is not in the corpus");

        var everything = new List<string>();
        everything.AddRange((await AllEventsAsync(run.GetProperty("id").GetGuid())).Select(e => e.ToString()));
        everything.Add((await ListAdviceAsync("status=open&pageSize=100")).ToString());
        everything.AddRange(_node.Model.Requests.Select(r => r.System + r.User));
        everything.Should().NotContain(text => text.Contains(canary, StringComparison.Ordinal));
        plain.Id.Should().NotBe(Guid.Empty);
    }

    private static async IAsyncEnumerable<DreamAdviceDraft> ReadEverything(IDreamCorpus corpus, Guid secretId, Action<string?> report, [EnumeratorCancellation] CancellationToken ct)
    {
        report(await corpus.ReadBodyAsync(secretId, ct));
        foreach (var article in corpus.Articles)
        {
            var body = await corpus.ReadBodyAsync(article.Id, ct);
            yield return MoveDraft(article, 80) with { Summary = $"It says: {body}" };
        }
    }

    [Fact]
    public async Task TheCorpus_RefusesAWrappedBody_EvenWhenTheFlagIsClear()
    {
        var note = await NewArticleAsync("Note", body: "secret words");
        using (var scope = _node.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ArticleService>().ProtectAsync(note.Id, "second-password-1", null);
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection()
                .Execute("UPDATE tbl_article SET protected = 0, embedding_projection = @v, embedding_pending = 0 WHERE id = @id", new { v = Vector, id = note.Id });
        }

        string? body = "not called";
        _node.Script.Detect = (corpus, _, ct) => ReadFirst(corpus, value => body = value, ct);
        var run = await RunToEndAsync();

        Counter(run, "articlesInScope").Should().Be(1, "the flag was cleared, so the metadata filter lets it through");
        body.Should().BeNull("the second check looks at the content itself");
    }

    private static async IAsyncEnumerable<DreamAdviceDraft> ReadFirst(IDreamCorpus corpus, Action<string?> report, [EnumeratorCancellation] CancellationToken ct)
    {
        report(await corpus.ReadBodyAsync(corpus.Articles[0].Id, ct));
        yield break;
    }

    [Fact]
    public async Task AnExcludedFolder_IsNeitherReadNorNamed()
    {
        const string canary = "CANARY-EXCLUDED-9c1d";
        await NewArticleAsync("Open note", "/Open");
        await NewArticleAsync($"Hidden {canary}", "/Private/Deep", tags: "hidden-only-tag");

        var put = await _client.PutAsJsonAsync("/api/dream/settings", SettingsBody(s => s with { ExcludedFolderPaths = ["/Private"] }));
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        IDreamCorpus? seen = null;
        _node.Script.Detect = (corpus, _, ct) =>
        {
            seen = corpus;
            return Yield(Array.Empty<DreamAdviceDraft>(), ct);
        };
        await RunToEndAsync();

        seen!.Articles.Should().ContainSingle().Which.Title.Should().Be("Open note");
        seen.Folders.Select(f => f.Path).Should().NotContain(p => p.StartsWith("/Private", StringComparison.Ordinal));
        seen.Tags.Select(t => t.Name).Should().NotContain("hidden-only-tag", "a tag only fenced-off notes carry is not named");
    }

    [Fact]
    public async Task ANoteMovedIntoAnExcludedFolder_AfterTheCorpusWasBuilt_IsNeitherReadNorNamed()
    {
        const string title = "Mover CANARY-MOVED-TITLE-4b2e";
        const string body = "CANARY-MOVED-BODY-4b2e";
        var stay = await NewArticleAsync("Stay", "/Open");
        var mover = await NewArticleAsync(title, "/Open", body);
        await NewArticleAsync("Fenced", "/Private");
        await SetSettingsAsync(s => s with { ExcludedFolderPaths = ["/Private"] });

        string? read = "not called";
        var listedAfter = true;
        var corpusHadIt = false;
        _node.Script.Detect = (corpus, ctx, ct) => MoveThenRead(corpus, ctx, ct);

        async IAsyncEnumerable<DreamAdviceDraft> MoveThenRead(IDreamCorpus corpus, DreamRunContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            var before = corpus.Articles.Single(a => a.Id == mover.Id);
            corpusHadIt = true;
            using (var scope = _node.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<ArticleService>().MoveAsync(mover.Id, "/Private/Inner");

            read = await corpus.ReadBodyAsync(mover.Id, ct);
            listedAfter = corpus.Articles.Any(a => a.Id == mover.Id);
            await ctx.Log.WriteAsync(DreamEventKind.Info, $"Looked at {before.Title}", [mover.Id], null, ct);
            yield return MoveDraft(before, 90);
            yield return MoveDraft(corpus.Articles.Single(a => a.Id == stay.Id), 80);
        }

        var run = await RunToEndAsync();

        corpusHadIt.Should().BeTrue("the corpus was built before the move");
        read.Should().BeNull("the article is under an excluded folder by the time its body is asked for");
        listedAfter.Should().BeFalse("an article found out of reach is withdrawn from the run");
        Counter(run, "adviceCreated").Should().Be(1, "only the article that stayed gets advice");

        var events = await AllEventsAsync(run.GetProperty("id").GetGuid());
        events.Select(e => e.GetProperty("text").GetString()).Should().Contain(DreamRunner.WithdrawnNote);
        var written = string.Join("\n", events.Select(e => e.ToString())) + (await ListAdviceAsync("status=open&pageSize=100"));
        written.Should().NotContain("CANARY-MOVED").And.NotContain(mover.Id.ToString("D"), "nothing names the note that went out of reach");
    }

    [Fact]
    public async Task ANoteProtectedDuringTheRun_IsNotNamedByAnyAdviceOrEvent()
    {
        var keep = await NewArticleAsync("Keep");
        var secret = await NewArticleAsync("Soon secret CANARY-PROT-TITLE-91e0");
        _node.Script.Detect = (corpus, ctx, ct) => ProtectThenPropose(corpus, ctx, ct);

        async IAsyncEnumerable<DreamAdviceDraft> ProtectThenPropose(IDreamCorpus corpus, DreamRunContext ctx, [EnumeratorCancellation] CancellationToken ct)
        {
            var before = corpus.Articles.Single(a => a.Id == secret.Id);
            using (var scope = _node.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<ArticleService>().ProtectAsync(secret.Id, "second-password-1", null);

            // No body read here: the advice and its journal line are checked on their own before they are written.
            yield return MoveDraft(before, 90);
            yield return MoveDraft(corpus.Articles.Single(a => a.Id == keep.Id), 80);
            await ctx.Log.WriteAsync(DreamEventKind.Info, $"Done with {before.Title}", [secret.Id], null, ct);
        }

        var run = await RunToEndAsync();

        Counter(run, "adviceCreated").Should().Be(1);
        var events = await AllEventsAsync(run.GetProperty("id").GetGuid());
        var written = string.Join("\n", events.Select(e => e.ToString())) + (await ListAdviceAsync("status=open&pageSize=100"));
        written.Should().NotContain("CANARY-PROT").And.NotContain(secret.Id.ToString("D"));
        events.Count(e => e.GetProperty("text").GetString() == DreamRunner.WithdrawnNote).Should().Be(2, "the dropped advice and the journal line");
    }

    [Fact]
    public async Task EmbeddingCoverage_CountsTheSameScopeAsTheCorpus()
    {
        await NewArticleAsync("Has a vector", "/Open");
        using (var scope = _node.Services.CreateScope())
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            await articles.CreateAsync("No vector 1", "/Private", [], "body");
            await articles.CreateAsync("No vector 2", "/Private/Deeper", [], "body");
        }

        var before = (await _client.GetFromJsonAsync<JsonElement>("/api/dream/status")).GetProperty("embeddings");
        before.GetProperty("total").GetInt32().Should().Be(3);
        before.GetProperty("pending").GetInt32().Should().Be(2);

        await SetSettingsAsync(s => s with { ExcludedFolderPaths = ["/Private"] });

        var after = (await _client.GetFromJsonAsync<JsonElement>("/api/dream/status")).GetProperty("embeddings");
        after.GetProperty("total").GetInt32().Should().Be(1, "an excluded subtree is not Dream's to count");
        after.GetProperty("pending").GetInt32().Should().Be(0);

        var coverage = await InScope(async sp =>
        {
            var settings = await sp.GetRequiredService<DreamStore>().GetSettingsAsync();
            return await sp.GetRequiredService<DreamCorpusBuilder>().GetEmbeddingCoverageAsync(settings);
        });
        coverage.Should().Be(new DreamEmbeddingCoverage(1, 0, 0), "the night check sees the same numbers: nothing in scope lacks a vector");
    }

    [Fact]
    public async Task ATagOnlyAProtectedNoteCarries_DoesNotLookUnused()
    {
        var note = await NewArticleAsync("Tagged", tags: "kept-tag");
        using (var scope = _node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ArticleService>().ProtectAsync(note.Id, "second-password-1", null);

        IDreamCorpus? seen = null;
        _node.Script.Detect = (corpus, _, ct) =>
        {
            seen = corpus;
            return Yield(Array.Empty<DreamAdviceDraft>(), ct);
        };
        await RunToEndAsync();

        seen!.Tags.Select(t => t.Name).Should().NotContain("kept-tag");
    }

    // ── Storage: encryption, the sweep, re-key, reset ────────────────────────────────────────────────────────────

    [Fact]
    public async Task NothingThatNamesANote_IsInAPlaintextColumn()
    {
        const string canary = "CANARY-TITLE-55aa";
        await NewArticleAsync(canary, tags: "visible-tag");
        DetectMoves(80);
        await RunToEndAsync();

        using var conn = ChatDb();
        foreach (var table in new[] { "dream_advice", "dream_run_event", "dream_run", "dream_examined", "dream_rule" })
        {
            var rows = conn.Query($"SELECT * FROM {table}").Cast<IDictionary<string, object?>>().ToList();
            foreach (var row in rows)
                foreach (var (column, value) in row)
                    if (value is string text)
                        text.Should().NotContain(canary, $"{table}.{column} is plaintext");
        }
    }

    private async Task<Guid> SeedAdviceNamingAsync(Guid articleId, string fingerprint = "f1")
    {
        var id = Guid.NewGuid();
        await WithStoreAsync(store => store.InsertAdviceAsync(new DreamAdviceRecord
        {
            Id = id,
            RunId = Guid.NewGuid(),
            Kind = DreamAdviceKind.MoveArticle,
            Source = DreamAdviceSource.Rule,
            ApplyMode = DreamApplyMode.Code,
            Score = 70,
            CreatedAt = DateTime.UtcNow,
            Payload = new DreamAdvicePayload
            {
                Fingerprint = fingerprint,
                Title = "T",
                Summary = "S",
                Action = DreamAction.MoveArticle(articleId, "/Target"),
            },
        }));
        return id;
    }

    [Fact]
    public async Task AdviceUnderAForeignKey_IsDeletedByTheSweep_AndTheRestStays()
    {
        var note = await NewArticleAsync("Note");
        var good = await SeedAdviceNamingAsync(note.Id);
        var foreign = await SeedAdviceNamingAsync(note.Id, "f2");

        var otherKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var (cipher, iv) = ArticleEncryptor.Encrypt("{\"fingerprint\":\"x\"}", otherKey, DreamAad.Advice(foreign));
        using (var conn = ChatDb())
            conn.Execute("UPDATE dream_advice SET payload_ciphertext = @cipher, payload_iv = @iv WHERE id = @id", new { cipher, iv, id = foreign.ToString("D") });

        await Scheduler.StepAsync(CancellationToken.None);

        using (var conn = ChatDb())
            conn.Query<string>("SELECT id FROM dream_advice").Should().ContainSingle().Which.Should().Be(good.ToString("D"));
        (await _client.GetAsync($"/api/dream/advice/{good}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WhileLocked_TheSweepJudgesNoRowUnreadable()
    {
        var note = await NewArticleAsync("Note");
        var advice = await SeedAdviceNamingAsync(note.Id);

        Session.Lock();
        await Scheduler.StepAsync(CancellationToken.None);
        (await Session.UnlockAsync(Password)).Should().BeTrue();

        using var conn = ChatDb();
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM dream_advice WHERE id = @id", new { id = advice.ToString("D") }).Should().Be(1);
    }

    [Fact]
    public async Task TheSweep_RemovesWhatNamesAHardDeletedArticle_ButKeepsATrashedOne()
    {
        var trashed = await NewArticleAsync("Trashed");
        var keptAdvice = await SeedAdviceNamingAsync(trashed.Id);
        using (var scope = _node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ArticleService>().DeleteAsync(trashed.Id);

        var gone = await SeedAdviceNamingAsync(Guid.NewGuid(), "f2");
        var run = Guid.NewGuid();
        await WithStoreAsync(store => store.InsertEventAsync(run, DreamEventKind.Advice, new DreamEventPayload("t", [Guid.NewGuid()], null), DateTime.UtcNow));

        await Scheduler.StepAsync(CancellationToken.None);

        using var conn = ChatDb();
        conn.Query<string>("SELECT id FROM dream_advice").Should().ContainSingle().Which.Should().Be(keptAdvice.ToString("D"));
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM dream_run_event").Should().Be(0);
        gone.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task TheSweep_ReturnsStaleApplying_AndAgesTheJournal()
    {
        var note = await NewArticleAsync("Note");
        var stuck = await SeedAdviceNamingAsync(note.Id);
        var run = Guid.NewGuid();
        await WithStoreAsync(store => store.InsertEventAsync(run, DreamEventKind.Info, new DreamEventPayload("old", [], null), DateTime.UtcNow.AddDays(-8)));
        await WithStoreAsync(store => store.InsertEventAsync(run, DreamEventKind.Info, new DreamEventPayload("new", [], null), DateTime.UtcNow));
        using (var conn = ChatDb())
            conn.Execute("UPDATE dream_advice SET status = 'applying', status_at = @at WHERE id = @id",
                new { at = DateTime.UtcNow.AddMinutes(-11).ToString("o"), id = stuck.ToString("D") });

        await Scheduler.StepAsync(CancellationToken.None);

        (await _client.GetFromJsonAsync<JsonElement>($"/api/dream/advice/{stuck}")).GetProperty("status").GetString().Should().Be("new");
        var events = await _client.GetFromJsonAsync<JsonElement>($"/api/dream/runs/{await CreateRunRowAsync(run)}/events");
        events.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("text").GetString()).Should().Equal("new");
    }

    private async Task<Guid> CreateRunRowAsync(Guid id)
    {
        await WithStoreAsync(store => store.CreateRunAsync(new DreamRunRecord
        {
            Id = id, Trigger = DreamRunTrigger.Manual, Mode = DreamRunMode.Normal, State = DreamRunState.Finished,
            Outcome = DreamRunOutcome.Completed, CreatedAt = DateTime.UtcNow,
        }));
        return id;
    }

    [Fact]
    public async Task AfterADekRotation_TheAdviceStillOpens()
    {
        await NewArticleAsync("Note");
        DetectMoves(80);
        await RunToEndAsync();

        var propose = await _client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password });
        propose.StatusCode.Should().Be(HttpStatusCode.OK, await propose.Content.ReadAsStringAsync());
        var commitEventId = (await propose.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("commitEventId").GetGuid().ToString();
        (await _client.PostAsJsonAsync("/api/dek-rotation/accept", new { commitEventId, masterPassword = Password })).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var progress = await _client.GetFromJsonAsync<JsonElement>("/api/dek-rotation/progress");
            var step = progress.GetProperty("currentStep").GetString();
            if (step == "Completed") break;
            step.Should().NotBe("Failed", progress.ToString());
            DateTime.UtcNow.Should().BeBefore(deadline, "the rotation must finish");
            await Task.Delay(100);
        }

        var list = await ListAdviceAsync();
        list.GetProperty("total").GetInt32().Should().Be(1);
        list.GetProperty("items")[0].GetProperty("title").GetString().Should().Be("Move Note");
    }

    [Fact]
    public async Task ANodeReset_EmptiesWhatARunWrote()
    {
        await NewArticleAsync("Note");
        DetectMoves(80);
        await RunToEndAsync();
        await SetSettingsAsync(s => s with { ScheduleEnabled = true });

        using (var scope = _node.Services.CreateScope())
            await scope.ServiceProvider.GetServices<INodeResetHook>().OfType<ApiStateResetHook>().Single().AfterVaultWipedAsync(CancellationToken.None);

        (await _client.GetFromJsonAsync<JsonElement>("/api/dream/runs")).GetProperty("total").GetInt32().Should().Be(0);
        (await ListAdviceAsync()).GetProperty("total").GetInt32().Should().Be(0);
        (await _client.GetFromJsonAsync<JsonElement>("/api/dream/settings")).GetProperty("scheduleEnabled").GetBoolean().Should().BeFalse();
    }

    // ── The schedule ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>03:10 UTC, in the default window, two days from now so that a setting saved just now is older than it.</summary>
    private static DateTimeOffset InWindow() => new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2), TimeSpan.Zero).AddHours(3).AddMinutes(10);

    private async Task<ManualDreamClock> ScheduledNodeAsync(bool scheduleOn = true)
    {
        var clock = new ManualDreamClock(InWindow());
        await ReplaceNodeAsync(new DreamEngineTestNode(new DreamEngineOptions { Clock = clock }));
        await NewArticleAsync("Note");
        await SetSettingsAsync(s => s with { ScheduleEnabled = scheduleOn });
        return clock;
    }

    [Fact]
    public async Task TheScheduler_StartsTheNightRunInItsWindow_OnceAWindow()
    {
        await ScheduledNodeAsync();
        DetectMoves(80);

        await Scheduler.StepAsync(CancellationToken.None);
        await Coordinator.WaitForIdleAsync();

        var runs = await _client.GetFromJsonAsync<JsonElement>("/api/dream/runs");
        runs.GetProperty("total").GetInt32().Should().Be(1);
        var night = runs.GetProperty("items")[0];
        night.GetProperty("trigger").GetString().Should().Be("schedule");
        night.GetProperty("limits").GetProperty("maxAdvice").GetInt32().Should().Be(10);
        night.GetProperty("limits").GetProperty("maxMinutes").GetInt32().Should().Be(110, "the rest of the window");

        await Scheduler.StepAsync(CancellationToken.None);
        await Coordinator.WaitForIdleAsync();
        (await _client.GetFromJsonAsync<JsonElement>("/api/dream/runs")).GetProperty("total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task TheScheduler_DoesNothing_WhenTheScheduleIsOff()
    {
        await ScheduledNodeAsync(scheduleOn: false);
        await Scheduler.StepAsync(CancellationToken.None);
        (await _client.GetFromJsonAsync<JsonElement>("/api/dream/runs")).GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task AWindowThatPasses_WhileTheOpenListIsFull_LeavesOneSkippedRecord()
    {
        var clock = await ScheduledNodeAsync();
        await SetSettingsAsync(s => s with { ScheduleEnabled = true, MaxOpenAdvice = 1 });
        var note = (await _client.GetFromJsonAsync<JsonElement>("/api/dream/runs")).GetProperty("total");
        note.GetInt32().Should().Be(0);
        await SeedAdviceNamingAsync(InScope(sp => sp.GetRequiredService<IDbConnectionFactory>().CreateConnection().ExecuteScalar<string>("SELECT id FROM tbl_article LIMIT 1")) is { } id ? Guid.Parse(id) : Guid.Empty);

        await Scheduler.StepAsync(CancellationToken.None);
        (await _client.GetFromJsonAsync<JsonElement>("/api/dream/runs")).GetProperty("total").GetInt32().Should().Be(0, "the window is still open");

        clock.Advance(TimeSpan.FromHours(3));
        await Scheduler.StepAsync(CancellationToken.None);
        await Scheduler.StepAsync(CancellationToken.None);

        var runs = await _client.GetFromJsonAsync<JsonElement>("/api/dream/runs");
        runs.GetProperty("total").GetInt32().Should().Be(1, "one record for one missed window");
        Outcome(runs.GetProperty("items")[0]).Should().Be("skipped");
        runs.GetProperty("items")[0].GetProperty("errorCode").GetString().Should().Be(DreamErrorCodes.OpenListFull);
    }

    [Fact]
    public async Task AWindowThatPassedBeforeTheScheduleWasTurnedOn_LeavesNoRecord()
    {
        var clock = new ManualDreamClock(DateTimeOffset.UtcNow.AddDays(-3));
        await ReplaceNodeAsync(new DreamEngineTestNode(new DreamEngineOptions { Clock = clock }));
        await SetSettingsAsync(s => s with { ScheduleEnabled = true });

        clock.Set(new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-2), TimeSpan.Zero).AddHours(6));
        await Scheduler.StepAsync(CancellationToken.None);

        (await _client.GetFromJsonAsync<JsonElement>("/api/dream/runs")).GetProperty("total").GetInt32().Should().Be(0);
    }

    // ── Settings and lists ───────────────────────────────────────────────────────────────────────────────────────

    private sealed record SettingsPut
    {
        public bool ScheduleEnabled { get; init; }
        public string ScheduleStart { get; init; } = "03:00";
        public int ScheduleWindowMinutes { get; init; } = 120;
        public int MaxAdviceScheduled { get; init; } = 10;
        public int MaxAdviceManual { get; init; } = 50;
        public int MaxOpenAdvice { get; init; } = 100;
        public int MinScore { get; init; } = 40;
        public int ManualMaxMinutes { get; init; } = 30;
        public int MaxModelCalls { get; init; } = 200;
        public long MaxTokens { get; init; } = 1_500_000;
        public bool UseModel { get; init; }
        public Guid? AnalysisModelId { get; init; }
        public string[] ExcludedFolderPaths { get; init; } = [];
    }

    private static SettingsPut SettingsBody(Func<SettingsPut, SettingsPut> change) => change(new SettingsPut());

    private async Task SetSettingsAsync(Func<SettingsPut, SettingsPut> change)
    {
        var response = await _client.PutAsJsonAsync("/api/dream/settings", SettingsBody(change));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Settings_RoundTrip_AndAreAudited()
    {
        await NewArticleAsync("Note", "/Fenced");
        await SetSettingsAsync(s => s with { MinScore = 55, ScheduleStart = "04:30", ExcludedFolderPaths = ["/Fenced/"] });

        var read = await _client.GetFromJsonAsync<JsonElement>("/api/dream/settings");
        read.GetProperty("minScore").GetInt32().Should().Be(55);
        read.GetProperty("scheduleStart").GetString().Should().Be("04:30");
        read.GetProperty("excludedFolders").EnumerateArray().Single().GetProperty("path").GetString().Should().Be("/Fenced");
        read.GetProperty("updatedAt").ValueKind.Should().Be(JsonValueKind.String);

        InScope(sp => sp.GetRequiredService<IDbConnectionFactory>().CreateConnection()
            .ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'dream_settings_changed'")).Should().Be(1);
    }

    [Theory]
    [InlineData("""{ "minScore": 101 }""")]
    [InlineData("""{ "scheduleStart": "25:00" }""")]
    [InlineData("""{ "excludedFolderPaths": ["/no/such/folder"] }""")]
    [InlineData("""{ "excludedFolderPaths": ["/../etc"] }""")]
    [InlineData("""{ "analysisModelId": "11111111-1111-1111-1111-111111111111" }""")]
    public async Task Settings_RefuseWhatIsOutOfRange(string change)
    {
        var body = JsonSerializer.SerializeToNode(SettingsBody(s => s), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        foreach (var (name, value) in System.Text.Json.Nodes.JsonNode.Parse(change)!.AsObject().ToList())
            body[name] = value?.DeepClone();

        var response = await _client.PutAsync("/api/dream/settings", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("""{ "maxAdvice": -1 }""")]
    [InlineData("""{ "maxAdvice": 1001 }""")]
    [InlineData("""{ "maxMinutes": 1441 }""")]
    [InlineData("""{ "mode": "normal", "sample": { "seed": 1 } }""")]
    [InlineData("""{ "modelId": "11111111-1111-1111-1111-111111111111" }""")]
    [InlineData("""{ "useModel": true }""")]
    public async Task Start_RefusesWhatItCannotDo(string body)
    {
        var response = await _client.PostAsync("/api/dream/runs", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ANamelessStart_UsesTheSettings_AndAModelThatIsNotReadyFallsBackToTheRules()
    {
        await NewArticleAsync("Note");
        await SetSettingsAsync(s => s with { UseModel = true, MaxAdviceManual = 7, ManualMaxMinutes = 9 });

        var run = await RunToEndAsync();

        Outcome(run).Should().Be("completed");
        run.GetProperty("useModel").GetBoolean().Should().BeFalse("nothing can leave the node without a model and a key");
        run.GetProperty("limits").GetProperty("maxAdvice").GetInt32().Should().Be(7);
        run.GetProperty("limits").GetProperty("maxMinutes").GetInt32().Should().Be(9);
        (await AllEventsAsync(run.GetProperty("id").GetGuid())).Select(e => e.GetProperty("text").GetString()).Should().Contain(t => t!.Contains("rules only"));
    }

    [Fact]
    public async Task Status_SaysWhatTheModelAndTheVectorsAreDoing()
    {
        var status = await _client.GetFromJsonAsync<JsonElement>("/api/dream/status");
        status.GetProperty("available").GetBoolean().Should().BeTrue();
        status.GetProperty("modelReady").GetBoolean().Should().BeFalse();
        status.GetProperty("chatAvailable").GetBoolean().Should().BeFalse();
        status.GetProperty("lastRun").ValueKind.Should().Be(JsonValueKind.Null);

        await AddTextModelAsync();
        await NewArticleAsync("Note");

        status = await _client.GetFromJsonAsync<JsonElement>("/api/dream/status");
        status.GetProperty("modelReady").GetBoolean().Should().BeTrue();
        status.GetProperty("modelLabel").GetString().Should().Be("Fake");
        status.GetProperty("chatAvailable").GetBoolean().Should().BeTrue();
        status.GetProperty("embeddings").GetProperty("total").GetInt32().Should().Be(1);
        status.GetProperty("embeddings").GetProperty("pending").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task TheAdviceList_FiltersSortsAndPages()
    {
        for (var i = 0; i < 4; i++) await NewArticleAsync($"Note {i}");
        DetectMoves(90, 70, 50, 85);
        await RunToEndAsync();
        var items = (await ListAdviceAsync("status=new")).GetProperty("items").EnumerateArray().ToList();
        await WithStoreAsync(store => store.TrySetAdviceStatusAsync(items[0].GetProperty("id").GetGuid(), [DreamAdviceStatus.New], DreamAdviceStatus.InChat, null, 1, DateTime.UtcNow));

        (await ListAdviceAsync()).GetProperty("total").GetInt32().Should().Be(4, "status=open is new and in_chat together");
        (await ListAdviceAsync("status=new")).GetProperty("total").GetInt32().Should().Be(3);
        (await ListAdviceAsync("status=in_chat")).GetProperty("total").GetInt32().Should().Be(1);
        (await ListAdviceAsync("status=open&minScore=80")).GetProperty("total").GetInt32().Should().Be(2);

        var page = await ListAdviceAsync("status=open&page=2&pageSize=2");
        page.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("score").GetInt32()).Should().Equal(70, 50);

        var byDate = await ListAdviceAsync("status=open&sort=date");
        byDate.GetProperty("items")[0].GetProperty("seq").GetInt64().Should().BeGreaterThan(byDate.GetProperty("items")[3].GetProperty("seq").GetInt64());

        (await _client.GetAsync("/api/dream/advice?status=nonsense")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.GetAsync("/api/dream/advice?kind=nonsense")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.GetAsync("/api/dream/advice?sort=nonsense")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var status = await _client.GetFromJsonAsync<JsonElement>("/api/dream/status");
        var counts = status.GetProperty("openAdvice");
        counts.GetProperty("total").GetInt32().Should().Be(4);
        counts.GetProperty("high").GetInt32().Should().Be(2);
        counts.GetProperty("medium").GetInt32().Should().Be(1);
        counts.GetProperty("low").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task OneAdvice_CarriesItsCardFields()
    {
        await NewArticleAsync("Note");
        DetectMoves(85);
        await RunToEndAsync();

        var id = (await ListAdviceAsync()).GetProperty("items")[0].GetProperty("id").GetGuid();
        var advice = await _client.GetFromJsonAsync<JsonElement>($"/api/dream/advice/{id}");

        advice.GetProperty("kind").GetString().Should().Be("move_article");
        advice.GetProperty("band").GetString().Should().Be("high");
        advice.GetProperty("applyMode").GetString().Should().Be("code");
        advice.GetProperty("title").GetString().Should().Be("Move Note");
        advice.GetProperty("action").GetProperty("type").GetString().Should().Be("move_article");
        advice.GetProperty("can").GetProperty("apply").GetBoolean().Should().BeTrue();
        advice.GetProperty("can").GetProperty("prompt").GetBoolean().Should().BeTrue();

        (await _client.GetAsync($"/api/dream/advice/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OneRun_CountsItsAdviceByStatus_AndKnowsItsJournal()
    {
        await NewArticleAsync("A");
        await NewArticleAsync("B");
        DetectMoves(85, 70);
        var run = await RunToEndAsync();
        var id = run.GetProperty("id").GetGuid();

        var items = (await ListAdviceAsync()).GetProperty("items");
        await WithStoreAsync(store => store.TrySetAdviceStatusAsync(items[0].GetProperty("id").GetGuid(), [DreamAdviceStatus.New], DreamAdviceStatus.Dismissed, null, 1, DateTime.UtcNow));

        var detail = await GetRunAsync(id);
        detail.GetProperty("adviceCounts").GetProperty("new").GetInt32().Should().Be(1);
        detail.GetProperty("adviceCounts").GetProperty("dismissed").GetInt32().Should().Be(1);
        detail.GetProperty("logAvailable").GetBoolean().Should().BeTrue();

        var events = await AllEventsAsync(id);
        events.Select(e => e.GetProperty("kind").GetString()).Should().Contain("phase").And.Contain("advice");
        events.Select(e => e.GetProperty("seq").GetInt64()).Should().BeInAscendingOrder();

        (await _client.GetAsync($"/api/dream/runs/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheHistory_FiltersByMode_AndPagesNewestFirst()
    {
        await NewArticleAsync("Note");
        var first = (await RunToEndAsync()).GetProperty("id").GetGuid();
        var second = (await RunToEndAsync(new { mode = "trial" })).GetProperty("id").GetGuid();

        var all = await _client.GetFromJsonAsync<JsonElement>("/api/dream/runs");
        all.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).Should().Equal(second, first);
        all.GetProperty("pageSize").GetInt32().Should().Be(10);

        var trials = await _client.GetFromJsonAsync<JsonElement>("/api/dream/runs?mode=trial");
        trials.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().Should().Be(second);

        (await _client.GetAsync("/api/dream/runs?mode=nonsense")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Rules and the AI model as two switches (package ux2a) ────────────────────────────────────────────────────

    [Fact]
    public async Task WithTheRulesOff_NoRuleAdviceIsMade_AndTheModelStillRuns()
    {
        await AddTextModelAsync();
        var a = await NewArticleAsync("Alpha");
        var b = await NewArticleAsync("Beta");
        DetectMoves(80, 70);
        _node.Script.Pairs = _ => [(a.Id, b.Id)];

        var run = await RunToEndAsync(new { useRules = false, useModel = true });

        Outcome(run).Should().Be("completed");
        run.GetProperty("useRules").GetBoolean().Should().BeFalse();
        run.GetProperty("useModel").GetBoolean().Should().BeTrue();
        Counter(run, "adviceCreated").Should().Be(1, "only the model's overlap, no move from the rules");
        var kinds = (await ListAdviceAsync()).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("kind").GetString()).ToList();
        kinds.Should().Equal("overlap");
        _node.Script.DetectorStarted.Task.IsCompleted.Should().BeFalse("the detectors did not run at all");
        (await AllEventsAsync(run.GetProperty("id").GetGuid())).Select(e => e.GetProperty("text").GetString())
            .Should().Contain(t => t!.Contains("built-in rules are off"));

        var listed = (await _client.GetFromJsonAsync<JsonElement>("/api/dream/runs")).GetProperty("items")[0];
        listed.GetProperty("useRules").GetBoolean().Should().BeFalse("the history row says what ran");
    }

    [Fact]
    public async Task TheRules_AreOnByDefault_AndRecordedOnTheRun()
    {
        await NewArticleAsync("Note");
        DetectMoves(80);

        var run = await RunToEndAsync();

        run.GetProperty("useRules").GetBoolean().Should().BeTrue();
        Counter(run, "adviceCreated").Should().Be(1);

        var pairsOnly = await RunToEndAsync(new { mode = "trial", phases = new[] { "pairs" } });
        pairsOnly.GetProperty("useRules").GetBoolean().Should().BeFalse("phases without the detectors run no rules, and the run says so");
    }

    [Theory]
    [InlineData("""{ "useRules": false, "useModel": false }""")]
    [InlineData("""{ "useRules": false }""")]
    public async Task AStartWithNothingToRun_IsRefused(string body)
    {
        var response = await _client.PostAsync("/api/dream/runs", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain("at least one");
        Coordinator.ActiveRunId.Should().BeNull();
    }

    [Fact]
    public async Task WithTheRulesOff_AModelFromTheSettingsThatIsNotReady_RefusesTheStart_InsteadOfRunningNothing()
    {
        await SetSettingsAsync(s => s with { UseModel = true });

        var response = await _client.PostAsJsonAsync("/api/dream/runs", new { useRules = false });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await _client.GetFromJsonAsync<JsonElement>("/api/dream/runs")).GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task TheStatus_SaysWhyTheModelIsNotReady()
    {
        (await _client.GetFromJsonAsync<JsonElement>("/api/dream/status")).GetProperty("modelProblem").GetString().Should().Be(DreamErrorCodes.NoModel);
        (await _client.PostAsJsonAsync("/api/chat/models", new { modelId = "fake/model", label = "Fake" })).EnsureSuccessStatusCode();
        (await _client.GetFromJsonAsync<JsonElement>("/api/dream/status")).GetProperty("modelProblem").GetString().Should().Be(DreamErrorCodes.NoKey);
        (await _client.PostAsJsonAsync("/api/chat/keys", new { label = "k", apiKey = "sk-test-key" })).EnsureSuccessStatusCode();
        var ready = await _client.GetFromJsonAsync<JsonElement>("/api/dream/status");
        ready.GetProperty("modelReady").GetBoolean().Should().BeTrue();
        ready.GetProperty("modelProblem").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
