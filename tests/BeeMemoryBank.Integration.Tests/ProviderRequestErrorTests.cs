using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Providers;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A request error is not a key problem (docs/ai-providers.md, the fallback chain): when a provider refuses the request itself - 400, 404,
/// 413, 422 - no other key of that provider is tried, the key is neither paused nor disabled nor noted, and the next model of the chain gets
/// the request; when the chain is exhausted the error is shown. Any other failure is still the key's (the next key is tried first). The same
/// rule for every protocol, for the chat, the non-streaming calls and Dream, where a request error is permanent for that model: no waits
/// for it, and a later round does not send it again.
/// </summary>
public class ProviderRequestErrorTests : IAsyncLifetime
{
    private const string Password = "requestErrorPassword";
    private readonly BmbWebApplicationFactory _factory = new();
    private Func<HttpRequestMessage, string, HttpResponseMessage> _respond = (_, _) => ProviderTestKit.Status(HttpStatusCode.InternalServerError);
    private readonly ScriptedProviderHandler _provider;
    private HttpClient _client = null!;

    public ProviderRequestErrorTests() => _provider = new ScriptedProviderHandler((req, body) => _respond(req, body));

    public async Task InitializeAsync()
    {
        _factory.RouteOutboundHttpThrough(_provider);
        _client = _factory.CreateClient();
        // A user id of this class alone: the chat's stream rate limit (30 turns in 5 minutes per user) is one limiter for the whole test
        // process, and the classes that chat as user 1 already use much of that budget.
        _client.DefaultRequestHeaders.Add("X-User-Id", "2101");
        await _factory.InitializeNodeAsync(password: Password);
        (await _factory.Services.GetRequiredService<SessionService>().UnlockAsync(Password)).Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private sealed record Setup(ChatProvider A, ChatProvider B, ChatModelRow ModelA, ChatModelRow ModelB);

    /// <summary>Provider A (two keys, <paramref name="protocol"/>) holds the default text model; provider B (one key, OpenAI format) its fallback.</summary>
    private async Task<Setup> ChainAsync(string protocol, bool withFallback = true)
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var a = await ProviderTestKit.AddProviderAsync(sp, "Provider A", "https://a.example/v1", protocol: protocol);
        var b = await ProviderTestKit.AddProviderAsync(sp, "Provider B", "https://b.example/v1");
        await ProviderTestKit.AddKeyAsync(sp, a.Id, "key-a-first", 1);
        await ProviderTestKit.AddKeyAsync(sp, a.Id, "key-a-second", 2);
        await ProviderTestKit.AddKeyAsync(sp, b.Id, "key-b-only", 1);
        var modelA = await ProviderTestKit.AddModelAsync(sp, a.Id, protocol == ProviderProtocols.Gemini ? "gemini-3-pro" : "model-a");
        var modelB = await ProviderTestKit.AddModelAsync(sp, b.Id, "model-b");
        (await _client.PatchAsJsonAsync("/api/chat/settings/defaults", new { defaultTextModelId = modelA.Id.ToString() })).EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync("/api/chat/settings/fallbacks", new { fallbackModelIds = withFallback ? new[] { modelB.Id } : [] }))
            .EnsureSuccessStatusCode();
        return new Setup(a, b, modelA, modelB);
    }

    private static HttpResponseMessage Refusal(string protocol, int status) => ProviderTestKit.Json((HttpStatusCode)status, protocol switch
    {
        ProviderProtocols.AnthropicMessages => "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"no\"}}",
        ProviderProtocols.Gemini => "{\"error\":{\"code\":" + status + ",\"message\":\"no\",\"status\":\"INVALID_ARGUMENT\"}}",
        _ => "{\"error\":{\"message\":\"no\",\"type\":\"invalid_request_error\"}}",
    });

    private static string Host(string url) => new Uri(url).Host;

    private async Task<(string Sse, JsonElement? Done)> TurnAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/chat/stream", new { message = "hello" });
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        JsonElement? done = null;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
            if (lines[i] == "event: done" && lines[i + 1].StartsWith("data: ", StringComparison.Ordinal))
                done = JsonDocument.Parse(lines[i + 1]["data: ".Length..]).RootElement.Clone();
        return (text, done);
    }

    private async Task<List<ChatApiKey>> KeysOfAsync(Guid providerId)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ChatSettingsRepository>().ListByProviderAsync(providerId)).OrderBy(k => k.Priority).ToList();
    }

    [Theory]
    [InlineData(1, ProviderProtocols.OpenAiChat, 400)]
    [InlineData(2, ProviderProtocols.OpenAiChat, 404)]
    [InlineData(3, ProviderProtocols.OpenAiChat, 413)]
    [InlineData(4, ProviderProtocols.OpenAiChat, 422)]
    [InlineData(5, ProviderProtocols.AnthropicMessages, 400)]
    [InlineData(6, ProviderProtocols.AnthropicMessages, 413)]
    [InlineData(7, ProviderProtocols.Gemini, 400)]
    [InlineData(8, ProviderProtocols.Gemini, 404)]
    public async Task InTheChat_ARequestError_TriesNoOtherKey_MarksNoKey_AndTheNextModelAnswers(int row, string protocol, int status)
    {
        var s = await ChainAsync(protocol);
        _respond = (req, _) => Host(req.RequestUri!.ToString()) == "a.example" ? Refusal(protocol, status) : ProviderTestKit.Sse("model-b", "from B");

        var (_, done) = await TurnAsync();

        done!.Value.GetProperty("content").GetString().Should().Be("from B", $"row {row}");
        done.Value.GetProperty("provider").GetString().Should().Be("Provider B");
        _provider.Seen.Select(x => x.Key).Should().Equal(["key-a-first", "key-b-only"], "the second key of A would get the same answer");
        (await KeysOfAsync(s.A.Id)).Should().OnlyContain(k => k.Enabled && k.DisabledUntil == null && k.LastError == null,
            "a refused request is not the key's failure");
    }

    [Theory]
    [InlineData(1, ProviderProtocols.OpenAiChat, 500)]
    [InlineData(2, ProviderProtocols.AnthropicMessages, 529)]
    [InlineData(3, ProviderProtocols.Gemini, 503)]
    public async Task InTheChat_AFailureThatIsNotARequestError_StillTriesTheNextKeyFirst(int row, string protocol, int status)
    {
        var s = await ChainAsync(protocol);
        _respond = (req, _) => Host(req.RequestUri!.ToString()) == "a.example" ? Refusal(protocol, status) : ProviderTestKit.Sse("model-b", "from B");

        var (_, done) = await TurnAsync();

        done!.Value.GetProperty("content").GetString().Should().Be("from B", $"row {row}");
        _provider.Seen.Select(x => x.Key).Should().Equal("key-a-first", "key-a-second", "key-b-only");
        (await KeysOfAsync(s.A.Id)).Should().OnlyContain(k => k.LastError != null, "a provider failure is noted on each key tried");
    }

    [Fact]
    public async Task WhenTheChainIsExhausted_TheRequestErrorIsShown()
    {
        var s = await ChainAsync(ProviderProtocols.AnthropicMessages, withFallback: false);
        _respond = (_, _) => Refusal(ProviderProtocols.AnthropicMessages, 400);

        var (sse, done) = await TurnAsync();

        done.Should().BeNull();
        sse.Should().Contain("event: error").And.Contain("HTTP 400").And.Contain("invalid_request_error");
        _provider.Seen.Should().ContainSingle();
        (await KeysOfAsync(s.A.Id)).Should().OnlyContain(k => k.Enabled && k.DisabledUntil == null && k.LastError == null);
    }

    [Fact]
    public async Task OffTheStream_TheRouterTreatsARequestErrorTheSameWay()
    {
        var s = await ChainAsync(ProviderProtocols.Gemini);
        _respond = (req, _) => Host(req.RequestUri!.ToString()) == "a.example" ? Refusal(ProviderProtocols.Gemini, 400) : ProviderTestKit.Ok("from B");
        using var scope = _factory.Services.CreateScope();
        var router = ProviderTestKit.Router(scope.ServiceProvider, _provider);

        var (answer, route) = await router.RunAsync([s.ModelA, s.ModelB],
            (r, call, ct) => r.Client.CompleteAsync(call, [new ChatRoleMessage("user", "x")], CompletionOptions.Chat, ct), CancellationToken.None);

        answer.Content.Should().Be("from B");
        route.ProviderName.Should().Be("Provider B");
        _provider.Seen.Select(x => x.Key).Should().Equal("key-a-first", "key-b-only");
        (await KeysOfAsync(s.A.Id)).Should().OnlyContain(k => k.LastError == null);

        // Alone in its chain the refusal is the answer: its message, no other key.
        _provider.Seen.Clear();
        var alone = () => router.RunAsync([s.ModelA],
            (r, call, ct) => r.Client.CompleteAsync(call, [new ChatRoleMessage("user", "x")], CompletionOptions.Chat, ct), CancellationToken.None);
        (await alone.Should().ThrowAsync<AllKeysExhaustedException>()).WithMessage("*HTTP 400*INVALID_ARGUMENT*");
        _provider.Seen.Should().ContainSingle();
    }

    private sealed class NoWait : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.Zero, period);
    }

    [Fact]
    public async Task InDream_ARequestErrorIsPermanentForThatModel_ItIsNotSentAgainInALaterRound()
    {
        var s = await ChainAsync(ProviderProtocols.AnthropicMessages);
        _respond = (req, _) => Host(req.RequestUri!.ToString()) == "a.example"
            ? Refusal(ProviderProtocols.AnthropicMessages, 422)
            : ProviderTestKit.Status(HttpStatusCode.ServiceUnavailable);
        using var scope = _factory.Services.CreateScope();
        var client = new ProviderDreamModelClient(ProviderTestKit.Router(scope.ServiceProvider, _provider), NullLogger<ProviderDreamModelClient>.Instance,
            time: new NoWait());
        var budget = new DreamRunBudget(50, 10_000_000);

        var call = () => client.CompleteAsync(new DreamModelRequest(s.ModelA.ModelId, "system", "user", 500, budget, s.ModelA.Id), CancellationToken.None);

        (await call.Should().ThrowAsync<DreamProviderException>()).Which.Message.Should().Contain("HTTP 422");
        var toA = _provider.Seen.Where(x => Host(x.Url) == "a.example").Select(x => x.Key).ToList();
        toA.Should().Equal(["key-a-first"], "the refused model is asked once, with one key, over every round");
        _provider.Seen.Count(x => Host(x.Url) == "b.example").Should().Be(4, "the fallback that failed transiently is tried in each of the four rounds");
        (await KeysOfAsync(s.A.Id)).Should().OnlyContain(k => k.LastError == null && k.DisabledUntil == null);
    }

    [Fact]
    public async Task InDream_ARequestErrorAloneGivesUpAtOnce_AndTheFallbackAnswersWhenThereIsOne()
    {
        var s = await ChainAsync(ProviderProtocols.OpenAiChat);
        _respond = (req, _) => Host(req.RequestUri!.ToString()) == "a.example"
            ? Refusal(ProviderProtocols.OpenAiChat, 404)
            : ProviderTestKit.Ok("{}", 1000, 50, "model-b");
        using var scope = _factory.Services.CreateScope();
        var client = new ProviderDreamModelClient(ProviderTestKit.Router(scope.ServiceProvider, _provider), NullLogger<ProviderDreamModelClient>.Instance,
            time: new NoWait());
        var budget = new DreamRunBudget(10, 100_000);

        var reply = await client.CompleteAsync(new DreamModelRequest(s.ModelA.ModelId, "system", "user", 500, budget, s.ModelA.Id), CancellationToken.None);

        reply.Provider.Should().Be("Provider B");
        _provider.Seen.Select(x => x.Key).Should().Equal("key-a-first", "key-b-only");
        budget.ModelCalls.Should().Be(2);
        budget.TokensUsed.Should().Be(1050, "the refusal cost no tokens");
    }
}
