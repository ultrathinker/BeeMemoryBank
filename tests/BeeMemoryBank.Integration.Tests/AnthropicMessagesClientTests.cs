using System.Net;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Providers;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Anthropic Messages client (<see cref="AnthropicMessagesClient"/>) against a scripted provider: what it sends (system on top, turns
/// that alternate, tools and their results, images, the output limit, never a sampling field, the key in <c>x-api-key</c>), what it reads
/// (text, several tool calls, usage with the cache, cut-off, refusal), the stream (text deltas, tool input from fragments, usage, an error
/// event), the thinking blocks sent back unmodified only to the same model, error statuses and Fetch models with paging.
/// </summary>
public class AnthropicMessagesClientTests
{
    private static readonly Guid ProviderId = Guid.NewGuid();
    private const string Model = "claude-opus-5-5";
    private const string Image = "data:image/png;base64,iVBORw0KGgo=";

    private static ProviderEgressTarget Target(ProviderWireOptions? options = null, Guid? providerId = null) =>
        new(providerId ?? ProviderId, "Anthropic", ProviderProtocols.AnthropicMessages, new Uri("https://api.anthropic.com/v1"), false,
            options ?? ProviderWireOptions.AnthropicNative);

    private static ModelCall Call(ProviderWireOptions? options = null, string model = Model, Guid? providerId = null) =>
        new(Target(options, providerId), "sk-ant-test-key-0001", model);

    private static HttpResponseMessage Message(object[] content, string stop = "end_turn", object? usage = null, string model = Model) =>
        ProviderTestKit.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            id = "msg_1", type = "message", role = "assistant", model, content, stop_reason = stop,
            usage = usage ?? new { input_tokens = 100, output_tokens = 20, cache_creation_input_tokens = 30, cache_read_input_tokens = 400 },
        }));

    private static object Text(string text) => new { type = "text", text };

    private static string Ev(object payload) => JsonSerializer.Serialize(payload);

    private static JsonElement Body(ScriptedProviderHandler handler, int index = 0) => JsonDocument.Parse(handler.Seen[index].Body).RootElement.Clone();

    private static ChatToolMessage Assistant(string? content, params (string Id, string Name, string Args)[] calls) => new()
    {
        Role = "assistant", Content = content,
        ToolCalls = calls.Select(c => new ChatToolCall { Id = c.Id, Function = new ChatToolCallFunction { Name = c.Name, Arguments = c.Args } }).ToList(),
    };

    [Fact]
    public async Task TheRequest_HasTheSystemPromptOnTop_TurnsThatAlternate_ToolsWithTheirResults_AndImages()
    {
        var handler = new ScriptedProviderHandler((_, _) => Message([Text("ok")]));
        List<ChatToolMessage> messages =
        [
            new() { Role = "system", Content = "You are Bee." },
            new() { Role = "user", Content = "hello" },
            new() { Role = "user", Content = "second" },
            Assistant("Let me look.", ("toolu_1", "bee_search", "{\"keywords\":\"a\"}"), ("toolu_2", "bee_get_tree", "{}")),
            new() { Role = "tool", ToolCallId = "toolu_1", Content = "{\"r\":1}" },
            new() { Role = "tool", ToolCallId = "toolu_2", Content = "{\"r\":2}" },
            new() { Role = "system", Content = "[Images attached to the previous message]" },
            new() { Role = "user", Content = "and this?", ImageDataUrls = [Image] },
        ];

        var turn = await ProviderTestKit.Anthropic(handler).CompleteWithToolsAsync(Call(), messages, ChatToolDispatcher.ToolDefinitions, CancellationToken.None);

        turn.Content.Should().Be("ok");
        var seen = handler.Seen.Single();
        seen.Url.Should().Be("https://api.anthropic.com/v1/messages");
        seen.Key.Should().Be("sk-ant-test-key-0001");
        var headers = handler.Headers.Single();
        headers["x-api-key"].Should().Be("sk-ant-test-key-0001");
        headers["anthropic-version"].Should().Be("2026-10-21");
        headers.Should().NotContainKey("authorization");

        var body = Body(handler);
        body.GetProperty("model").GetString().Should().Be(Model);
        body.GetProperty("max_tokens").GetInt32().Should().Be(16000, "max_tokens is required; the chat gives none");
        foreach (var field in new[] { "temperature", "top_p", "top_k", "thinking", "output_config", "stream" })
            body.TryGetProperty(field, out _).Should().BeFalse($"'{field}' is not sent by the chat's call");

        var system = body.GetProperty("system");
        system.GetArrayLength().Should().Be(1, "only the leading system message is the system prompt");
        system[0].GetProperty("text").GetString().Should().Be("You are Bee.");
        system[0].GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");

        var turns = body.GetProperty("messages");
        turns.EnumerateArray().Select(m => m.GetProperty("role").GetString()).Should().Equal("user", "assistant", "user");
        turns[0].GetProperty("content").EnumerateArray().Select(b => b.GetProperty("text").GetString()).Should().Equal("hello", "second");
        var assistant = turns[1].GetProperty("content");
        assistant.EnumerateArray().Select(b => b.GetProperty("type").GetString()).Should().Equal("text", "tool_use", "tool_use");
        assistant[1].GetProperty("id").GetString().Should().Be("toolu_1");
        assistant[1].GetProperty("input").GetProperty("keywords").GetString().Should().Be("a");
        assistant[2].GetProperty("input").ValueKind.Should().Be(JsonValueKind.Object);
        var last = turns[2].GetProperty("content");
        last.EnumerateArray().Select(b => b.GetProperty("type").GetString()).Should().Equal("tool_result", "tool_result", "text", "image", "text");
        last[0].GetProperty("tool_use_id").GetString().Should().Be("toolu_1");
        last[0].GetProperty("content").GetString().Should().Be("{\"r\":1}");
        last[1].GetProperty("tool_use_id").GetString().Should().Be("toolu_2");
        last[2].GetProperty("text").GetString().Should().Be("[Images attached to the previous message]", "a later system note stays where it was");
        var source = last[3].GetProperty("source");
        source.GetProperty("type").GetString().Should().Be("base64");
        source.GetProperty("media_type").GetString().Should().Be("image/png");
        source.GetProperty("data").GetString().Should().Be("iVBORw0KGgo=");
        last[4].GetProperty("text").GetString().Should().Be("and this?");

        var tools = body.GetProperty("tools");
        tools.GetArrayLength().Should().Be(ChatToolDispatcher.ToolDefinitions.Count);
        foreach (var (tool, i) in tools.EnumerateArray().Select((t, i) => (t, i)))
        {
            tool.GetProperty("name").GetString().Should().Be(ChatToolDispatcher.ToolDefinitions[i].Function.Name);
            tool.GetProperty("input_schema").GetProperty("type").GetString().Should().Be("object");
            tool.TryGetProperty("cache_control", out _).Should().Be(i == tools.GetArrayLength() - 1, "only the last tool marks the cache");
        }
    }

    [Fact]
    public async Task ACallLeftWithoutAResult_GetsANotRunResult_AndAnotherProvidersIdIsMadeValid()
    {
        var handler = new ScriptedProviderHandler((_, _) => Message([Text("ok")]));
        List<ChatToolMessage> messages =
        [
            new() { Role = "user", Content = "save it and look" },
            Assistant(null, ("call_w1", "bee_save_article", "{\"title\":\"t\"}"), ("functions.bee_search:0", "bee_search", "{}")),
            new() { Role = "tool", ToolCallId = "call_w1", Content = "{\"error\":\"User denied this action.\"}" },
            new() { Role = "user", Content = "next" },
        ];

        await ProviderTestKit.Anthropic(handler).CompleteWithToolsAsync(Call(), messages, null, CancellationToken.None);

        var turns = Body(handler).GetProperty("messages");
        var foreign = AnthropicMessagesClient.WireId("functions.bee_search:0");
        foreign.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        turns[1].GetProperty("content")[1].GetProperty("id").GetString().Should().Be(foreign);
        var user = turns[2].GetProperty("content");
        user.EnumerateArray().Select(b => b.GetProperty("type").GetString()).Should().Equal("tool_result", "tool_result", "text");
        user[0].GetProperty("tool_use_id").GetString().Should().Be("call_w1");
        user[1].GetProperty("tool_use_id").GetString().Should().Be(foreign);
        user[1].GetProperty("is_error").GetBoolean().Should().BeTrue();
        user[1].GetProperty("content").GetString().Should().Contain("not run");
        Body(handler).TryGetProperty("tools", out _).Should().BeFalse();
    }

    [Fact]
    public async Task DreamsCall_SendsItsOutputLimitAndTheEffort_ButNoTemperature()
    {
        var handler = new ScriptedProviderHandler((_, _) => Message([Text("{}")]));
        var client = ProviderTestKit.Anthropic(handler);
        List<ChatRoleMessage> messages = [new("system", "rules"), new("user", "notes")];

        await client.CompleteAsync(Call(), messages, new CompletionOptions(500, 0.0, "low"), CancellationToken.None);
        await client.CompleteAsync(Call(new ProviderWireOptions { Reasoning = ReasoningStyles.None, PromptCaching = false }), messages,
            new CompletionOptions(500, 0.0, "low"), CancellationToken.None);

        var withEffort = Body(handler, 0);
        withEffort.GetProperty("max_tokens").GetInt32().Should().Be(500);
        withEffort.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("low");
        withEffort.TryGetProperty("temperature", out _).Should().BeFalse("this API version refuses any sampling field; Dream's temperature 0 is dropped");
        withEffort.TryGetProperty("thinking", out _).Should().BeFalse("current models refuse budget_tokens and a disabled thinking");
        withEffort.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");

        var plain = Body(handler, 1);
        plain.TryGetProperty("output_config", out _).Should().BeFalse("the reasoning style 'none' sends no effort");
        handler.Seen[1].Body.Should().NotContain("cache_control", "prompt caching is off for this provider");
    }

    [Fact]
    public async Task TheAnswer_GivesItsText_EveryToolCall_TheUsageWithTheCache_AndTheThinkingForTheNextRound()
    {
        var handler = new ScriptedProviderHandler((_, _) => Message(
        [
            new { type = "thinking", thinking = "secret-thought", signature = "sig-1" },
            Text("I will search."),
            new { type = "tool_use", id = "toolu_1", name = "bee_search", input = new { keywords = "x" } },
            new { type = "tool_use", id = "toolu_2", name = "bee_get_tree", input = new { } },
        ], stop: "tool_use"));

        var turn = await ProviderTestKit.Anthropic(handler).CompleteWithToolsAsync(Call(),
            [new ChatToolMessage { Role = "user", Content = "find x" }], ChatToolDispatcher.ToolDefinitions, CancellationToken.None);

        turn.Content.Should().Be("I will search.", "thinking text is never part of the answer");
        turn.ToolCalls!.Select(c => (c.Id, c.Name)).Should().Equal(("toolu_1", "bee_search"), ("toolu_2", "bee_get_tree"));
        JsonDocument.Parse(turn.ToolCalls![0].ArgumentsJson).RootElement.GetProperty("keywords").GetString().Should().Be("x");
        turn.PromptTokens.Should().Be(530, "input + cache creation + cache read");
        turn.CompletionTokens.Should().Be(20);
        turn.ProviderState.Should().NotBeNull();
        turn.ProviderState!.IsFor(ProviderProtocols.AnthropicMessages, ProviderId, Model).Should().BeTrue();
        turn.ProviderState.Content[0].GetProperty("signature").GetString().Should().Be("sig-1");
    }

    [Fact]
    public async Task APlainCompletion_SaysWhenTheLimitCutItOff_WhenTheModelThought_AndWhenItRefused()
    {
        var answers = new Queue<HttpResponseMessage>(
        [
            Message([Text("half")], stop: "max_tokens"),
            Message([new { type = "thinking", thinking = "t", signature = "s" }], stop: "max_tokens"),
            Message([Text("")], stop: "refusal"),
        ]);
        var client = ProviderTestKit.Anthropic(new ScriptedProviderHandler((_, _) => answers.Dequeue()));
        List<ChatRoleMessage> messages = [new("user", "x")];

        var cut = await client.CompleteAsync(Call(), messages, new CompletionOptions(100), CancellationToken.None);
        var thought = await client.CompleteAsync(Call(), messages, new CompletionOptions(100), CancellationToken.None);
        var refused = await client.CompleteAsync(Call(), messages, new CompletionOptions(100), CancellationToken.None);

        cut.FinishReason.Should().Be("length");
        cut.PromptTokens.Should().Be(530);
        thought.HasReasoning.Should().BeTrue();
        thought.Content.Should().BeEmpty();
        refused.FinishReason.Should().Be("refusal");
    }

    [Fact]
    public async Task ARefusal_InTheChat_IsAClearMessage_AndNotRetried()
    {
        var handler = new ScriptedProviderHandler((_, _) => Message([Text("")], stop: "refusal"));
        var call = () => ProviderTestKit.Anthropic(handler).CompleteWithToolsAsync(Call(), [new ChatToolMessage { Role = "user", Content = "x" }], null,
            CancellationToken.None);

        (await call.Should().ThrowAsync<ProviderRefusalException>()).WithMessage("*declined to answer*");
        handler.Seen.Should().ContainSingle();
    }

    private static HttpResponseMessage ToolStream() => ProviderTestKit.EventStream(
        ("message_start", Ev(new { type = "message_start", message = new { id = "msg_1", model = Model, content = Array.Empty<object>(), usage = new { input_tokens = 50, cache_read_input_tokens = 1000, output_tokens = 1 } } })),
        ("ping", Ev(new { type = "ping" })),
        ("content_block_start", Ev(new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "" } })),
        ("content_block_delta", Ev(new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "secret-thought" } })),
        ("content_block_delta", Ev(new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = "sig-abc" } })),
        ("content_block_stop", Ev(new { type = "content_block_stop", index = 0 })),
        ("content_block_start", Ev(new { type = "content_block_start", index = 1, content_block = new { type = "text", text = "" } })),
        ("content_block_delta", Ev(new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = "Hel" } })),
        ("content_block_delta", Ev(new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = "lo" } })),
        ("content_block_stop", Ev(new { type = "content_block_stop", index = 1 })),
        ("content_block_start", Ev(new { type = "content_block_start", index = 2, content_block = new { type = "tool_use", id = "toolu_9", name = "bee_search", input = new { } } })),
        ("content_block_delta", Ev(new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = "{\"key" } })),
        ("content_block_delta", Ev(new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = "words\": \"bees\"}" } })),
        ("content_block_stop", Ev(new { type = "content_block_stop", index = 2 })),
        ("message_delta", Ev(new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 42 } })),
        ("message_stop", Ev(new { type = "message_stop" })));

    [Fact]
    public async Task AStream_ForwardsTheText_AssemblesToolInputFromItsFragments_KeepsTheThinking_AndCountsTheUsage()
    {
        var handler = new ScriptedProviderHandler((_, _) => ToolStream());
        var deltas = new List<string>();

        var turn = await ProviderTestKit.Anthropic(handler).StreamWithToolsAsync(Call(), [new ChatToolMessage { Role = "user", Content = "bees?" }],
            ChatToolDispatcher.ToolDefinitions, (d, _) => { deltas.Add(d); return ValueTask.CompletedTask; }, CancellationToken.None);

        Body(handler).GetProperty("stream").GetBoolean().Should().BeTrue();
        deltas.Should().Equal(["Hel", "lo"], "thinking is never shown");
        turn.Content.Should().Be("Hello");
        turn.ToolCalls.Should().ContainSingle();
        turn.ToolCalls![0].Id.Should().Be("toolu_9");
        JsonDocument.Parse(turn.ToolCalls[0].ArgumentsJson).RootElement.GetProperty("keywords").GetString().Should().Be("bees");
        turn.PromptTokens.Should().Be(1050);
        turn.CompletionTokens.Should().Be(42);
        var state = turn.ProviderState!.Content;
        state.EnumerateArray().Select(b => b.GetProperty("type").GetString()).Should().Equal("thinking", "text", "tool_use");
        state[0].GetProperty("thinking").GetString().Should().Be("secret-thought");
        state[0].GetProperty("signature").GetString().Should().Be("sig-abc");
        state[1].GetProperty("text").GetString().Should().Be("Hello");
        state[2].GetProperty("input").GetProperty("keywords").GetString().Should().Be("bees");
    }

    [Fact]
    public async Task AnErrorEvent_AfterTheFirstDelta_IsAnErrorWithItsStatus_AndAStreamThatStopsShort_BrokeOff()
    {
        var overloaded = new ScriptedProviderHandler((_, _) => ProviderTestKit.EventStream(
            ("message_start", Ev(new { type = "message_start", message = new { model = Model, usage = new { input_tokens = 5, output_tokens = 1 } } })),
            ("content_block_start", Ev(new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })),
            ("content_block_delta", Ev(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Hi" } })),
            ("error", Ev(new { type = "error", error = new { type = "overloaded_error", message = "Overloaded" } }))));
        var deltas = new List<string>();

        var stream = () => ProviderTestKit.Anthropic(overloaded).StreamWithToolsAsync(Call(), [new ChatToolMessage { Role = "user", Content = "x" }], null,
            (d, _) => { deltas.Add(d); return ValueTask.CompletedTask; }, CancellationToken.None);

        (await stream.Should().ThrowAsync<ProviderHttpException>()).Which.StatusCode.Should().Be(529);
        deltas.Should().Equal("Hi");

        var cut = new ScriptedProviderHandler((_, _) => ProviderTestKit.EventStream(
            ("message_start", Ev(new { type = "message_start", message = new { model = Model } })),
            ("content_block_start", Ev(new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })),
            ("content_block_delta", Ev(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Hi" } }))));
        var broken = () => ProviderTestKit.Anthropic(cut).StreamWithToolsAsync(Call(), [new ChatToolMessage { Role = "user", Content = "x" }], null,
            (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        await broken.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task TheNextRound_SendsTheThinkingBackUnmodified_ButOnlyToTheSameProviderAndModel()
    {
        var first = new ScriptedProviderHandler((_, _) => ToolStream());
        var turn = await ProviderTestKit.Anthropic(first).StreamWithToolsAsync(Call(), [new ChatToolMessage { Role = "user", Content = "bees?" }],
            ChatToolDispatcher.ToolDefinitions, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        var assistant = Assistant(turn.Content, ("toolu_9", "bee_search", turn.ToolCalls![0].ArgumentsJson));
        assistant.ProviderState = turn.ProviderState;
        List<ChatToolMessage> loop =
        [
            new() { Role = "user", Content = "bees?" },
            assistant,
            new() { Role = "tool", ToolCallId = "toolu_9", Content = "{\"articles\":[]}" },
        ];

        var next = new ScriptedProviderHandler((_, _) => Message([Text("none")]));
        var client = ProviderTestKit.Anthropic(next);
        await client.CompleteWithToolsAsync(Call(), loop, ChatToolDispatcher.ToolDefinitions, CancellationToken.None);
        await client.CompleteWithToolsAsync(Call(model: "claude-sonnet-5-5"), loop, ChatToolDispatcher.ToolDefinitions, CancellationToken.None);
        await client.CompleteWithToolsAsync(Call(providerId: Guid.NewGuid()), loop, ChatToolDispatcher.ToolDefinitions, CancellationToken.None);

        var same = Body(next, 0).GetProperty("messages")[1].GetProperty("content");
        same.EnumerateArray().Select(b => b.GetProperty("type").GetString()).Should().Equal("thinking", "text", "tool_use");
        same[0].GetProperty("thinking").GetString().Should().Be("secret-thought");
        same[0].GetProperty("signature").GetString().Should().Be("sig-abc");
        Body(next, 0).GetProperty("messages")[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString().Should().Be("toolu_9");
        foreach (var other in new[] { 1, 2 })
        {
            var rebuilt = Body(next, other).GetProperty("messages")[1].GetProperty("content");
            rebuilt.EnumerateArray().Select(b => b.GetProperty("type").GetString()).Should().Equal(["text", "tool_use"], "another model gets no thinking");
            next.Seen[other].Body.Should().NotContain("sig-abc").And.NotContain("secret-thought");
        }
    }

    [Theory]
    [InlineData(1, 400, "invalid_request_error")]
    [InlineData(2, 401, "authentication_error")]
    [InlineData(3, 429, "rate_limit_error")]
    [InlineData(4, 529, "overloaded_error")]
    [InlineData(5, 500, "api_error")]
    public async Task AnErrorStatus_IsAProviderHttpException_NamingItsType_ButNeverTheBody(int row, int status, string type)
    {
        var handler = new ScriptedProviderHandler((_, _) => ProviderTestKit.Json((HttpStatusCode)status,
            JsonSerializer.Serialize(new { type = "error", error = new { type, message = "it quoted: SECRET-NOTE-TEXT" } })));

        var call = () => ProviderTestKit.Anthropic(handler).CompleteWithToolsAsync(Call(), [new ChatToolMessage { Role = "user", Content = "x" }], null,
            CancellationToken.None);

        var ex = (await call.Should().ThrowAsync<ProviderHttpException>($"row {row}")).Which;
        ex.StatusCode.Should().Be(status);
        ex.Message.Should().Contain($"HTTP {status}").And.Contain(type).And.NotContain("SECRET-NOTE-TEXT");
    }

    [Fact]
    public async Task FetchModels_ReadsEveryPage()
    {
        var handler = new ScriptedProviderHandler((req, _) => req.RequestUri!.Query.Contains("after_id")
            ? ProviderTestKit.Json(HttpStatusCode.OK, "{\"data\":[{\"type\":\"model\",\"id\":\"claude-b\",\"display_name\":\"B\",\"max_input_tokens\":200000}],\"has_more\":false,\"last_id\":\"claude-b\"}")
            : ProviderTestKit.Json(HttpStatusCode.OK, "{\"data\":[{\"type\":\"model\",\"id\":\"claude-a\",\"display_name\":\"A\"},{\"id\":\"bad id\"}],\"has_more\":true,\"last_id\":\"claude-a\"}"));

        var list = await ProviderTestKit.Anthropic(handler).ListModelsAsync(Target(), "sk-ant-test-key-0001", CancellationToken.None);

        list.Select(m => (m.Id, m.Name, m.ContextWindow)).Should().Equal(("claude-a", "A", null), ("claude-b", "B", 200000));
        handler.Seen.Select(s => s.Url).Should().Equal(
            "https://api.anthropic.com/v1/models?limit=1000", "https://api.anthropic.com/v1/models?limit=1000&after_id=claude-a");
        handler.Headers.Should().OnlyContain(h => h["x-api-key"] == "sk-ant-test-key-0001" && h["anthropic-version"] == "2026-10-21");
    }

    [Fact]
    public async Task ImageGeneration_IsNotOffered_AndAnotherProtocolsProvider_IsRefusedBeforeARequest()
    {
        var handler = new ScriptedProviderHandler((_, _) => Message([Text("x")]));
        var client = ProviderTestKit.Anthropic(handler);

        client.SupportsImageGeneration(ProviderWireOptions.AnthropicNative with { ImageGeneration = true }).Should().BeFalse();
        await ((Func<Task>)(() => client.GenerateImageAsync(Call(), [new ChatToolMessage { Role = "user", Content = "a cat" }], CancellationToken.None)))
            .Should().ThrowAsync<ProviderUnavailableException>();
        var openAiTarget = ProviderEgressTarget.ForTest("https://api.anthropic.com/v1");
        await ((Func<Task>)(() => client.CompleteWithToolsAsync(new ModelCall(openAiTarget, "k", Model), [new ChatToolMessage { Role = "user", Content = "x" }],
            null, CancellationToken.None))).Should().ThrowAsync<ProviderUnavailableException>();
        handler.Seen.Should().BeEmpty();
    }
}
