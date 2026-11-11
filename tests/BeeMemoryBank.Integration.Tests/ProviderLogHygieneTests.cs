using System.Net;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Providers;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// No protocol client writes what a provider answered into the node's log or into an exception text (review prov-r2, finding 1): an error
/// body or an answer can quote what was sent - decrypted note text, tool results, write arguments, thinking. A sentinel in every response
/// body (error JSON, plain text, a stream's error event, an answer the client only inspects) and in the request itself must appear in no log
/// line at any level and in no exception message, for every client and every path: completion, tools, stream, image generation, fetch models.
/// What may be logged is the status, a short error word and the provider's request id when it is a plain token.
/// </summary>
public class ProviderLogHygieneTests
{
    private const string Sentinel = "SENTINEL-NOTE-TEXT-7f3a";

    private static readonly string[] Protocols = [ProviderProtocols.OpenAiChat, ProviderProtocols.AnthropicMessages, ProviderProtocols.Gemini];

    private static readonly string[] Paths = ["completion", "tools", "stream", "image", "models"];

    public static IEnumerable<object[]> Cases()
    {
        var row = 0;
        foreach (var protocol in Protocols)
            foreach (var path in Paths)
                yield return [++row, protocol, path];
    }

    private static HttpResponseMessage Body(HttpStatusCode code, string body, string contentType = "application/json", string? requestId = null)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        if (requestId is not null) response.Headers.TryAddWithoutValidation("request-id", requestId);
        return response;
    }

    /// <summary>Error bodies of every shape the three protocols use, and one that is not JSON, all quoting the sentinel.</summary>
    private static IEnumerable<HttpResponseMessage> Errors() =>
    [
        Body(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { type = "error", error = new { type = "invalid_request_error", message = "you sent: " + Sentinel } })),
        Body(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { error = new { code = 400, message = "you sent: " + Sentinel, status = "INVALID_ARGUMENT" } })),
        Body(HttpStatusCode.InternalServerError, JsonSerializer.Serialize(new { error = new { message = Sentinel, type = "server_error", code = Sentinel } })),
        Body(HttpStatusCode.UnprocessableEntity, "upstream echo: " + Sentinel, "text/plain"),
    ];

    /// <summary>Answers with status 200 whose body quotes the sentinel where a client might be tempted to log it.</summary>
    private static IEnumerable<HttpResponseMessage> Answers(string protocol, string path)
    {
        string Ev(object o) => JsonSerializer.Serialize(o);
        switch (protocol, path)
        {
            case (ProviderProtocols.OpenAiChat, "stream"):
                // A plain answer to a request with tools: the diagnostic for models that narrate fake tool calls.
                yield return ProviderTestKit.Sse("m", "the article says ", Sentinel);
                break;
            case (ProviderProtocols.OpenAiChat, "image"):
                // No image came back, only text: the warning about the missing image.
                yield return ProviderTestKit.Ok("I cannot draw " + Sentinel);
                break;
            case (ProviderProtocols.AnthropicMessages, "stream"):
                yield return ProviderTestKit.EventStream(
                    ("message_start", Ev(new { type = "message_start", message = new { model = "m" } })),
                    ("error", Ev(new { type = "error", error = new { type = "overloaded_error", message = "while reading " + Sentinel } })));
                yield return ProviderTestKit.EventStream(
                    ("error", Ev(new { type = "error", error = new { type = Sentinel, message = Sentinel } })));
                break;
            case (ProviderProtocols.Gemini, "stream"):
                yield return ProviderTestKit.EventStream((null, Ev(new { error = new { code = 503, message = "while reading " + Sentinel, status = "UNAVAILABLE" } })));
                yield return ProviderTestKit.EventStream((null, Ev(new { error = new { code = 500, message = Sentinel, status = Sentinel } })));
                break;
        }
    }

    private static (IModelProtocolClient Client, List<string> Log) Client(string protocol, HttpMessageHandler handler)
    {
        switch (protocol)
        {
            case ProviderProtocols.AnthropicMessages:
            {
                var log = new CapturingLogger<AnthropicMessagesClient>();
                return (ProviderTestKit.Anthropic(handler, log), log.Lines);
            }
            case ProviderProtocols.Gemini:
            {
                var log = new CapturingLogger<GeminiClient>();
                return (ProviderTestKit.Gemini(handler, log), log.Lines);
            }
            default:
            {
                var log = new CapturingLogger<OpenAiChatClient>();
                return (ProviderTestKit.Client(handler, log), log.Lines);
            }
        }
    }

    private static Task Call(IModelProtocolClient client, string protocol, string path)
    {
        var target = ProviderEgressTarget.ForTest("https://api.example.com/v1", protocol: protocol,
            options: protocol == ProviderProtocols.OpenAiChat ? ProviderWireOptions.OpenRouter : ProviderWireOptions.DefaultFor(protocol));
        var call = new ModelCall(target, "key", "gemini-3-pro");
        List<ChatToolMessage> messages = [new() { Role = "user", Content = "please read " + Sentinel }];
        return path switch
        {
            "completion" => client.CompleteAsync(call, [new ChatRoleMessage("user", "please read " + Sentinel)], new CompletionOptions(100, 0.0, "low"),
                CancellationToken.None),
            "tools" => client.CompleteWithToolsAsync(call, messages, ChatToolDispatcher.ToolDefinitions, CancellationToken.None),
            "stream" => client.StreamWithToolsAsync(call, messages, ChatToolDispatcher.ToolDefinitions, (_, _) => ValueTask.CompletedTask,
                CancellationToken.None),
            "image" => client.GenerateImageAsync(call, messages, CancellationToken.None),
            _ => client.ListModelsAsync(target, "key", CancellationToken.None),
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NothingAProviderSent_ReachesTheLogOrAnExceptionText(int row, string protocol, string path)
    {
        var responses = Errors().Concat(Answers(protocol, path)).ToList();
        foreach (var response in responses)
        {
            var handler = new ScriptedProviderHandler((_, _) => response);
            var (client, log) = Client(protocol, handler);
            string? thrown = null;
            try { await Call(client, protocol, path); }
            catch (Exception ex) { thrown = ex.ToString(); }

            log.Should().NotContain(l => l.Contains(Sentinel), $"row {row}: {protocol} {path} logged what the provider sent");
            (thrown ?? "").Should().NotContain(Sentinel, $"row {row}: {protocol} {path} put what the provider sent into an exception");
        }
    }

    [Theory]
    [InlineData(1, ProviderProtocols.OpenAiChat)]
    [InlineData(2, ProviderProtocols.AnthropicMessages)]
    [InlineData(3, ProviderProtocols.Gemini)]
    public async Task AFailure_IsLoggedWithItsStatusErrorWordAndAPlainRequestId_AndNothingElse(int row, string protocol)
    {
        var plain = new ScriptedProviderHandler((_, _) => Body(HttpStatusCode.TooManyRequests,
            JsonSerializer.Serialize(new { error = new { type = "rate_limit_error", status = "RESOURCE_EXHAUSTED", message = Sentinel } }), requestId: "req_011CTZ9x"));
        var (client, log) = Client(protocol, plain);
        await ((Func<Task>)(() => Call(client, protocol, "tools"))).Should().ThrowAsync<ProviderHttpException>();
        var line = log.Should().ContainSingle($"row {row}").Subject;
        line.Should().Contain("429").And.Contain("req_011CTZ9x").And.NotContain(Sentinel);
        line.Should().Contain(protocol == ProviderProtocols.Gemini ? "RESOURCE_EXHAUSTED" : "rate_limit_error");

        var odd = new ScriptedProviderHandler((_, _) => Body(HttpStatusCode.TooManyRequests, "{}", requestId: "id with spaces <b>"));
        var (client2, log2) = Client(protocol, odd);
        await ((Func<Task>)(() => Call(client2, protocol, "tools"))).Should().ThrowAsync<ProviderHttpException>();
        log2.Should().ContainSingle().Which.Should().NotContain("spaces", "a request id that is not a plain token is left out");
    }
}
