using System.Net;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Services;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests.Dream;

/// <summary>
/// The real Dream model client (plan 3.7, package M1) on a real node's chat key table, with only the HTTP call to
/// OpenRouter replaced: the chat's key rotation is used (401 disables, 429 pauses, 5xx tries the next key), the run's
/// budget is checked before and counted after, a failed round waits 30, 60 and 120 seconds and then gives up, and no key
/// or note text escapes into an exception message.
/// </summary>
public class OpenRouterDreamModelClientTests : IAsyncLifetime
{
    private const string Password = "dreamModelClientPassword";
    private readonly BmbWebApplicationFactory _factory = new();

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync(password: Password);
        (await Session.UnlockAsync(Password)).Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private SessionService Session => _factory.Services.GetRequiredService<SessionService>();

    // ── Doubles ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Stands in for openrouter.ai: answers each request from a script and keeps what it was sent.</summary>
    private sealed class ScriptedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string? Key, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.Parameter, body));
            return respond(request, body);
        }
    }

    /// <summary>Waits no time at all and says how long it was asked to wait.</summary>
    private sealed class RecordingTime : TimeProvider
    {
        public List<TimeSpan> Waits { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan) Waits.Add(dueTime);
            return base.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.Zero, period);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + exception);
    }

    private static HttpResponseMessage Ok(string content, int? prompt = 1500, int? completion = 120, string model = "provider/model-x") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model,
                choices = new[] { new { message = new { content } } },
                usage = prompt == null ? null : new { prompt_tokens = prompt, completion_tokens = completion },
            }), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Status(HttpStatusCode code) =>
        new(code) { Content = new StringContent("{\"error\":{\"message\":\"upstream said no\"}}", Encoding.UTF8, "application/json") };

    private async Task<Guid> AddKeyAsync(string secret, int priority, bool enabled = true)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ChatSettingsRepository>();
        var key = new ChatApiKey
        {
            Id = Guid.NewGuid(), Label = $"key-{priority}", KeyPrefix = secret[..8], Enabled = enabled, Priority = priority,
            CreatedAt = DateTime.UtcNow,
        };
        await repo.SealSecretAsync(key, secret);
        await repo.CreateAsync(key);
        return key.Id;
    }

    private async Task<ChatApiKey> KeyRowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ChatSettingsRepository>().GetByIdAsync(id))!;
    }

    private async Task<T> WithClientAsync<T>(ScriptedHandler handler, TimeProvider? time, Func<OpenRouterDreamModelClient, Task<T>> body,
        DreamThresholds? thresholds = null, ILogger<OpenRouterClient>? openRouterLog = null)
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var openRouter = new OpenRouterClient(new HttpClient(handler), openRouterLog ?? NullLogger<OpenRouterClient>.Instance);
        var client = new OpenRouterDreamModelClient(sp.GetRequiredService<ChatSettingsRepository>(), Session, openRouter,
            NullLogger<OpenRouterDreamModelClient>.Instance, thresholds, time);
        return await body(client);
    }

    private static DreamModelRequest Request(DreamRunBudget? budget = null, string user = "the user text", int maxOutput = 700) =>
        new("vendor/some-model", "the system text", user, maxOutput, budget ?? new DreamRunBudget(200, 1_500_000));

    // ── The call ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACall_SendsTheModelTheTwoMessagesTheOutputLimitAndTemperatureZero_AndCountsTheRealUsage()
    {
        const string secret = "sk-or-dream-key-one-0001";
        await AddKeyAsync(secret, 1);
        var handler = new ScriptedHandler((_, _) => Ok("{\"verdict\":\"related\"}", 1500, 120));
        var budget = new DreamRunBudget(10, 100_000);

        var reply = await WithClientAsync(handler, null, c => c.CompleteAsync(Request(budget, maxOutput: 650), CancellationToken.None));

        reply.Content.Should().Be("{\"verdict\":\"related\"}");
        reply.PromptTokens.Should().Be(1500);
        reply.CompletionTokens.Should().Be(120);
        reply.Model.Should().Be("provider/model-x");
        budget.ModelCalls.Should().Be(1);
        budget.TokensUsed.Should().Be(1620);

        var seen = handler.Seen.Should().ContainSingle().Subject;
        seen.Url.Should().Be("https://openrouter.ai/api/v1/chat/completions", "the egress host is pinned by the chat client");
        seen.Key.Should().Be(secret);
        using var doc = JsonDocument.Parse(seen.Body);
        var root = doc.RootElement;
        root.GetProperty("model").GetString().Should().Be("vendor/some-model");
        root.GetProperty("max_tokens").GetInt32().Should().Be(650);
        root.GetProperty("temperature").GetDouble().Should().Be(0);
        root.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low", "reasoning models are asked to think little");
        root.GetProperty("reasoning").EnumerateObject().Should().ContainSingle("only the effort is sent");
        root.GetProperty("stream").GetBoolean().Should().BeFalse();
        root.TryGetProperty("tools", out _).Should().BeFalse("the model has no tools");
        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        messages.Should().HaveCount(2);
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages[0].GetProperty("content").GetString().Should().Be("the system text");
        messages[1].GetProperty("role").GetString().Should().Be("user");
        messages[1].GetProperty("content").GetString().Should().Be("the user text");
    }

    [Fact]
    public async Task TheChatsOwnCall_StillSendsNoOutputLimitAndNoTemperature()
    {
        var handler = new ScriptedHandler((_, _) => Ok("hi"));
        var client = new OpenRouterClient(new HttpClient(handler), NullLogger<OpenRouterClient>.Instance);

        await client.CompleteAsync("sk-or-any-key", "vendor/model", [new ChatRoleMessage("user", "hello")]);

        handler.Seen.Single().Body.Should().NotContain("max_tokens").And.NotContain("temperature").And.NotContain("reasoning");
    }

    [Fact]
    public async Task AReplyWithoutUsage_IsCountedByEstimate_SoTheBudgetStillStopsTheRun()
    {
        await AddKeyAsync("sk-or-dream-key-two-0002", 1);
        var handler = new ScriptedHandler((_, _) => Ok("x".PadRight(300, 'x'), prompt: null));
        var budget = new DreamRunBudget(10, 100_000);

        var reply = await WithClientAsync(handler, null, c => c.CompleteAsync(Request(budget, user: new string('u', 3000)), CancellationToken.None));

        reply.PromptTokens.Should().BeGreaterThan(900, "about (system + user) / 3");
        reply.CompletionTokens.Should().Be(100);
        budget.TokensUsed.Should().Be(reply.PromptTokens + reply.CompletionTokens);
    }

    // ── The budget ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheBudget_IsCheckedBeforeTheCall_ACallItDoesNotAdmitIsNeverMade()
    {
        await AddKeyAsync("sk-or-dream-key-three-0003", 1);
        var handler = new ScriptedHandler((_, _) => Ok("{}"));
        var budget = new DreamRunBudget(maxModelCalls: 1, maxTokens: 100_000);

        await WithClientAsync(handler, null, async c =>
        {
            await c.CompleteAsync(Request(budget), CancellationToken.None);
            var second = () => c.CompleteAsync(Request(budget), CancellationToken.None);
            await second.Should().ThrowAsync<DreamBudgetExceededException>();
            return 0;
        });

        handler.Seen.Should().HaveCount(1);
    }

    [Fact]
    public async Task ATokenBudgetBelowTheEstimate_RefusesTheCall()
    {
        await AddKeyAsync("sk-or-dream-key-four-0004", 1);
        var handler = new ScriptedHandler((_, _) => Ok("{}"));
        var budget = new DreamRunBudget(maxModelCalls: 50, maxTokens: 1000);

        var call = () => WithClientAsync(handler, null, c => c.CompleteAsync(Request(budget, user: new string('u', 9000)), CancellationToken.None));

        await call.Should().ThrowAsync<DreamBudgetExceededException>();
        handler.Seen.Should().BeEmpty();
        budget.ModelCalls.Should().Be(0);
    }

    // ── The chat's key rotation ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A429OnTheFirstKey_PausesItForFiveMinutes_AndTheSecondKeyAnswers()
    {
        const string first = "sk-or-dream-first-0005", second = "sk-or-dream-second-0006";
        var firstId = await AddKeyAsync(first, 1);
        var secondId = await AddKeyAsync(second, 2);
        var handler = new ScriptedHandler((req, _) =>
            req.Headers.Authorization!.Parameter == first ? Status(HttpStatusCode.TooManyRequests) : Ok("{\"ok\":true}"));
        var before = DateTime.UtcNow;

        var reply = await WithClientAsync(handler, null, c => c.CompleteAsync(Request(), CancellationToken.None));

        reply.Content.Should().Be("{\"ok\":true}");
        handler.Seen.Select(s => s.Key).Should().Equal(first, second);
        var paused = await KeyRowAsync(firstId);
        paused.Enabled.Should().BeTrue();
        paused.DisabledUntil.Should().NotBeNull();
        paused.DisabledUntil!.Value.Should().BeCloseTo(before + TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30));
        (await KeyRowAsync(secondId)).DisabledUntil.Should().BeNull();
    }

    [Fact]
    public async Task AnUpstreamErrorBody_IsNeverLoggedByTheDreamCall_ButTheChatsOwnCallStillLogsIt()
    {
        const string excerpt = "FLAGGED-INPUT-EXCERPT-OF-A-NOTE-8812";
        await AddKeyAsync("sk-or-dream-flagged-0021", 1);
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent($"{{\"error\":{{\"message\":\"flagged\",\"metadata\":{{\"flagged_input\":\"{excerpt}\"}}}}}}", Encoding.UTF8, "application/json"),
        });
        var log = new CapturingLogger<OpenRouterClient>();

        var call = () => WithClientAsync(handler, new RecordingTime(), c => c.CompleteAsync(Request(), CancellationToken.None), openRouterLog: log);

        var ex = (await call.Should().ThrowAsync<DreamProviderException>()).Which;
        log.Lines.Should().NotBeEmpty().And.OnlyContain(l => l.Contains("HTTP 403") && !l.Contains(excerpt));
        ex.ToString().Should().NotContain(excerpt);

        var chatLog = new CapturingLogger<OpenRouterClient>();
        var chat = new OpenRouterClient(new HttpClient(handler), chatLog);
        var chatCall = () => chat.CompleteAsync("sk-or-chat-key", "vendor/model", [new ChatRoleMessage("user", "hello")]);
        await chatCall.Should().ThrowAsync<OpenRouterHttpException>();
        chatLog.Lines.Should().Contain(l => l.Contains(excerpt), "the chat's own behaviour is unchanged");
    }

    [Fact]
    public async Task A401_DisablesTheKey_AndTheNextOneIsUsed()
    {
        const string bad = "sk-or-dream-revoked-0007", good = "sk-or-dream-good-0008";
        var badId = await AddKeyAsync(bad, 1);
        await AddKeyAsync(good, 2);
        var handler = new ScriptedHandler((req, _) =>
            req.Headers.Authorization!.Parameter == bad ? Status(HttpStatusCode.Unauthorized) : Ok("{}"));

        await WithClientAsync(handler, null, c => c.CompleteAsync(Request(), CancellationToken.None));

        (await KeyRowAsync(badId)).Enabled.Should().BeFalse("a 401 disables the key until an admin enables it");
    }

    [Fact]
    public async Task A5xxOrATransportError_MovesOnToTheNextKey_WithoutPausingTheFirst()
    {
        const string flaky = "sk-or-dream-flaky-0009", steady = "sk-or-dream-steady-0010";
        var flakyId = await AddKeyAsync(flaky, 1);
        await AddKeyAsync(steady, 2);
        var handler = new ScriptedHandler((req, _) =>
            req.Headers.Authorization!.Parameter == flaky ? throw new HttpRequestException("connection reset") : Ok("{}"));

        await WithClientAsync(handler, null, c => c.CompleteAsync(Request(), CancellationToken.None));

        handler.Seen.Select(s => s.Key).Should().Equal(flaky, steady);
        var row = await KeyRowAsync(flakyId);
        row.Enabled.Should().BeTrue();
        row.DisabledUntil.Should().BeNull();
    }

    // ── When every key has failed ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryKeyFailing_WaitsThirtySixtyAndOneTwentySeconds_ThenIsAProviderError()
    {
        const string secret = "sk-or-dream-allfail-0011";
        await AddKeyAsync(secret, 1);
        var handler = new ScriptedHandler((_, _) => Status(HttpStatusCode.BadGateway));
        var time = new RecordingTime();

        var call = () => WithClientAsync(handler, time, c => c.CompleteAsync(Request(), CancellationToken.None));

        var ex = (await call.Should().ThrowAsync<DreamProviderException>()).Which;
        time.Waits.Should().Equal(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120));
        handler.Seen.Should().HaveCount(4, "one try, then one after each wait");
        ex.Message.Should().NotContain(secret).And.NotContain("the user text");
    }

    [Fact]
    public async Task EveryKeyPausedBy429_WaitsTheSameLadder_ThenIsAProviderError()
    {
        await AddKeyAsync("sk-or-dream-limited-0012", 1);
        var handler = new ScriptedHandler((_, _) => Status(HttpStatusCode.TooManyRequests));
        var time = new RecordingTime();

        var call = () => WithClientAsync(handler, time, c => c.CompleteAsync(Request(), CancellationToken.None));

        await call.Should().ThrowAsync<DreamProviderException>();
        time.Waits.Should().HaveCount(3);
        handler.Seen.Should().HaveCount(1, "the key is paused by the first 429, so the later rounds find none to try");
    }

    [Fact]
    public async Task AKeyThatRecoversDuringTheWaits_AnswersTheLaterRound()
    {
        await AddKeyAsync("sk-or-dream-recover-0013", 1);
        var calls = 0;
        var handler = new ScriptedHandler((_, _) => ++calls < 3 ? Status(HttpStatusCode.ServiceUnavailable) : Ok("{\"back\":1}"));
        var time = new RecordingTime();
        var budget = new DreamRunBudget(10, 100_000);

        var reply = await WithClientAsync(handler, time, c => c.CompleteAsync(Request(budget), CancellationToken.None));

        reply.Content.Should().Be("{\"back\":1}");
        time.Waits.Should().Equal(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        budget.ModelCalls.Should().Be(3, "every request sent is a call: the two 503s and the answer");
        budget.TokensUsed.Should().Be(1620, "a refusal by status costs no tokens, the answer what the provider reported");
    }

    // ── Reasoning models: the cap, the effort and the cut-off ───────────────────────────────────────────────────

    private static HttpResponseMessage Raw(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private async Task<DreamModelReply> AskAsync(HttpResponseMessage answer, int maxOutput = 700)
    {
        await AddKeyAsync("sk-or-dream-reason-" + Guid.NewGuid().ToString("N")[..8], 1);
        var handler = new ScriptedHandler((_, _) => answer);
        return await WithClientAsync(handler, null, c => c.CompleteAsync(Request(maxOutput: maxOutput), CancellationToken.None));
    }

    [Fact]
    public async Task AnAnswerTheCapStopped_IsFlaggedCutOff_ThroughFinishReasonLength()
    {
        var reply = await AskAsync(Raw("""{"model":"m","choices":[{"finish_reason":"length","message":{"content":"{\"verdict\":\"dup"}}],"usage":{"prompt_tokens":900,"completion_tokens":3000}}"""));

        reply.CutOff.Should().BeTrue();
        reply.Content.Should().Be("{\"verdict\":\"dup");
        reply.CompletionTokens.Should().Be(3000);
    }

    [Theory]
    [InlineData("""{"model":"m","choices":[{"finish_reason":"length","message":{"content":null,"reasoning":"Let me think about the two notes ..."}}],"usage":{"prompt_tokens":900,"completion_tokens":700}}""", true)]
    [InlineData("""{"model":"m","choices":[{"finish_reason":"stop","message":{"content":"","reasoning":"thinking"}}],"usage":{"prompt_tokens":900,"completion_tokens":700}}""", true)]
    [InlineData("""{"model":"m","choices":[{"message":{"content":"","reasoning_details":[{"type":"reasoning.text","text":"thinking"}]}}],"usage":{"prompt_tokens":900,"completion_tokens":700}}""", true)]
    [InlineData("""{"model":"m","choices":[{"message":{"content":null}}],"usage":{"prompt_tokens":900,"completion_tokens":700,"completion_tokens_details":{"reasoning_tokens":690}}}""", true)]
    [InlineData("""{"model":"m","choices":[{"finish_reason":"stop","message":{"content":""}}],"usage":{"prompt_tokens":900,"completion_tokens":0}}""", false)]
    [InlineData("""{"model":"m","choices":[{"finish_reason":"stop","message":{"content":"{}","reasoning":"short thought"}}],"usage":{"prompt_tokens":900,"completion_tokens":50}}""", false)]
    [InlineData("""{"model":"m","choices":[{"finish_reason":"stop","message":{"content":"{}"}}],"usage":{"prompt_tokens":900,"completion_tokens":50}}""", false)]
    [InlineData("""{"model":"m","choices":[{"message":{"content":"{}"}}]}""", false)]
    [InlineData("""{"model":"m","choices":[{"message":{"content":"","reasoning":{"summary":"an object"}}}]}""", true)]
    [InlineData("""{"model":"m","choices":[{"message":{"content":"","reasoning":42}}]}""", false)]
    [InlineData("""{"model":"m","choices":[{"message":{"content":"","reasoning_details":"odd"}}],"usage":{"prompt_tokens":5,"completion_tokens":5,"completion_tokens_details":"odd"}}""", false)]
    [InlineData("""{"model":"m","choices":[{"message":{"content":""}}],"usage":{"prompt_tokens":5,"completion_tokens":5,"completion_tokens_details":{"reasoning_tokens":"many"}}}""", false)]
    public async Task CutOff_IsFinishReasonLength_OrReasoningWithoutContent_NothingElse(string json, bool expected)
    {
        (await AskAsync(Raw(json))).CutOff.Should().Be(expected);
    }

    [Fact]
    public async Task TheCeiling_LetsTheTasksCapsThrough_AndClampsAnythingAbove()
    {
        await AddKeyAsync("sk-or-dream-ceiling-0040", 1);
        var handler = new ScriptedHandler((_, _) => Ok("{}"));

        await WithClientAsync(handler, null, async c =>
        {
            await c.CompleteAsync(Request(maxOutput: 6000), CancellationToken.None);
            await c.CompleteAsync(Request(maxOutput: 50_000), CancellationToken.None);
            return 0;
        });

        var sent = handler.Seen.Select(s => JsonDocument.Parse(s.Body).RootElement.GetProperty("max_tokens").GetInt32()).ToList();
        sent.Should().Equal(6000, OpenRouterDreamModelClient.MaxOutputTokensCeiling);
        OpenRouterDreamModelClient.MaxOutputTokensCeiling.Should().BeGreaterThanOrEqualTo(6000, "the tree review's cap must fit");
    }

    [Fact]
    public async Task TheReservation_CoversTheWholeOutputCap_SoAnAttemptThatCouldOverrunIsNotSent()
    {
        await AddKeyAsync("sk-or-dream-reserve-0041", 1);
        var handler = new ScriptedHandler((_, _) => Ok("{}"));
        // input estimate of the helper's texts: 10 tokens; with a 3000 cap the attempt needs 3010.
        var tight = new DreamRunBudget(maxModelCalls: 10, maxTokens: 3009);
        var roomy = new DreamRunBudget(maxModelCalls: 10, maxTokens: 3010);

        var refused = () => WithClientAsync(handler, null, c => c.CompleteAsync(Request(tight, maxOutput: 3000), CancellationToken.None));
        await refused.Should().ThrowAsync<DreamBudgetExceededException>();
        handler.Seen.Should().BeEmpty();

        await WithClientAsync(handler, null, c => c.CompleteAsync(Request(roomy, maxOutput: 3000), CancellationToken.None));
        handler.Seen.Should().ContainSingle();
    }

    // ── Every outgoing request counts against the budget ────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryKeyTried_IsACallUsed_AndARefusalByStatusCostsNoTokens()
    {
        const string first = "sk-or-dream-first-0030", second = "sk-or-dream-second-0031";
        await AddKeyAsync(first, 1);
        await AddKeyAsync(second, 2);
        var handler = new ScriptedHandler((req, _) =>
            req.Headers.Authorization!.Parameter == first ? Status(HttpStatusCode.TooManyRequests) : Ok("{}", 1500, 120));
        var budget = new DreamRunBudget(10, 100_000);

        await WithClientAsync(handler, null, c => c.CompleteAsync(Request(budget), CancellationToken.None));

        handler.Seen.Should().HaveCount(2);
        budget.ModelCalls.Should().Be(2, "the refused attempt on the first key used a call slot");
        budget.TokensUsed.Should().Be(1620, "the refused attempt cost no tokens, the answered one what the provider reported");
    }

    [Fact]
    public async Task TheCallLimit_StopsAFailoverBetweenKeys_TheSecondKeyIsNeverTried()
    {
        const string first = "sk-or-dream-first-0032", second = "sk-or-dream-second-0033";
        await AddKeyAsync(first, 1);
        await AddKeyAsync(second, 2);
        var handler = new ScriptedHandler((req, _) =>
            req.Headers.Authorization!.Parameter == first ? Status(HttpStatusCode.TooManyRequests) : Ok("{}"));
        var budget = new DreamRunBudget(maxModelCalls: 1, maxTokens: 100_000);

        var call = () => WithClientAsync(handler, null, c => c.CompleteAsync(Request(budget), CancellationToken.None));

        await call.Should().ThrowAsync<DreamBudgetExceededException>();
        handler.Seen.Should().ContainSingle().Which.Key.Should().Be(first, "the one call allowed went to the first key");
        budget.ModelCalls.Should().Be(1);
    }

    [Fact]
    public async Task TheCallLimit_StopsTheRetryRounds_WithoutWaitingForARequestThatCannotBeSent()
    {
        await AddKeyAsync("sk-or-dream-outage-0034", 1);
        var handler = new ScriptedHandler((_, _) => Status(HttpStatusCode.ServiceUnavailable));
        var time = new RecordingTime();
        var budget = new DreamRunBudget(maxModelCalls: 2, maxTokens: 100_000);

        var call = () => WithClientAsync(handler, time, c => c.CompleteAsync(Request(budget), CancellationToken.None));

        await call.Should().ThrowAsync<DreamBudgetExceededException>();
        handler.Seen.Should().HaveCount(2, "one try and one retry, as many as the budget has call slots");
        time.Waits.Should().Equal(TimeSpan.FromSeconds(30));   // no 60 s wait for a third request that cannot go out
        budget.ModelCalls.Should().Be(2);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("timeout")]
    public async Task AnAttemptWhoseOutcomeIsUnknown_IsChargedTheWholeUpperEstimate_AndTheTokenLimitStopsTheRetries(string kind)
    {
        await AddKeyAsync("sk-or-dream-unknown-0036", 1);
        var handler = new ScriptedHandler((_, _) => kind == "timeout"
            ? throw new TaskCanceledException("timed out", new TimeoutException())
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[]}", Encoding.UTF8, "application/json") });
        var time = new RecordingTime();
        var request = Request(new DreamRunBudget(maxModelCalls: 50, maxTokens: 1000));
        var estimate = DreamRunBudget.EstimateTokens(request.System, request.User, request.MaxOutputTokens);   // 710

        var call = () => WithClientAsync(handler, time, c => c.CompleteAsync(request, CancellationToken.None));

        await call.Should().ThrowAsync<DreamBudgetExceededException>("a second attempt would need 710 more tokens of a 1000 allowance");
        handler.Seen.Should().HaveCount(1);
        request.Budget.ModelCalls.Should().Be(1);
        request.Budget.TokensUsed.Should().Be(estimate, "there is no usage to go by, and the provider may have charged the request");
        time.Waits.Should().BeEmpty("no wait for a request that cannot be sent");
    }

    // ── Keys that cannot be read are a setup failure, keys that are paused are a wait ───────────────────────────

    [Fact]
    public async Task AKeyThatCannotBeDecrypted_IsASetupFailure_NoWaitAndNothingSent()
    {
        var id = await AddKeyAsync("sk-or-dream-garbled-0037", 1);
        using (var scope = _factory.Services.CreateScope())
        using (var conn = scope.ServiceProvider.GetRequiredService<ChatDbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE chat_api_key SET iv = zeroblob(12) WHERE id = @id", new { id });
        var handler = new ScriptedHandler((_, _) => Ok("{}"));
        var time = new RecordingTime();
        var budget = new DreamRunBudget(10, 100_000);

        var call = () => WithClientAsync(handler, time, c => c.CompleteAsync(Request(budget), CancellationToken.None));

        (await call.Should().ThrowAsync<DreamProviderException>()).Which.Message.Should().Contain("cannot be read").And.Contain("enter it again");
        time.Waits.Should().BeEmpty("no wait cures a key this node cannot open");
        handler.Seen.Should().BeEmpty();
        budget.ModelCalls.Should().Be(0);
    }

    [Fact]
    public async Task AKeyThatIsOnlyPaused_StillGetsTheWaits_ThenIsAProviderError()
    {
        var id = await AddKeyAsync("sk-or-dream-paused-0038", 1);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ChatSettingsRepository>()
                .RecordKeyFailureAsync(id, disable: false, disabledUntil: DateTime.UtcNow.AddMinutes(5), "rate limited");
        var handler = new ScriptedHandler((_, _) => Ok("{}"));
        var time = new RecordingTime();

        var call = () => WithClientAsync(handler, time, c => c.CompleteAsync(Request(), CancellationToken.None));

        (await call.Should().ThrowAsync<DreamProviderException>()).Which.Message.Should().Contain("paused");
        time.Waits.Should().Equal(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120));
        handler.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task ARequestTheProviderRefuses_ForGood_GivesUpAtOnce_WithoutWaiting()
    {
        await AddKeyAsync("sk-or-dream-badmodel-0014", 1);
        var handler = new ScriptedHandler((_, _) => Status(HttpStatusCode.BadRequest));
        var time = new RecordingTime();

        var call = () => WithClientAsync(handler, time, c => c.CompleteAsync(Request(), CancellationToken.None));

        (await call.Should().ThrowAsync<DreamProviderException>()).Which.Message.Should().Contain("HTTP 400").And.Contain("analysis model");
        time.Waits.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenAll401sDisableEveryKey_ItGivesUpAtOnce_WithoutWaiting()
    {
        await AddKeyAsync("sk-or-dream-revoked-0015", 1);
        var handler = new ScriptedHandler((_, _) => Status(HttpStatusCode.Unauthorized));
        var time = new RecordingTime();

        var call = () => WithClientAsync(handler, time, c => c.CompleteAsync(Request(), CancellationToken.None));

        (await call.Should().ThrowAsync<DreamProviderException>()).Which.Message.Should().Contain("disabled");
        time.Waits.Should().BeEmpty();
    }

    [Fact]
    public async Task NoKeyAtAll_OrOnlyADisabledOne_IsAProviderErrorAtOnce_NothingIsSent()
    {
        var handler = new ScriptedHandler((_, _) => Ok("{}"));
        var time = new RecordingTime();

        var none = () => WithClientAsync(handler, time, c => c.CompleteAsync(Request(), CancellationToken.None));
        (await none.Should().ThrowAsync<DreamProviderException>()).Which.Message.Should().Contain("No OpenRouter key is enabled");

        await AddKeyAsync("sk-or-dream-off-0016", 1, enabled: false);
        var disabled = () => WithClientAsync(handler, time, c => c.CompleteAsync(Request(), CancellationToken.None));
        await disabled.Should().ThrowAsync<DreamProviderException>();

        handler.Seen.Should().BeEmpty();
        time.Waits.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAnswerThatIsNotACompletion_IsRetriedLikeAnOutage_NotLeftToCrashTheRun()
    {
        await AddKeyAsync("sk-or-dream-odd-0017", 1);
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[]}", Encoding.UTF8, "application/json"),
        });
        var time = new RecordingTime();

        var call = () => WithClientAsync(handler, time, c => c.CompleteAsync(Request(), CancellationToken.None));

        await call.Should().ThrowAsync<DreamProviderException>();
        time.Waits.Should().HaveCount(3);
    }

    [Fact]
    public async Task StopDuringTheWait_EndsTheCallAtOnce()
    {
        await AddKeyAsync("sk-or-dream-stop-0018", 1);
        var handler = new ScriptedHandler((_, _) => Status(HttpStatusCode.InternalServerError));
        using var cts = new CancellationTokenSource();
        var time = new CancelOnWait(cts);

        var call = () => WithClientAsync(handler, time, c => c.CompleteAsync(Request(), cts.Token));

        await call.Should().ThrowAsync<OperationCanceledException>();
        handler.Seen.Should().HaveCount(1);
    }

    /// <summary>A "Stop" that arrives while the call is waiting.</summary>
    private sealed class CancelOnWait(CancellationTokenSource cts) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            cts.Cancel();
            return base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period);
        }
    }

    // ── Locked, and what is never in a message ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhileTheVaultIsLocked_NothingIsUnlocked_NothingIsSent_AndItIsTheSessionsOwnException()
    {
        await AddKeyAsync("sk-or-dream-locked-0019", 1);
        Session.Lock();
        var handler = new ScriptedHandler((_, _) => Ok("{}"));

        var call = () => WithClientAsync(handler, null, c => c.CompleteAsync(Request(), CancellationToken.None));

        await call.Should().ThrowAsync<SessionLockedException>();
        handler.Seen.Should().BeEmpty();
        Session.IsUnlocked.Should().BeFalse("the client never unlocks anything");
    }

    [Fact]
    public async Task NoKeyAndNoNoteTextEverReachesAnExceptionMessage()
    {
        const string secret = "sk-or-dream-hushhush-0020";
        const string noteText = "TEXT-OF-A-PRIVATE-NOTE-4471";
        await AddKeyAsync(secret, 1);
        var handler = new ScriptedHandler((_, _) => Status(HttpStatusCode.InternalServerError));
        var time = new RecordingTime();

        var call = () => WithClientAsync(handler, time, c => c.CompleteAsync(Request(user: noteText), CancellationToken.None));

        var ex = (await call.Should().ThrowAsync<DreamProviderException>()).Which;
        ex.ToString().Should().NotContain(secret).And.NotContain(noteText);
    }

    [Fact]
    public async Task ARequestWithoutAModelId_IsRefusedBeforeAnythingElse()
    {
        var handler = new ScriptedHandler((_, _) => Ok("{}"));
        var request = Request() with { ModelId = " " };

        var call = () => WithClientAsync(handler, null, c => c.CompleteAsync(request, CancellationToken.None));

        await call.Should().ThrowAsync<DreamProviderException>();
        handler.Seen.Should().BeEmpty();
        request.Budget.ModelCalls.Should().Be(0);
    }
}
