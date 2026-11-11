using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Providers;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The chat's failover through the real /api/chat/stream (docs/ai-providers.md): the keys of one provider (401 disables, 402/429 pause, any
/// other failure tries the next key), then the default text model's fallback chain across providers - streaming only before the first
/// byte. The provider that answered is in the done event and on the stored message. Only the HTTP of the providers is scripted.
/// </summary>
public class ProviderFailoverTests : IAsyncLifetime
{
    private const string Password = "providerFailoverPassword";
    private readonly BmbWebApplicationFactory _factory = new();
    private Func<HttpRequestMessage, string, HttpResponseMessage> _respond = (_, _) => ProviderTestKit.Status(HttpStatusCode.InternalServerError);
    private readonly ScriptedProviderHandler _provider;
    private HttpClient _client = null!;

    public ProviderFailoverTests() => _provider = new ScriptedProviderHandler((req, body) => _respond(req, body));

    public async Task InitializeAsync()
    {
        _factory.RouteOutboundHttpThrough(_provider);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-User-Id", "1");
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

    /// <summary>Provider A (two keys) holds the default text model, provider B (one key) its only fallback.</summary>
    private async Task<Setup> TwoProvidersAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var a = await ProviderTestKit.AddProviderAsync(sp, "Provider A", "https://a.example/v1");
        var b = await ProviderTestKit.AddProviderAsync(sp, "Provider B", "https://b.example/v1");
        await ProviderTestKit.AddKeyAsync(sp, a.Id, "sk-a-first-0001", 1);
        await ProviderTestKit.AddKeyAsync(sp, a.Id, "sk-a-second-0002", 2);
        await ProviderTestKit.AddKeyAsync(sp, b.Id, "sk-b-only-0003", 1);
        var modelA = await ProviderTestKit.AddModelAsync(sp, a.Id, "model-a");
        var modelB = await ProviderTestKit.AddModelAsync(sp, b.Id, "model-b");
        (await _client.PatchAsJsonAsync("/api/chat/settings/defaults", new { defaultTextModelId = modelA.Id.ToString() })).EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync("/api/chat/settings/fallbacks", new { fallbackModelIds = new[] { modelB.Id } })).EnsureSuccessStatusCode();
        return new Setup(a, b, modelA, modelB);
    }

    private async Task<(string Sse, JsonElement? Done)> TurnAsync(string message = "hello")
    {
        var response = await _client.PostAsJsonAsync("/api/chat/stream", new { message });
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        JsonElement? done = null;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
            if (lines[i] == "event: done" && lines[i + 1].StartsWith("data: ", StringComparison.Ordinal))
                done = JsonDocument.Parse(lines[i + 1]["data: ".Length..]).RootElement.Clone();
        return (text, done);
    }

    private static string Host(string url) => new Uri(url).Host;

    [Fact]
    public async Task InsideAProvider_A429PausesTheKey_AndTheNextKeyAnswers()
    {
        var s = await TwoProvidersAsync();
        _respond = (req, _) => req.Headers.Authorization!.Parameter == "sk-a-first-0001"
            ? ProviderTestKit.Status(HttpStatusCode.TooManyRequests)
            : ProviderTestKit.Sse("model-a", "from ", "A");

        var (_, done) = await TurnAsync();

        done!.Value.GetProperty("content").GetString().Should().Be("from A");
        done.Value.GetProperty("provider").GetString().Should().Be("Provider A");
        _provider.Seen.Select(x => x.Key).Should().Equal("sk-a-first-0001", "sk-a-second-0002");
        using var scope = _factory.Services.CreateScope();
        var keys = await scope.ServiceProvider.GetRequiredService<ChatSettingsRepository>().ListByProviderAsync(s.A.Id);
        keys.Single(k => k.Priority == 1).DisabledUntil.Should().NotBeNull("a 429 pauses the key for five minutes");
    }

    [Fact]
    public async Task WhenEveryKeyOfTheProviderFails_TheFallbackModelOfAnotherProviderAnswers_AndItIsRecorded()
    {
        var s = await TwoProvidersAsync();
        _respond = (req, _) => Host(req.RequestUri!.ToString()) == "a.example"
            ? ProviderTestKit.Status(HttpStatusCode.BadGateway)
            : ProviderTestKit.Sse("model-b", "from B");

        var (_, done) = await TurnAsync();

        done!.Value.GetProperty("content").GetString().Should().Be("from B");
        done.Value.GetProperty("model").GetString().Should().Be("model-b");
        done.Value.GetProperty("provider").GetString().Should().Be("Provider B");
        _provider.Seen.Select(x => (Host(x.Url), x.Key)).Should().Equal(
            ("a.example", "sk-a-first-0001"), ("a.example", "sk-a-second-0002"), ("b.example", "sk-b-only-0003"));
        JsonDocument.Parse(_provider.Seen[2].Body).RootElement.GetProperty("model").GetString().Should().Be("model-b");

        var conversationId = done.Value.GetProperty("conversationId").GetGuid();
        var messages = await _client.GetFromJsonAsync<List<JsonElement>>($"/api/chat/conversations/{conversationId}/messages");
        var answer = messages!.Last(m => m.GetProperty("role").GetString() == "assistant");
        answer.GetProperty("provider").GetString().Should().Be("Provider B", "the message stores which provider answered");
        answer.GetProperty("model").GetString().Should().Be("model-b");
    }

    [Fact]
    public async Task AFallbackWhoseProviderIsNotApproved_IsSkipped_AndNothingIsSentToIt()
    {
        var s = await TwoProvidersAsync();
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ChatProviderRepository>().WithdrawApprovalAsync(s.B.Id);
        _respond = (_, _) => ProviderTestKit.Status(HttpStatusCode.ServiceUnavailable);

        var (sse, done) = await TurnAsync();

        done.Should().BeNull();
        sse.Should().Contain("event: error").And.Contain("not approved");
        _provider.Seen.Should().OnlyContain(x => Host(x.Url) == "a.example");
    }

    [Fact]
    public async Task WhenNoModelOfTheChainCanBeCalled_TheTurnIsRefusedBeforeItStreams()
    {
        var s = await TwoProvidersAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var providers = scope.ServiceProvider.GetRequiredService<ChatProviderRepository>();
            await providers.WithdrawApprovalAsync(s.A.Id);
            await providers.WithdrawApprovalAsync(s.B.Id);
        }

        var response = await _client.PostAsJsonAsync("/api/chat/stream", new { message = "hello" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not approved");
        _provider.Seen.Should().BeEmpty();
    }

    /// <summary>A stream that breaks after its first delta reached the user is an error: no other key, no fallback is spliced in.</summary>
    [Fact]
    public async Task AFailureAfterTheFirstByte_IsAnError_NoOtherKeyOrModelIsTried()
    {
        await TwoProvidersAsync();
        _respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BreakingSseContent("model-a", "Hel") };

        var (sse, done) = await TurnAsync();

        done.Should().BeNull();
        sse.Should().Contain("event: delta").And.Contain("Hel").And.Contain("event: error");
        _provider.Seen.Should().ContainSingle("the user has seen part of the first answer; nothing is spliced after it");
    }

    [Fact]
    public async Task AFailureBeforeTheFirstByte_OfAStream_MovesOnToTheNextKey()
    {
        await TwoProvidersAsync();
        _respond = (req, _) => req.Headers.Authorization!.Parameter == "sk-a-first-0001"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new BreakingSseContent("model-a") }
            : ProviderTestKit.Sse("model-a", "second key");

        var (_, done) = await TurnAsync();

        done!.Value.GetProperty("content").GetString().Should().Be("second key");
        _provider.Seen.Select(x => x.Key).Should().Equal("sk-a-first-0001", "sk-a-second-0002");
    }

    // ── A provider that stalls: the HTTP client's timeout, while the browser is still there (review prov-r1, finding 2) ──────────
    // The scripted handler throws what HttpClient throws when its Timeout elapses: a TaskCanceledException with a TimeoutException inside,
    // while the request's own token (the browser's) is NOT cancelled.

    private static HttpResponseMessage TimesOut() =>
        throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException());

    [Fact]
    public async Task AProviderThatStallsBeforeItsHeaders_TimesOut_AndTheNextKeyAnswers()
    {
        var s = await TwoProvidersAsync();
        _respond = (req, _) => req.Headers.Authorization!.Parameter == "sk-a-first-0001" ? TimesOut() : ProviderTestKit.Sse("model-a", "second ", "key");

        var (sse, done) = await TurnAsync();

        sse.Should().NotContain("event: error");
        done!.Value.GetProperty("content").GetString().Should().Be("second key");
        done.Value.GetProperty("provider").GetString().Should().Be("Provider A");
        _provider.Seen.Select(x => x.Key).Should().Equal("sk-a-first-0001", "sk-a-second-0002");
        using var scope = _factory.Services.CreateScope();
        var first = (await scope.ServiceProvider.GetRequiredService<ChatSettingsRepository>().ListByProviderAsync(s.A.Id)).Single(k => k.Priority == 1);
        first.LastError.Should().Be(ModelRouter.NoAnswerInTime, "the timeout is noted on the key");
        first.Enabled.Should().BeTrue("a stall is not the key's fault");
        first.DisabledUntil.Should().BeNull("a stall does not pause the key either");
    }

    [Fact]
    public async Task WhenEveryKeyOfTheProviderTimesOut_TheFallbackModelOfAnotherProviderAnswers()
    {
        await TwoProvidersAsync();
        _respond = (req, _) => Host(req.RequestUri!.ToString()) == "a.example" ? TimesOut() : ProviderTestKit.Sse("model-b", "from B");

        var (_, done) = await TurnAsync();

        done!.Value.GetProperty("content").GetString().Should().Be("from B");
        done.Value.GetProperty("provider").GetString().Should().Be("Provider B");
        _provider.Seen.Select(x => (Host(x.Url), x.Key)).Should().Equal(
            ("a.example", "sk-a-first-0001"), ("a.example", "sk-a-second-0002"), ("b.example", "sk-b-only-0003"));
    }

    [Fact]
    public async Task WhenEveryModelOfTheChainTimesOut_TheUserGetsAnErrorFrame_NotSilence()
    {
        await TwoProvidersAsync();
        _respond = (_, _) => TimesOut();

        var (sse, done) = await TurnAsync();

        done.Should().BeNull();
        sse.Should().Contain("event: error").And.Contain("did not answer in time");
        _provider.Seen.Should().HaveCount(3, "both keys of A, then B's key");
    }

    [Fact]
    public async Task AStallAfterTheFirstByte_IsAnErrorFrame_NoOtherKeyOrModelIsTried()
    {
        await TwoProvidersAsync();
        _respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new BreakingSseContent("model-a", ["Hel"],
                () => new TaskCanceledException("The operation was canceled.", new TimeoutException())),
        };

        var (sse, done) = await TurnAsync();

        done.Should().BeNull();
        sse.Should().Contain("event: delta").And.Contain("Hel").And.Contain("event: error").And.Contain("did not answer in time");
        _provider.Seen.Should().ContainSingle("the user has seen part of the first answer; nothing is spliced after it");
    }

    /// <summary>The same rule off the stream: vision, image generation, Fetch models and Dream go through <see cref="ModelRouter.RunAsync"/> /
    /// <c>RunOnRouteAsync</c>. A timeout moves on to the next key; the caller's own cancellation is not retried.</summary>
    [Fact]
    public async Task OffTheStream_ATimeoutMovesOnToTheNextKey_AndTheCallersCancellationIsNotRetried()
    {
        var s = await TwoProvidersAsync();
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var vision = new List<ChatToolMessage> { new() { Role = "user", Content = "describe" } };

        _respond = (req, _) => req.Headers.Authorization!.Parameter == "sk-a-first-0001" ? TimesOut() : ProviderTestKit.Ok("a description");
        var (answer, route) = await ProviderTestKit.Router(sp, _provider).RunAsync([s.ModelA],
            (r, call, ct) => r.Client.CompleteWithToolsAsync(call, vision, null, ct), CancellationToken.None);
        answer.Content.Should().Be("a description");
        route.ProviderName.Should().Be("Provider A");
        _provider.Seen.Select(x => x.Key).Should().Equal("sk-a-first-0001", "sk-a-second-0002");

        // The caller goes away during the first key's request: that is not the provider's failure, nothing more is sent.
        _provider.Seen.Clear();
        using var caller = new CancellationTokenSource();
        _respond = (_, _) =>
        {
            caller.Cancel();
            throw new TaskCanceledException("The operation was canceled.", null, caller.Token);
        };
        var cancelled = () => ProviderTestKit.Router(sp, _provider).RunAsync([s.ModelA, s.ModelB],
            (r, call, ct) => r.Client.CompleteWithToolsAsync(call, vision, null, ct), caller.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        _provider.Seen.Should().ContainSingle();
    }

    [Fact]
    public async Task FetchModels_MovesOnToTheNextKey_WhenTheFirstTimesOut()
    {
        var s = await TwoProvidersAsync();
        _respond = (req, _) => req.Headers.Authorization!.Parameter == "sk-a-first-0001"
            ? TimesOut()
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"listed-model\",\"context_length\":8192}]}", Encoding.UTF8, "application/json"),
            };

        var response = await _client.PostAsync($"/api/chat/providers/{s.A.Id}/fetch-models", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("listed-model");
        _provider.Seen.Select(x => x.Key).Should().Equal("sk-a-first-0001", "sk-a-second-0002");
    }

    [Fact]
    public async Task ImageGeneration_IsRefusedWithAClearMessage_ForAProviderThatDoesNotOfferIt()
    {
        var s = await TwoProvidersAsync();
        var model = await _client.PostAsJsonAsync("/api/chat/models",
            new { modelId = "img-model", label = "Img", isText = false, isImageGen = true, providerId = s.A.Id });
        model.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await model.Content.ReadAsStringAsync()).Should().Contain("Image generation is not available");

        var defaults = await _client.PatchAsJsonAsync("/api/chat/settings/defaults",
            new { defaultTextModelId = s.ModelA.Id.ToString(), defaultImageGenModelId = s.ModelB.Id.ToString() });
        defaults.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = _factory.Services.CreateScope();
        var router = ProviderTestKit.Router(scope.ServiceProvider, _provider);
        var run = () => router.RunAsync([s.ModelA],
            (route, call, ct) => route.Client.SupportsImageGeneration(route.Target.Options)
                ? route.Client.GenerateImageAsync(call, [new ChatToolMessage { Role = "user", Content = "a cat" }], ct)
                : throw new ProviderUnavailableException($"Image generation is not available through provider '{route.ProviderName}'."),
            CancellationToken.None);
        (await run.Should().ThrowAsync<AllKeysExhaustedException>()).WithMessage("*Image generation is not available*");
        _provider.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task TheFallbackChain_TakesTextModelsOnly_EachOnce()
    {
        var s = await TwoProvidersAsync();
        Guid vision;
        using (var scope = _factory.Services.CreateScope())
            vision = (await ProviderTestKit.AddModelAsync(scope.ServiceProvider, s.B.Id, "vision-only", isText: false, isVision: true)).Id;

        (await _client.PatchAsJsonAsync("/api/chat/settings/fallbacks", new { fallbackModelIds = new[] { vision } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PatchAsJsonAsync("/api/chat/settings/fallbacks", new { fallbackModelIds = new[] { s.ModelB.Id, s.ModelB.Id } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PatchAsJsonAsync("/api/chat/settings/fallbacks", new { fallbackModelIds = new[] { Guid.NewGuid() } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var saved = await _client.GetFromJsonAsync<JsonElement>("/api/chat/settings/fallbacks");
        saved.GetProperty("fallbackModelIds").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(s.ModelB.Id);
    }

    /// <summary>An SSE body that sends the given deltas and then breaks off like a dropped connection (or with the given failure).</summary>
    private sealed class BreakingSseContent(string model, string[] deltas, Func<Exception>? failure = null) : HttpContent
    {
        public BreakingSseContent(string model, params string[] deltas) : this(model, deltas, null) { }

        private Exception Failure() => failure?.Invoke() ?? new HttpRequestException("The connection was closed by the provider.");

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            foreach (var d in deltas)
            {
                var frame = "data: " + JsonSerializer.Serialize(new { model, choices = new[] { new { index = 0, delta = new { content = d } } } }) + "\n\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(frame));
            }
            throw Failure();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new BreakingStream(model, deltas, Failure));
    }

    private sealed class BreakingStream(string model, string[] deltas, Func<Exception> failure) : Stream
    {
        private readonly byte[] _data = Encoding.UTF8.GetBytes(string.Concat(deltas.Select(d =>
            "data: " + JsonSerializer.Serialize(new { model, choices = new[] { new { index = 0, delta = new { content = d } } } }) + "\n\n")));
        private int _pos;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _data.Length) throw failure();
            var n = Math.Min(count, _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
