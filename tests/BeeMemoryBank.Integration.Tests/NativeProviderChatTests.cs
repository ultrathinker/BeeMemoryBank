using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Providers;
using BeeMemoryBank.Core.Services;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The native Anthropic Messages and Gemini protocols through the real chat (<c>/api/chat/stream</c>, <c>/confirm</c>): the thinking blocks
/// and thought signatures of a tool-call turn go back with the next round of the loop, and with the round that <c>/confirm</c> resumes from
/// the stored (sealed) row, and are dropped when the fallback chain moves to another model; the keys of a native provider fail over as every
/// provider's do (401 disables, 429 pauses, 529 / 5xx and a timeout try the next key); Fetch models pages through the provider's list; a
/// custom provider of either protocol gets that protocol's wire options. Only the providers' HTTP is scripted.
/// </summary>
public class NativeProviderChatTests : IAsyncLifetime
{
    private const string Password = "nativeProviderPassword";
    private const string AnthropicBase = "https://api.anthropic.com/v1";
    private const string GeminiBase = "https://generativelanguage.googleapis.com/v1beta";
    private readonly BmbWebApplicationFactory _factory = new();
    private Func<HttpRequestMessage, string, HttpResponseMessage> _respond = (_, _) => ProviderTestKit.Status(HttpStatusCode.InternalServerError);
    private readonly ScriptedProviderHandler _provider;
    private HttpClient _client = null!;

    public NativeProviderChatTests() => _provider = new ScriptedProviderHandler((req, body) => _respond(req, body));

    public async Task InitializeAsync()
    {
        _factory.RouteOutboundHttpThrough(_provider);
        _client = _factory.CreateClient();
        // A user id of this class alone: the chat's stream rate limit (30 turns in 5 minutes per user) is one limiter for the whole test
        // process, and the classes that chat as user 1 already use much of that budget.
        _client.DefaultRequestHeaders.Add("X-User-Id", "2102");
        await _factory.InitializeNodeAsync(password: Password);
        (await _factory.Services.GetRequiredService<SessionService>().UnlockAsync(Password)).Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    // ── set-up ─────────────────────────────────────────────────────────────────

    private async Task<(ChatProvider Provider, ChatModelRow Model)> ProviderAsync(string protocol, string name, string baseUrl, string model,
        params string[] keys)
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var p = await ProviderTestKit.AddProviderAsync(sp, name, baseUrl, protocol: protocol);
        for (var i = 0; i < keys.Length; i++) await ProviderTestKit.AddKeyAsync(sp, p.Id, keys[i], i + 1);
        return (p, await ProviderTestKit.AddModelAsync(sp, p.Id, model));
    }

    private async Task DefaultsAsync(ChatModelRow model, params ChatModelRow[] fallbacks)
    {
        (await _client.PatchAsJsonAsync("/api/chat/settings/defaults", new { defaultTextModelId = model.Id.ToString() })).EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync("/api/chat/settings/fallbacks", new { fallbackModelIds = fallbacks.Select(f => f.Id).ToArray() }))
            .EnsureSuccessStatusCode();
    }

    private static List<(string Event, JsonElement Data)> Frames(string sse)
    {
        var frames = new List<(string, JsonElement)>();
        var lines = sse.Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
            if (lines[i].StartsWith("event: ", StringComparison.Ordinal) && lines[i + 1].StartsWith("data: ", StringComparison.Ordinal))
                frames.Add((lines[i]["event: ".Length..], JsonDocument.Parse(lines[i + 1]["data: ".Length..]).RootElement.Clone()));
        return frames;
    }

    private async Task<(string Sse, List<(string Event, JsonElement Data)> Frames)> TurnAsync(string message)
    {
        var response = await _client.PostAsJsonAsync("/api/chat/stream", new { message });
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return (text, Frames(text));
    }

    private async Task<(string Sse, List<(string Event, JsonElement Data)> Frames)> ConfirmAsync(Guid conversationId, string toolCallId, bool allow)
    {
        var response = await _client.PostAsJsonAsync($"/api/chat/stream/{conversationId}/confirm", new { toolCallId, allow });
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return (text, Frames(text));
    }

    private static JsonElement Done(List<(string Event, JsonElement Data)> frames) => frames.Single(f => f.Event == "done").Data;

    private JsonElement Sent(int index) => JsonDocument.Parse(_provider.Seen[index].Body).RootElement.Clone();

    // ── Anthropic answers ──────────────────────────────────────────────────────

    private static string Ev(object payload) => JsonSerializer.Serialize(payload);

    private static HttpResponseMessage AnthropicToolTurn(string thought, string signature, string id, string tool, string inputJson, string model = "claude-opus-5-5") =>
        ProviderTestKit.EventStream(
            ("message_start", Ev(new { type = "message_start", message = new { model, usage = new { input_tokens = 900, cache_read_input_tokens = 100, output_tokens = 1 } } })),
            ("content_block_start", Ev(new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "" } })),
            ("content_block_delta", Ev(new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = thought } })),
            ("content_block_delta", Ev(new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature } })),
            ("content_block_stop", Ev(new { type = "content_block_stop", index = 0 })),
            ("content_block_start", Ev(new { type = "content_block_start", index = 1, content_block = new { type = "tool_use", id, name = tool, input = new { } } })),
            ("content_block_delta", Ev(new { type = "content_block_delta", index = 1, delta = new { type = "input_json_delta", partial_json = inputJson } })),
            ("content_block_stop", Ev(new { type = "content_block_stop", index = 1 })),
            ("message_delta", Ev(new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 30 } })),
            ("message_stop", Ev(new { type = "message_stop" })));

    private static HttpResponseMessage AnthropicText(string text, string model = "claude-opus-5-5") =>
        ProviderTestKit.EventStream(
            ("message_start", Ev(new { type = "message_start", message = new { model, usage = new { input_tokens = 1000, output_tokens = 1 } } })),
            ("content_block_start", Ev(new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })),
            ("content_block_delta", Ev(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } })),
            ("content_block_stop", Ev(new { type = "content_block_stop", index = 0 })),
            ("message_delta", Ev(new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 5 } })),
            ("message_stop", Ev(new { type = "message_stop" })));

    // ── Gemini answers ─────────────────────────────────────────────────────────

    private static string GeminiChunk(object[] parts, string? finish, string model = "gemini-3-pro") => JsonSerializer.Serialize(new
    {
        candidates = new[] { new { content = new { role = "model", parts }, finishReason = finish, index = 0 } },
        usageMetadata = finish is null ? null : new { promptTokenCount = 1200, candidatesTokenCount = 20, thoughtsTokenCount = 10 },
        modelVersion = model,
    });

    private static HttpResponseMessage GeminiCallTurn(string tool, object args, string signature) =>
        ProviderTestKit.EventStream((null, GeminiChunk([new { functionCall = new { name = tool, args }, thoughtSignature = signature }], "STOP")));

    private static HttpResponseMessage GeminiText(string text) =>
        ProviderTestKit.EventStream((null, GeminiChunk([new { text }], null)), (null, GeminiChunk([new { text = "" }], "STOP")));

    // ── the tool loop ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Anthropic_TheNextRoundOfTheToolLoop_SendsTheThinkingBack_BeforeItsToolUse_AndTheUserNeverSeesIt()
    {
        var (_, model) = await ProviderAsync(ProviderProtocols.AnthropicMessages, "Anthropic", AnthropicBase, "claude-opus-5-5", "sk-ant-0001");
        await DefaultsAsync(model);
        _respond = (_, body) => body.Contains("tool_result")
            ? AnthropicText("Done.")
            : AnthropicToolTurn("secret-thought", "sig-1", "toolu_1", "bee_get_tree", "{}");

        var (sse, frames) = await TurnAsync("what is in my vault?");

        Done(frames).GetProperty("content").GetString().Should().Be("Done.");
        Done(frames).GetProperty("provider").GetString().Should().Be("Anthropic");
        Done(frames).GetProperty("promptTokens").GetInt32().Should().Be(1000);
        sse.Should().NotContain("secret-thought").And.NotContain("sig-1");
        _provider.Seen.Should().HaveCount(2);
        _provider.Seen.Should().OnlyContain(s => s.Url == AnthropicBase + "/messages" && s.Key == "sk-ant-0001");
        var turns = Sent(1).GetProperty("messages");
        var assistant = turns[1].GetProperty("content");
        assistant.EnumerateArray().Select(b => b.GetProperty("type").GetString()).Should().Equal("thinking", "tool_use");
        assistant[0].GetProperty("thinking").GetString().Should().Be("secret-thought");
        assistant[0].GetProperty("signature").GetString().Should().Be("sig-1");
        turns[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString().Should().Be("toolu_1");
    }

    [Fact]
    public async Task Gemini_TheNextRoundOfTheToolLoop_SendsTheThoughtSignatureBack_OnTheSamePart()
    {
        var (_, model) = await ProviderAsync(ProviderProtocols.Gemini, "Google Gemini", GeminiBase, "gemini-3-pro", "AIza-0001");
        await DefaultsAsync(model);
        _respond = (_, body) => body.Contains("functionResponse")
            ? GeminiText("Done.")
            : GeminiCallTurn("bee_get_tree", new { }, "gsig-1");

        var (sse, frames) = await TurnAsync("what is in my vault?");

        Done(frames).GetProperty("content").GetString().Should().Be("Done.");
        Done(frames).GetProperty("provider").GetString().Should().Be("Google Gemini");
        sse.Should().NotContain("gsig-1");
        _provider.Seen.Should().HaveCount(2);
        _provider.Seen[0].Url.Should().Be(GeminiBase + "/models/gemini-3-pro:streamGenerateContent?alt=sse");
        _provider.Seen.Should().OnlyContain(s => s.Key == "AIza-0001" && !s.Url.Contains("AIza"));
        var contents = Sent(1).GetProperty("contents");
        var call = contents[1].GetProperty("parts")[0];
        call.GetProperty("functionCall").GetProperty("name").GetString().Should().Be("bee_get_tree");
        call.GetProperty("thoughtSignature").GetString().Should().Be("gsig-1");
        contents[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("name").GetString().Should().Be("bee_get_tree");
    }

    // ── /confirm ───────────────────────────────────────────────────────────────

    private async Task<(Guid ConversationId, string ToolCallId)> PausedOnAWriteAsync(string message)
    {
        var (_, frames) = await TurnAsync(message);
        frames.Should().NotContain(f => f.Event == "done");
        var conversationId = frames.Single(f => f.Event == "conversation").Data.GetProperty("conversationId").GetGuid();
        var toolCallId = frames.Single(f => f.Event == "confirm_required").Data.GetProperty("toolCallId").GetString()!;
        return (conversationId, toolCallId);
    }

    /// <summary>The paused assistant row: its provider state is sealed (no thinking text or signature in the clear), and the ciphertext is
    /// bound to that row - put on another message it does not open.</summary>
    private async Task TheStoredStateIsSealedAndBoundToItsRow(Guid conversationId, params string[] secrets)
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        using (var chat = sp.GetRequiredService<ChatDbConnectionFactory>().CreateConnection())
        {
            var rows = (await chat.QueryAsync<(string Id, string Role, byte[]? Cipher, byte[]? Iv, long? Version)>(
                "SELECT id, role, provider_state_ciphertext, provider_state_iv, provider_state_key_v FROM chat_message WHERE conversation_id = @c",
                new { c = conversationId })).ToList();
            rows.Where(r => r.Role == "assistant").Should().HaveCount(2, "the paused tool-call turn and the answer after the confirmation");
            rows.Where(r => r.Cipher != null).Should().ContainSingle("only a tool-call turn keeps a provider state");
            var paused = rows.Single(r => r.Role == "assistant" && r.Cipher != null);
            paused.Cipher.Should().NotBeNull();
            paused.Iv.Should().NotBeNull();
            paused.Version.Should().Be(1);
            foreach (var secret in secrets)
                paused.Cipher!.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)).Should().BeLessThan(0, $"'{secret}' is sealed");
            var user = rows.First(r => r.Role == "user");
            await chat.ExecuteAsync("UPDATE chat_message SET provider_state_ciphertext = @c, provider_state_iv = @i, provider_state_key_v = 1 WHERE id = @id",
                new { c = paused.Cipher, i = paused.Iv, id = user.Id });
        }
        var history = await sp.GetRequiredService<ChatMessageRepository>().ListByConversationAsync(conversationId, sp.GetRequiredService<SessionService>());
        history.Single(m => m.Role == "assistant" && m.ToolCallsJson != null).ProviderStateJson.Should().NotBeNull();
        history.First(m => m.Role == "user").ProviderStateJson.Should().BeNull("a state moved onto another message does not open there");
    }

    [Fact]
    public async Task Anthropic_Confirm_ResumesTheLoop_WithTheThinkingOfThePausedTurn_FromItsSealedRow()
    {
        var (_, model) = await ProviderAsync(ProviderProtocols.AnthropicMessages, "Anthropic", AnthropicBase, "claude-opus-5-5", "sk-ant-0001");
        await DefaultsAsync(model);
        _respond = (_, body) => body.Contains("tool_result")
            ? AnthropicText("Not saved, as you wish.")
            : AnthropicToolTurn("plan-thought", "sig-2", "toolu_w", "bee_save_article", "{\"title\":\"T\",\"treePath\":\"/A\",\"content\":\"c\"}");

        var (conversationId, toolCallId) = await PausedOnAWriteAsync("save a note");
        toolCallId.Should().Be("toolu_w");
        var (_, frames) = await ConfirmAsync(conversationId, toolCallId, allow: false);

        Done(frames).GetProperty("content").GetString().Should().Be("Not saved, as you wish.");
        var turns = Sent(1).GetProperty("messages");
        var assistant = turns[1].GetProperty("content");
        assistant[0].GetProperty("type").GetString().Should().Be("thinking");
        assistant[0].GetProperty("signature").GetString().Should().Be("sig-2");
        assistant[0].GetProperty("thinking").GetString().Should().Be("plan-thought");
        assistant[1].GetProperty("id").GetString().Should().Be("toolu_w");
        var result = turns[2].GetProperty("content")[0];
        result.GetProperty("tool_use_id").GetString().Should().Be("toolu_w");
        result.GetProperty("content").GetString().Should().Contain("User denied");
        await TheStoredStateIsSealedAndBoundToItsRow(conversationId, "plan-thought", "sig-2");
    }

    [Fact]
    public async Task Gemini_Confirm_ResumesTheLoop_WithTheSignatureOfThePausedTurn_FromItsSealedRow()
    {
        var (_, model) = await ProviderAsync(ProviderProtocols.Gemini, "Google Gemini", GeminiBase, "gemini-3-pro", "AIza-0001");
        await DefaultsAsync(model);
        _respond = (_, body) => body.Contains("functionResponse")
            ? GeminiText("Not saved.")
            : GeminiCallTurn("bee_save_article", new { title = "T", treePath = "/A", content = "c" }, "gsig-2");

        var (conversationId, toolCallId) = await PausedOnAWriteAsync("save a note");
        toolCallId.Should().StartWith(GeminiClient.SyntheticIdPrefix, "Gemini gave the call no id");
        var (_, frames) = await ConfirmAsync(conversationId, toolCallId, allow: false);

        Done(frames).GetProperty("content").GetString().Should().Be("Not saved.");
        var contents = Sent(1).GetProperty("contents");
        var call = contents[1].GetProperty("parts")[0];
        call.GetProperty("thoughtSignature").GetString().Should().Be("gsig-2");
        call.GetProperty("functionCall").TryGetProperty("id", out _).Should().BeFalse();
        var response = contents[2].GetProperty("parts")[0].GetProperty("functionResponse");
        response.GetProperty("name").GetString().Should().Be("bee_save_article");
        response.GetProperty("response").GetProperty("error").GetString().Should().Be("User denied this action.");
        _provider.Seen[1].Body.Should().NotContain(toolCallId, "a made-up id is never sent");
        await TheStoredStateIsSealedAndBoundToItsRow(conversationId, "gsig-2");
    }

    // ── the fallback chain moves to another model ──────────────────────────────

    [Fact]
    public async Task Anthropic_WhenTheChainMovesToAnotherModel_MidLoop_TheThinkingIsDropped()
    {
        var (_, a) = await ProviderAsync(ProviderProtocols.AnthropicMessages, "Provider A", AnthropicBase, "claude-opus-5-5", "sk-ant-a");
        var (_, b) = await ProviderAsync(ProviderProtocols.AnthropicMessages, "Provider B", AnthropicBase, "claude-sonnet-5-5", "sk-ant-b");
        await DefaultsAsync(a, b);
        _respond = (req, body) => req.Headers.GetValues("x-api-key").Single() == "sk-ant-a"
            ? body.Contains("tool_result") ? ProviderTestKit.Json((HttpStatusCode)529, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}")
                : AnthropicToolTurn("secret-thought", "sig-a", "toolu_1", "bee_get_tree", "{}")
            : AnthropicText("From B.", "claude-sonnet-5-5");

        var (_, frames) = await TurnAsync("what is in my vault?");

        Done(frames).GetProperty("content").GetString().Should().Be("From B.");
        Done(frames).GetProperty("provider").GetString().Should().Be("Provider B");
        _provider.Seen.Select(s => s.Key).Should().Equal("sk-ant-a", "sk-ant-a", "sk-ant-b");
        _provider.Seen[2].Body.Should().NotContain("secret-thought").And.NotContain("sig-a");
        var turns = Sent(2).GetProperty("messages");
        Sent(2).GetProperty("model").GetString().Should().Be("claude-sonnet-5-5");
        turns[1].GetProperty("content").EnumerateArray().Select(x => x.GetProperty("type").GetString()).Should().Equal("tool_use");
        turns[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString().Should().Be("toolu_1");
    }

    [Fact]
    public async Task Gemini_WhenTheChainMovesToAnotherModel_MidLoop_TheSignatureIsDropped_AndTheCallMarkedAsWrittenElsewhere()
    {
        var (_, a) = await ProviderAsync(ProviderProtocols.Gemini, "Provider A", GeminiBase, "gemini-3-pro", "AIza-a");
        var (_, b) = await ProviderAsync(ProviderProtocols.Gemini, "Provider B", GeminiBase, "gemini-3-flash", "AIza-b");
        await DefaultsAsync(a, b);
        _respond = (req, body) => req.Headers.GetValues("x-goog-api-key").Single() == "AIza-a"
            ? body.Contains("functionResponse") ? ProviderTestKit.Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":503,\"status\":\"UNAVAILABLE\"}}")
                : GeminiCallTurn("bee_get_tree", new { }, "gsig-a")
            : GeminiText("From B.");

        var (_, frames) = await TurnAsync("what is in my vault?");

        Done(frames).GetProperty("content").GetString().Should().Be("From B.");
        Done(frames).GetProperty("provider").GetString().Should().Be("Provider B");
        _provider.Seen.Select(s => s.Key).Should().Equal("AIza-a", "AIza-a", "AIza-b");
        _provider.Seen[2].Url.Should().Contain("/models/gemini-3-flash:");
        _provider.Seen[2].Body.Should().NotContain("gsig-a");
        Sent(2).GetProperty("contents")[1].GetProperty("parts")[0].GetProperty("thoughtSignature").GetString().Should().Be(GeminiClient.SkipSignature);
    }

    // ── a refusal or a safety block is an answer, not a failure (review prov-r2, finding 2) ─────────────────

    /// <summary>Provider A (<paramref name="protocol"/>, two keys) holds the default text model, provider B (OpenAI format) its fallback.</summary>
    private async Task<(ChatProvider A, ChatProvider B)> RefusalChainAsync(string protocol)
    {
        var anthropic = protocol == ProviderProtocols.AnthropicMessages;
        var (a, modelA) = await ProviderAsync(protocol, "Provider A", anthropic ? AnthropicBase : GeminiBase, anthropic ? "claude-opus-5-5" : "gemini-3-pro",
            "key-a-first", "key-a-second");
        var (b, modelB) = await ProviderAsync(ProviderProtocols.OpenAiChat, "Provider B", "https://b.example/v1", "model-b", "key-b-only");
        await DefaultsAsync(modelA, modelB);
        return (a, b);
    }

    private async Task TheRefusalIsShown_NothingElseIsAsked_AndNoKeyIsMarked(ChatProvider a, ChatProvider b, string expected)
    {
        var (sse, frames) = await TurnAsync("something the model will not do");

        frames.Should().NotContain(f => f.Event == "done");
        var error = frames.Single(f => f.Event == "error").Data.GetProperty("error").GetString();
        error.Should().Contain(expected).And.Contain("Provider A");
        _provider.Seen.Should().ContainSingle("a refusal is the model's answer: no other key and no fallback model is asked")
            .Which.Key.Should().Be("key-a-first");
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ChatSettingsRepository>();
        (await repo.ListByProviderAsync(a.Id)).Should().OnlyContain(k => k.Enabled && k.DisabledUntil == null && k.LastError == null,
            "a refusal is not a key's failure");
        (await repo.ListByProviderAsync(b.Id)).Should().OnlyContain(k => k.LastUsedAt == null, "the fallback was never asked");
        sse.Should().NotContain("event: delta");
    }

    [Fact]
    public async Task Anthropic_ARefusal_IsShownPlainly_WithOneRequest_NoOtherKey_NoFallback_AndNoKeyMarked()
    {
        var (a, b) = await RefusalChainAsync(ProviderProtocols.AnthropicMessages);
        _respond = (_, _) => ProviderTestKit.EventStream(
            ("message_start", Ev(new { type = "message_start", message = new { model = "claude-opus-5-5", usage = new { input_tokens = 50, output_tokens = 1 } } })),
            ("message_delta", Ev(new { type = "message_delta", delta = new { stop_reason = "refusal" }, usage = new { output_tokens = 1 } })),
            ("message_stop", Ev(new { type = "message_stop" })));

        await TheRefusalIsShown_NothingElseIsAsked_AndNoKeyIsMarked(a, b, "declined to answer");
    }

    [Fact]
    public async Task Gemini_ASafetyBlock_IsShownPlainly_WithOneRequest_NoOtherKey_NoFallback_AndNoKeyMarked()
    {
        var (a, b) = await RefusalChainAsync(ProviderProtocols.Gemini);
        _respond = (_, _) => ProviderTestKit.EventStream((null, GeminiChunk([], "SAFETY")));

        await TheRefusalIsShown_NothingElseIsAsked_AndNoKeyIsMarked(a, b, "blocked the answer (SAFETY)");
    }

    // ── keys fail over as everywhere ───────────────────────────────────────────

    private static HttpResponseMessage TimesOut() =>
        throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException());

    [Theory]
    [InlineData(1, ProviderProtocols.AnthropicMessages)]
    [InlineData(2, ProviderProtocols.Gemini)]
    public async Task TheKeysOfANativeProvider_FailOverAsEveryProvidersDo(int row, string protocol)
    {
        var anthropic = protocol == ProviderProtocols.AnthropicMessages;
        var (provider, model) = await ProviderAsync(protocol, "Native", anthropic ? AnthropicBase : GeminiBase,
            anthropic ? "claude-opus-5-5" : "gemini-3-pro", "key-1-refused", "key-2-limited", "key-3-overloaded", "key-4-stalls", "key-5-good");
        await DefaultsAsync(model);
        _respond = (req, _) =>
        {
            var key = req.Headers.TryGetValues(anthropic ? "x-api-key" : "x-goog-api-key", out var v) ? v.Single() : "";
            return key switch
            {
                "key-1-refused" => anthropic
                    ? ProviderTestKit.Json(HttpStatusCode.Unauthorized, "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}")
                    : ProviderTestKit.Json(HttpStatusCode.BadRequest,
                        "{\"error\":{\"code\":400,\"message\":\"API key not valid.\",\"status\":\"INVALID_ARGUMENT\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"API_KEY_INVALID\"}]}}"),
                "key-2-limited" => anthropic
                    ? ProviderTestKit.Json(HttpStatusCode.TooManyRequests, "{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"slow down\"}}")
                    : ProviderTestKit.Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\"}}"),
                "key-3-overloaded" => anthropic
                    ? ProviderTestKit.Json((HttpStatusCode)529, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}")
                    : ProviderTestKit.Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":503,\"status\":\"UNAVAILABLE\"}}"),
                "key-4-stalls" => TimesOut(),
                _ => anthropic ? AnthropicText("Answered.") : GeminiText("Answered."),
            };
        };

        var (_, frames) = await TurnAsync("hello");

        Done(frames).GetProperty("content").GetString().Should().Be("Answered.", $"row {row}");
        _provider.Seen.Select(s => s.Key).Should().Equal("key-1-refused", "key-2-limited", "key-3-overloaded", "key-4-stalls", "key-5-good");
        using var scope = _factory.Services.CreateScope();
        var keys = (await scope.ServiceProvider.GetRequiredService<ChatSettingsRepository>().ListByProviderAsync(provider.Id)).OrderBy(k => k.Priority).ToList();
        keys[0].Enabled.Should().BeFalse("a refused key (401, or Google's API_KEY_INVALID) is disabled");
        keys[1].Enabled.Should().BeTrue();
        keys[1].DisabledUntil.Should().NotBeNull("a 429 pauses the key");
        keys[2].Enabled.Should().BeTrue();
        keys[2].DisabledUntil.Should().BeNull("an overloaded provider is not the key's fault");
        keys[2].LastError.Should().Contain(anthropic ? "overloaded_error" : "UNAVAILABLE");
        keys[3].DisabledUntil.Should().BeNull();
        keys[3].LastError.Should().Be(ModelRouter.NoAnswerInTime);
    }

    // ── Fetch models, custom providers ─────────────────────────────────────────

    [Fact]
    public async Task FetchModels_OfAGeminiProvider_PagesThroughItsList()
    {
        var (provider, _) = await ProviderAsync(ProviderProtocols.Gemini, "Google Gemini", GeminiBase, "gemini-3-pro", "AIza-0001");
        _respond = (req, _) => req.RequestUri!.Query.Contains("pageToken=next")
            ? ProviderTestKit.Json(HttpStatusCode.OK, "{\"models\":[{\"name\":\"models/gemini-3-flash\",\"supportedGenerationMethods\":[\"generateContent\"]}]}")
            : ProviderTestKit.Json(HttpStatusCode.OK, "{\"models\":[{\"name\":\"models/gemini-3-pro\",\"inputTokenLimit\":1048576,\"supportedGenerationMethods\":[\"generateContent\"]}],\"nextPageToken\":\"next\"}");

        var response = await _client.PostAsync($"/api/chat/providers/{provider.Id}/fetch-models", null);

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        JsonDocument.Parse(text).RootElement.EnumerateArray().Select(m => m.GetProperty("id").GetString()).Should().Equal("gemini-3-flash", "gemini-3-pro");
        _provider.Seen.Should().HaveCount(2);
        _provider.Seen.Should().OnlyContain(s => s.Key == "AIza-0001" && !s.Url.Contains("key="));
    }

    [Fact]
    public async Task TheAnthropicAndGooglePresets_MakeNativeProviders_AndAreOffered()
    {
        var offered = await _client.GetFromJsonAsync<JsonElement>("/api/chat/providers/presets");
        offered.EnumerateArray().Where(p => p.GetProperty("id").GetString() is "anthropic" or "google")
            .Select(p => p.GetProperty("protocol").GetString()).Should().BeEquivalentTo(["anthropic-messages", "gemini"]);

        var anthropic = await (await _client.PostAsJsonAsync("/api/chat/providers", new { presetId = "anthropic" })).Content.ReadFromJsonAsync<JsonElement>();
        var google = await (await _client.PostAsJsonAsync("/api/chat/providers", new { presetId = "google" })).Content.ReadFromJsonAsync<JsonElement>();

        anthropic.GetProperty("protocol").GetString().Should().Be("anthropic-messages");
        anthropic.GetProperty("baseUrl").GetString().Should().Be(AnthropicBase);
        anthropic.GetProperty("host").GetString().Should().Be("api.anthropic.com");
        anthropic.GetProperty("options").GetProperty("promptCaching").GetBoolean().Should().BeTrue();
        google.GetProperty("protocol").GetString().Should().Be("gemini");
        google.GetProperty("baseUrl").GetString().Should().Be(GeminiBase);
        google.GetProperty("options").GetProperty("reasoning").GetString().Should().Be("gemini");
    }

    [Fact]
    public async Task ACustomProviderOfANativeProtocol_GetsThatProtocolsOptions_AndOptionsOfAnotherProtocolAreRefused()
    {
        var anthropic = await _client.PostAsJsonAsync("/api/chat/providers",
            new { name = "My Anthropic", protocol = "anthropic-messages", baseUrl = "https://api.anthropic.com/v1" });
        var gemini = await _client.PostAsJsonAsync("/api/chat/providers",
            new { name = "My Gemini", protocol = "gemini", baseUrl = "https://generativelanguage.googleapis.com/v1beta" });
        var wrongStyle = await _client.PostAsJsonAsync("/api/chat/providers",
            new { name = "Odd", protocol = "anthropic-messages", baseUrl = "https://api.anthropic.com/v1", options = new { reasoning = "openrouter" } });
        var imageGen = await _client.PostAsJsonAsync("/api/chat/providers",
            new { name = "Odd 2", protocol = "gemini", baseUrl = "https://generativelanguage.googleapis.com/v1beta", options = new { reasoning = "gemini", imageGeneration = true } });

        anthropic.StatusCode.Should().Be(HttpStatusCode.Created, await anthropic.Content.ReadAsStringAsync());
        var a = await anthropic.Content.ReadFromJsonAsync<JsonElement>();
        a.GetProperty("protocol").GetString().Should().Be("anthropic-messages");
        a.GetProperty("options").GetProperty("reasoning").GetString().Should().Be("anthropic");
        a.GetProperty("options").GetProperty("promptCaching").GetBoolean().Should().BeTrue();
        a.GetProperty("clientAvailable").GetBoolean().Should().BeTrue();
        gemini.StatusCode.Should().Be(HttpStatusCode.Created);
        (await gemini.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("options").GetProperty("reasoning").GetString().Should().Be("gemini");
        wrongStyle.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrongStyle.Content.ReadAsStringAsync()).Should().Contain("anthropic-messages");
        imageGen.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A provider switched to another protocol without options of its own gets that protocol's defaults.
        var id = a.GetProperty("id").GetGuid();
        var switched = await _client.PatchAsJsonAsync($"/api/chat/providers/{id}",
            new { protocol = "gemini", baseUrl = "https://generativelanguage.googleapis.com/v1beta" });
        switched.StatusCode.Should().Be(HttpStatusCode.OK, await switched.Content.ReadAsStringAsync());
        var after = await switched.Content.ReadFromJsonAsync<JsonElement>();
        after.GetProperty("options").GetProperty("reasoning").GetString().Should().Be("gemini");
        after.GetProperty("approved").GetBoolean().Should().BeFalse();
        _provider.Seen.Should().BeEmpty();
    }
}
