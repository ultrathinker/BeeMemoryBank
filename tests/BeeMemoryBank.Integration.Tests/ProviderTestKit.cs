using System.Net;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>Stands in for every model provider: answers each request from a script and keeps what it was sent (URL, key, body, and every
/// request header). The key is the bearer token, or the protocol's own key header (Anthropic's <c>x-api-key</c>, Gemini's
/// <c>x-goog-api-key</c>). No request ever leaves the test process.</summary>
public sealed class ScriptedProviderHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(string Url, string? Key, string Body)> Seen { get; } = [];

    /// <summary>The request headers of each request of <see cref="Seen"/>, in the same order (names in lower case).</summary>
    public List<Dictionary<string, string>> Headers { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var key = request.Headers.Authorization?.Parameter
                  ?? (headers.TryGetValue("x-api-key", out var a) ? a : headers.TryGetValue("x-goog-api-key", out var g) ? g : null);
        lock (Seen)
        {
            Seen.Add((request.RequestUri!.ToString(), key, body));
            Headers.Add(headers);
        }
        return respond(request, body);
    }
}

/// <summary>A logger that keeps every line at every level, with its structured values and any exception, so a test can show what a log
/// never holds.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var line = logLevel + " " + formatter(state, exception);
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
            line += " | " + string.Join(" | ", values.Select(v => v.Key + "=" + v.Value));
        if (exception is not null) line += " | " + exception;
        lock (Lines) Lines.Add(line);
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> whose every named client sends through one handler (the scripted provider).</summary>
public sealed class HandlerClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public List<string> Names { get; } = [];

    public HttpClient CreateClient(string name)
    {
        lock (Names) Names.Add(name);
        return new HttpClient(handler, disposeHandler: false);
    }
}

/// <summary>Builds the real provider pipeline (router → protocol client → egress guard) of a test node around a scripted handler, and
/// adds providers, keys and models the way the endpoints do.</summary>
public static class ProviderTestKit
{
    public static HttpResponseMessage Ok(string content, int? prompt = 1500, int? completion = 120, string model = "provider/model-x") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model,
                choices = new[] { new { message = new { content } } },
                usage = prompt == null ? null : new { prompt_tokens = prompt, completion_tokens = completion },
            }), Encoding.UTF8, "application/json"),
        };

    public static HttpResponseMessage Status(HttpStatusCode code) =>
        new(code) { Content = new StringContent("{\"error\":{\"message\":\"upstream said no\"}}", Encoding.UTF8, "application/json") };

    /// <summary>An SSE body of text deltas, then (optionally) a usage chunk and [DONE].</summary>
    public static HttpResponseMessage Sse(string model, params string[] deltas)
    {
        var sb = new StringBuilder();
        foreach (var d in deltas)
            sb.Append("data: ").Append(JsonSerializer.Serialize(new { model, choices = new[] { new { index = 0, delta = new { content = d } } } })).Append("\n\n");
        sb.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    public static ModelRouter Router(IServiceProvider sp, HttpMessageHandler handler, ILogger<OpenAiChatClient>? log = null) =>
        new(sp.GetRequiredService<ChatSettingsRepository>(), sp.GetRequiredService<ChatProviderRepository>(),
            new ModelClientRegistry([Client(handler, log), Anthropic(handler), Gemini(handler)]));

    public static OpenAiChatClient Client(HttpMessageHandler handler, ILogger<OpenAiChatClient>? log = null) =>
        new(new ProviderEgress(new HandlerClientFactory(handler)), log ?? NullLogger<OpenAiChatClient>.Instance);

    public static AnthropicMessagesClient Anthropic(HttpMessageHandler handler, ILogger<AnthropicMessagesClient>? log = null) =>
        new(new ProviderEgress(new HandlerClientFactory(handler)), log ?? NullLogger<AnthropicMessagesClient>.Instance);

    public static GeminiClient Gemini(HttpMessageHandler handler, ILogger<GeminiClient>? log = null) =>
        new(new ProviderEgress(new HandlerClientFactory(handler)), log ?? NullLogger<GeminiClient>.Instance);

    /// <summary>An answer of <paramref name="code"/> with a JSON body.</summary>
    public static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A server-sent event stream made of <paramref name="events"/> (each an <c>event:</c> name, or null, and a JSON payload).</summary>
    public static HttpResponseMessage EventStream(params (string? Event, string Data)[] events)
    {
        var sb = new StringBuilder();
        foreach (var (name, data) in events)
        {
            if (name is not null) sb.Append("event: ").Append(name).Append('\n');
            sb.Append("data: ").Append(data).Append("\n\n");
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    /// <summary>A provider row as POST /api/chat/providers makes it, optionally approved the way POST .../approve does.</summary>
    public static async Task<ChatProvider> AddProviderAsync(IServiceProvider sp, string name, string baseUrl, bool approve = true, bool isLocal = false,
        ProviderWireOptions? options = null, string protocol = ProviderProtocols.OpenAiChat)
    {
        var providers = sp.GetRequiredService<ChatProviderRepository>();
        var (url, error) = ProviderUrl.Normalize(baseUrl, isLocal);
        url.Should().NotBeNull(error);
        var now = DateTime.UtcNow;
        var p = new ChatProvider
        {
            Id = Guid.NewGuid(), Name = name, Protocol = protocol, BaseUrl = url!, IsLocal = isLocal,
            OptionsJson = (options ?? ProviderWireOptions.DefaultFor(protocol)).ToJson(), CreatedAt = now, UpdatedAt = now,
        };
        await providers.CreateAsync(p);
        if (approve) await providers.ApproveAsync(p, userId: 1);
        return (await providers.GetAsync(p.Id))!;
    }

    public static async Task<Guid> AddKeyAsync(IServiceProvider sp, Guid providerId, string secret, int priority = 0, bool enabled = true)
    {
        var repo = sp.GetRequiredService<ChatSettingsRepository>();
        var key = new ChatApiKey
        {
            Id = Guid.NewGuid(), ProviderId = providerId, Label = $"key-{priority}", KeyPrefix = secret[..Math.Min(8, secret.Length)],
            Enabled = enabled, Priority = priority, CreatedAt = DateTime.UtcNow,
        };
        await repo.SealSecretAsync(key, secret);
        await repo.CreateAsync(key);
        return key.Id;
    }

    public static async Task<ChatModelRow> AddModelAsync(IServiceProvider sp, Guid providerId, string modelId, bool isText = true,
        bool isVision = false, bool isImageGen = false)
    {
        var repo = sp.GetRequiredService<ChatSettingsRepository>();
        var model = new ChatModelRow
        {
            Id = Guid.NewGuid(), ProviderId = providerId, ModelId = modelId, Label = modelId, IsText = isText, IsVision = isVision,
            IsImageGen = isImageGen, CreatedAt = DateTime.UtcNow,
        };
        await repo.CreateAsync(model);
        return model;
    }
}
