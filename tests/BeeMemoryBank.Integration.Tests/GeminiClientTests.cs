using System.Net;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Providers;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Gemini client (<see cref="GeminiClient"/>) against a scripted provider: what it sends (systemInstruction, user/model turns, tools with
/// schemas Gemini accepts, function calls and responses matched by name, images, the key only in <c>x-goog-api-key</c> and never in the
/// address, Dream's generation config), what it reads (text, several calls with made-up ids, usage with thoughts, cut-off, safety blocks),
/// the stream, thought signatures sent back only to the same model, error statuses and Fetch models with paging.
/// </summary>
public class GeminiClientTests
{
    private static readonly Guid ProviderId = Guid.NewGuid();
    private const string Model = "gemini-3-pro";
    private const string Base = "https://generativelanguage.googleapis.com/v1beta";
    private const string Key = "AIza-test-key-0001";

    private static ProviderEgressTarget Target(ProviderWireOptions? options = null, Guid? providerId = null) =>
        new(providerId ?? ProviderId, "Google Gemini", ProviderProtocols.Gemini, new Uri(Base), false, options ?? ProviderWireOptions.GeminiNative);

    private static ModelCall Call(string model = Model, ProviderWireOptions? options = null, Guid? providerId = null) =>
        new(Target(options, providerId), Key, model);

    private static string Response(object[] parts, string? finish = "STOP", object? usage = null, object? feedback = null) =>
        JsonSerializer.Serialize(new
        {
            candidates = parts.Length == 0 && finish is null ? null : new[] { new { content = new { role = "model", parts }, finishReason = finish, index = 0 } },
            usageMetadata = usage ?? new { promptTokenCount = 900, candidatesTokenCount = 40, thoughtsTokenCount = 60, totalTokenCount = 1000 },
            promptFeedback = feedback,
            modelVersion = Model,
        });

    private static HttpResponseMessage Ok(object[] parts, string? finish = "STOP", object? usage = null) =>
        ProviderTestKit.Json(HttpStatusCode.OK, Response(parts, finish, usage));

    private static JsonElement Body(ScriptedProviderHandler handler, int index = 0) => JsonDocument.Parse(handler.Seen[index].Body).RootElement.Clone();

    private static ChatToolMessage Assistant(string? content, params (string Id, string Name, string Args)[] calls) => new()
    {
        Role = "assistant", Content = content,
        ToolCalls = calls.Select(c => new ChatToolCall { Id = c.Id, Function = new ChatToolCallFunction { Name = c.Name, Arguments = c.Args } }).ToList(),
    };

    [Fact]
    public async Task TheRequest_HasTheSystemInstruction_UserAndModelTurns_CallsAndResponsesByName_AndImages_AndTheKeyOnlyInItsHeader()
    {
        var handler = new ScriptedProviderHandler((_, _) => Ok([new { text = "ok" }]));
        List<ChatToolMessage> messages =
        [
            new() { Role = "system", Content = "You are Bee." },
            new() { Role = "user", Content = "hello" },
            Assistant("Looking.", (GeminiClient.SyntheticIdPrefix + "aa", "bee_search", "{\"keywords\":\"a\"}"), ("given-id-2", "bee_get_tree", "{}")),
            new() { Role = "tool", ToolCallId = GeminiClient.SyntheticIdPrefix + "aa", Content = "{\"articles\":[]}" },
            new() { Role = "tool", ToolCallId = "given-id-2", Content = "[1,2]" },
            new() { Role = "system", Content = "[Images attached to the previous message]" },
            new() { Role = "user", Content = "and this?", ImageDataUrls = ["data:image/jpeg;base64,/9j/4AAQ"] },
        ];

        var turn = await ProviderTestKit.Gemini(handler).CompleteWithToolsAsync(Call(), messages, ChatToolDispatcher.ToolDefinitions, CancellationToken.None);

        turn.Content.Should().Be("ok");
        var seen = handler.Seen.Single();
        seen.Url.Should().Be($"{Base}/models/{Model}:generateContent");
        seen.Url.Should().NotContain("key=").And.NotContain(Key, "the key never goes into the address");
        handler.Headers.Single()["x-goog-api-key"].Should().Be(Key);
        handler.Headers.Single().Should().NotContainKey("authorization");

        var body = Body(handler);
        body.TryGetProperty("generationConfig", out _).Should().BeFalse("the chat sends no generation config");
        body.GetProperty("systemInstruction").GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("text").GetString())
            .Should().Equal("You are Bee.");
        var contents = body.GetProperty("contents");
        contents.EnumerateArray().Select(c => c.GetProperty("role").GetString()).Should().Equal("user", "model", "user");
        var model = contents[1].GetProperty("parts");
        model[0].GetProperty("text").GetString().Should().Be("Looking.");
        var first = model[1].GetProperty("functionCall");
        first.GetProperty("name").GetString().Should().Be("bee_search");
        first.GetProperty("args").GetProperty("keywords").GetString().Should().Be("a");
        first.TryGetProperty("id", out _).Should().BeFalse("an id this client made up is never sent");
        model[1].TryGetProperty("thoughtSignature", out _).Should().BeFalse("a round before the last user message is not checked");
        model[2].GetProperty("functionCall").GetProperty("id").GetString().Should().Be("given-id-2");
        model[2].TryGetProperty("thoughtSignature", out _).Should().BeFalse();

        var user = contents[2].GetProperty("parts");
        var r1 = user[0].GetProperty("functionResponse");
        r1.GetProperty("name").GetString().Should().Be("bee_search", "a result is matched to its call's name");
        r1.TryGetProperty("id", out _).Should().BeFalse();
        r1.GetProperty("response").GetProperty("articles").ValueKind.Should().Be(JsonValueKind.Array);
        var r2 = user[1].GetProperty("functionResponse");
        r2.GetProperty("name").GetString().Should().Be("bee_get_tree");
        r2.GetProperty("id").GetString().Should().Be("given-id-2");
        r2.GetProperty("response").GetProperty("output").ValueKind.Should().Be(JsonValueKind.Array, "a result that is not an object goes under 'output'");
        user[2].GetProperty("text").GetString().Should().Be("[Images attached to the previous message]");
        user[3].GetProperty("text").GetString().Should().Be("and this?");
        user[4].GetProperty("inlineData").GetProperty("mimeType").GetString().Should().Be("image/jpeg");
        user[4].GetProperty("inlineData").GetProperty("data").GetString().Should().Be("/9j/4AAQ");

        var declarations = body.GetProperty("tools")[0].GetProperty("functionDeclarations");
        declarations.GetArrayLength().Should().Be(ChatToolDispatcher.ToolDefinitions.Count);
    }

    [Fact]
    public void TheChatsRealToolSchemas_AreCutDownToWhatGeminiAccepts()
    {
        string[] allowed =
        [
            "type", "format", "title", "description", "nullable", "enum", "maxItems", "minItems", "properties", "required", "minProperties",
            "maxProperties", "minLength", "maxLength", "pattern", "example", "anyOf", "propertyOrdering", "items", "minimum", "maximum",
        ];
        foreach (var tool in ChatToolDispatcher.ToolDefinitions)
        {
            var converted = GeminiSchema.Convert(tool.Function.Parameters);
            converted.Should().NotBeNull(tool.Function.Name);
            var json = JsonDocument.Parse(converted!.ToJsonString()).RootElement;
            Walk(json, tool.Function.Name);
            var properties = json.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
            if (json.TryGetProperty("required", out var required))
                required.EnumerateArray().Select(r => r.GetString()).Should().BeSubsetOf(properties, tool.Function.Name);
        }
        var save = ChatToolDispatcher.ToolDefinitions.Single(t => t.Function.Name == "bee_update_article");
        var id = JsonDocument.Parse(GeminiSchema.Convert(save.Function.Parameters)!.ToJsonString()).RootElement.GetProperty("properties").GetProperty("id");
        id.TryGetProperty("format", out _).Should().BeFalse("Gemini knows no 'uuid' format for a string");
        id.GetProperty("description").GetString().Should().Contain("(format: uuid)");

        GeminiSchema.Convert(JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}").RootElement)
            .Should().BeNull("a tool without parameters declares none");
        var odd = GeminiSchema.Convert(JsonDocument.Parse(
            "{\"$schema\":\"x\",\"type\":\"object\",\"properties\":{\"a\":{\"type\":[\"string\",\"null\"],\"default\":\"d\"},\"b\":{\"const\":\"k\"},\"c\":{\"oneOf\":[{\"type\":\"integer\",\"format\":\"int64\"},{\"type\":\"string\"}]}},\"required\":[\"a\",\"zz\"]}").RootElement)!;
        var oddJson = JsonDocument.Parse(odd.ToJsonString()).RootElement;
        oddJson.TryGetProperty("$schema", out _).Should().BeFalse();
        oddJson.GetProperty("properties").GetProperty("a").GetProperty("type").GetString().Should().Be("string");
        oddJson.GetProperty("properties").GetProperty("a").GetProperty("nullable").GetBoolean().Should().BeTrue();
        oddJson.GetProperty("properties").GetProperty("a").TryGetProperty("default", out _).Should().BeFalse();
        oddJson.GetProperty("properties").GetProperty("b").GetProperty("enum")[0].GetString().Should().Be("k");
        oddJson.GetProperty("properties").GetProperty("c").GetProperty("anyOf")[0].GetProperty("format").GetString().Should().Be("int64");
        oddJson.GetProperty("required").EnumerateArray().Select(r => r.GetString()).Should().Equal("a");

        void Walk(JsonElement node, string tool)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            foreach (var p in node.EnumerateObject())
            {
                p.Name.Should().BeOneOf(allowed, tool);
                if (p.Name == "properties") foreach (var child in p.Value.EnumerateObject()) Walk(child.Value, tool);
                if (p.Name == "items") Walk(p.Value, tool);
                if (p.Name == "anyOf") foreach (var child in p.Value.EnumerateArray()) Walk(child, tool);
            }
        }
    }

    [Theory]
    [InlineData(1, "gemini-3-pro", true)]
    [InlineData(2, "models/gemini-3.5-flash", true)]
    [InlineData(3, "gemini-2.5-flash", false)]
    [InlineData(4, "gemma-3-27b-it", false)]
    public async Task DreamsCall_SendsTheLimitTheTemperature_AndALowThinkingLevel_OnlyToModelsThatHaveLevels(int row, string model, bool level)
    {
        var handler = new ScriptedProviderHandler((_, _) => Ok([new { text = "{}" }]));

        var answer = await ProviderTestKit.Gemini(handler).CompleteAsync(Call(model), [new ChatRoleMessage("system", "rules"), new ChatRoleMessage("user", "notes")],
            new CompletionOptions(700, 0.0, "low"), CancellationToken.None);

        answer.PromptTokens.Should().Be(900, $"row {row}");
        answer.CompletionTokens.Should().Be(100, "candidates + thoughts");
        answer.HasReasoning.Should().BeTrue();
        handler.Seen.Single().Url.Should().EndWith($"/models/{model.Replace("models/", "")}:generateContent");
        var config = Body(handler).GetProperty("generationConfig");
        config.GetProperty("maxOutputTokens").GetInt32().Should().Be(700);
        config.GetProperty("temperature").GetDouble().Should().Be(0.0);
        config.TryGetProperty("thinkingConfig", out var thinking).Should().Be(level, $"row {row}");
        if (level) thinking.GetProperty("thinkingLevel").GetString().Should().Be("low");
    }

    [Fact]
    public async Task TheAnswer_GivesItsText_EveryCallWithAnId_TheUsage_AndKeepsTheSignedParts()
    {
        var handler = new ScriptedProviderHandler((_, _) => Ok(
        [
            new { text = "Searching." } as object,
            new { functionCall = new { name = "bee_search", args = new { keywords = "x" } }, thoughtSignature = "sig-1" },
            new { functionCall = new { name = "bee_get_tree", args = new { } } },
            new { functionCall = new { name = "bee_get_tree", args = new { path = "/A" }, id = "g-3" } },
        ]));

        var turn = await ProviderTestKit.Gemini(handler).CompleteWithToolsAsync(Call(), [new ChatToolMessage { Role = "user", Content = "x" }],
            ChatToolDispatcher.ToolDefinitions, CancellationToken.None);

        turn.Content.Should().Be("Searching.");
        turn.ToolCalls!.Select(c => c.Name).Should().Equal("bee_search", "bee_get_tree", "bee_get_tree");
        turn.ToolCalls![0].Id.Should().StartWith(GeminiClient.SyntheticIdPrefix);
        turn.ToolCalls[1].Id.Should().StartWith(GeminiClient.SyntheticIdPrefix).And.NotBe(turn.ToolCalls[0].Id);
        turn.ToolCalls[2].Id.Should().Be("g-3");
        JsonDocument.Parse(turn.ToolCalls[0].ArgumentsJson).RootElement.GetProperty("keywords").GetString().Should().Be("x");
        turn.PromptTokens.Should().Be(900);
        turn.CompletionTokens.Should().Be(100);
        turn.ProviderState!.IsFor(ProviderProtocols.Gemini, ProviderId, Model).Should().BeTrue();
        turn.ProviderState.Content[1].GetProperty("thoughtSignature").GetString().Should().Be("sig-1");
    }

    [Fact]
    public async Task ACutOffIsLength_ASafetyBlockIsAClearRefusal_AndABlockedPromptToo()
    {
        var answers = new Queue<HttpResponseMessage>(
        [
            Ok([new { text = "half" }], finish: "MAX_TOKENS"),
            Ok([new { text = "" }], finish: "SAFETY"),
            Ok([new { text = "" }], finish: "PROHIBITED_CONTENT"),
            ProviderTestKit.Json(HttpStatusCode.OK, Response([], finish: null, feedback: new { blockReason = "SAFETY" })),
            Ok([new { text = "" }], finish: "MALFORMED_FUNCTION_CALL"),
        ]);
        var client = ProviderTestKit.Gemini(new ScriptedProviderHandler((_, _) => answers.Dequeue()));
        List<ChatToolMessage> chat = [new() { Role = "user", Content = "x" }];

        (await client.CompleteAsync(Call(), [new ChatRoleMessage("user", "x")], new CompletionOptions(10), CancellationToken.None))
            .FinishReason.Should().Be("length");
        (await client.CompleteAsync(Call(), [new ChatRoleMessage("user", "x")], new CompletionOptions(10), CancellationToken.None))
            .FinishReason.Should().Be("content_filter");
        (await ((Func<Task>)(() => client.CompleteWithToolsAsync(Call(), chat, null, CancellationToken.None))).Should()
            .ThrowAsync<ProviderRefusalException>()).WithMessage("*blocked the answer (PROHIBITED_CONTENT)*");
        (await ((Func<Task>)(() => client.CompleteWithToolsAsync(Call(), chat, null, CancellationToken.None))).Should()
            .ThrowAsync<ProviderRefusalException>()).WithMessage("*blocked this request (SAFETY)*");
        (await ((Func<Task>)(() => client.CompleteWithToolsAsync(Call(), chat, null, CancellationToken.None))).Should()
            .ThrowAsync<InvalidOperationException>()).WithMessage("*MALFORMED_FUNCTION_CALL*");
    }

    private static HttpResponseMessage SignedStream() => ProviderTestKit.EventStream(
        (null, Response([new { text = "Hel" }], finish: null, usage: new { promptTokenCount = 800 })),
        (null, Response([new { text = "lo" }], finish: null, usage: new { promptTokenCount = 800 })),
        (null, Response([new { functionCall = new { name = "bee_search", args = new { keywords = "bees" } }, thoughtSignature = "sig-stream" }], finish: null,
            usage: new { promptTokenCount = 800 })),
        (null, Response([new { text = "" , thoughtSignature = "sig-tail" }], finish: "STOP",
            usage: new { promptTokenCount = 800, candidatesTokenCount = 30, thoughtsTokenCount = 12 })));

    [Fact]
    public async Task AStream_ForwardsTheText_TakesCallsWhole_CountsTheUsageAtTheEnd_AndKeepsTheSignatures()
    {
        var handler = new ScriptedProviderHandler((_, _) => SignedStream());
        var deltas = new List<string>();

        var turn = await ProviderTestKit.Gemini(handler).StreamWithToolsAsync(Call(), [new ChatToolMessage { Role = "user", Content = "bees?" }],
            ChatToolDispatcher.ToolDefinitions, (d, _) => { deltas.Add(d); return ValueTask.CompletedTask; }, CancellationToken.None);

        handler.Seen.Single().Url.Should().Be($"{Base}/models/{Model}:streamGenerateContent?alt=sse");
        deltas.Should().Equal("Hel", "lo");
        turn.Content.Should().Be("Hello");
        turn.ToolCalls!.Single().Name.Should().Be("bee_search");
        turn.PromptTokens.Should().Be(800);
        turn.CompletionTokens.Should().Be(42);
        var parts = turn.ProviderState!.Content;
        parts.GetArrayLength().Should().Be(3);
        parts[0].GetProperty("text").GetString().Should().Be("Hello", "fragments of one part are one part");
        parts[1].GetProperty("thoughtSignature").GetString().Should().Be("sig-stream");
        parts[2].GetProperty("thoughtSignature").GetString().Should().Be("sig-tail");
    }

    [Fact]
    public async Task AnErrorInsideTheStream_AfterTheFirstDelta_IsAnErrorWithItsStatus_AndAStreamWithoutAnEnd_BrokeOff()
    {
        var failing = new ScriptedProviderHandler((_, _) => ProviderTestKit.EventStream(
            (null, Response([new { text = "Hi" }], finish: null)),
            (null, "{\"error\":{\"code\":503,\"message\":\"The model is overloaded.\",\"status\":\"UNAVAILABLE\"}}")));
        var deltas = new List<string>();
        var stream = () => ProviderTestKit.Gemini(failing).StreamWithToolsAsync(Call(), [new ChatToolMessage { Role = "user", Content = "x" }], null,
            (d, _) => { deltas.Add(d); return ValueTask.CompletedTask; }, CancellationToken.None);

        var ex = (await stream.Should().ThrowAsync<ProviderHttpException>()).Which;
        ex.StatusCode.Should().Be(503);
        ex.Message.Should().Contain("UNAVAILABLE").And.NotContain("overloaded.");
        deltas.Should().Equal("Hi");

        var cut = new ScriptedProviderHandler((_, _) => ProviderTestKit.EventStream((null, Response([new { text = "Hi" }], finish: null))));
        var broken = () => ProviderTestKit.Gemini(cut).StreamWithToolsAsync(Call(), [new ChatToolMessage { Role = "user", Content = "x" }], null,
            (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        await broken.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task TheNextRound_SendsTheSignedPartsBackVerbatim_ButOnlyToTheSameProviderAndModel()
    {
        var turn = await ProviderTestKit.Gemini(new ScriptedProviderHandler((_, _) => SignedStream())).StreamWithToolsAsync(Call(),
            [new ChatToolMessage { Role = "user", Content = "bees?" }], ChatToolDispatcher.ToolDefinitions, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        var call = turn.ToolCalls!.Single();
        var assistant = Assistant(turn.Content, (call.Id, call.Name, call.ArgumentsJson));
        assistant.ProviderState = turn.ProviderState;
        List<ChatToolMessage> loop =
        [
            new() { Role = "user", Content = "bees?" },
            assistant,
            new() { Role = "tool", ToolCallId = call.Id, Content = "{\"articles\":[]}" },
        ];

        var next = new ScriptedProviderHandler((_, _) => Ok([new { text = "none" }]));
        var client = ProviderTestKit.Gemini(next);
        await client.CompleteWithToolsAsync(Call(), loop, ChatToolDispatcher.ToolDefinitions, CancellationToken.None);
        await client.CompleteWithToolsAsync(Call("gemini-3-flash"), loop, ChatToolDispatcher.ToolDefinitions, CancellationToken.None);
        await client.CompleteWithToolsAsync(Call("gemini-2.5-pro"), loop, ChatToolDispatcher.ToolDefinitions, CancellationToken.None);

        var same = Body(next, 0).GetProperty("contents")[1].GetProperty("parts");
        same.GetArrayLength().Should().Be(3);
        same[1].GetProperty("thoughtSignature").GetString().Should().Be("sig-stream");
        same[2].GetProperty("thoughtSignature").GetString().Should().Be("sig-tail");
        var response = Body(next, 0).GetProperty("contents")[2].GetProperty("parts")[0].GetProperty("functionResponse");
        response.GetProperty("name").GetString().Should().Be("bee_search");

        next.Seen[1].Body.Should().NotContain("sig-stream").And.NotContain("sig-tail");
        Body(next, 1).GetProperty("contents")[1].GetProperty("parts")[1].GetProperty("thoughtSignature").GetString().Should().Be(GeminiClient.SkipSignature);
        next.Seen[2].Body.Should().NotContain("sig-stream").And.NotContain(GeminiClient.SkipSignature, "Gemini 2.5 does not check signatures");
    }

    [Theory]
    [InlineData(1, 429, "RESOURCE_EXHAUSTED", null, 429)]
    [InlineData(2, 400, "INVALID_ARGUMENT", "API_KEY_INVALID", 401)]
    [InlineData(3, 400, "INVALID_ARGUMENT", null, 400)]
    [InlineData(4, 503, "UNAVAILABLE", null, 503)]
    [InlineData(5, 403, "PERMISSION_DENIED", null, 403)]
    public async Task AnErrorStatus_IsAProviderHttpException_NamingItsStatusWord_ButNeverTheBody(int row, int http, string word, string? reason, int expected)
    {
        var details = reason is null ? Array.Empty<object>() : [new Dictionary<string, object> { ["@type"] = "type.googleapis.com/google.rpc.ErrorInfo", ["reason"] = reason }];
        var handler = new ScriptedProviderHandler((_, _) => ProviderTestKit.Json((HttpStatusCode)http,
            JsonSerializer.Serialize(new { error = new { code = http, message = "it quoted: SECRET-NOTE-TEXT", status = word, details } })));

        var call = () => ProviderTestKit.Gemini(handler).CompleteWithToolsAsync(Call(), [new ChatToolMessage { Role = "user", Content = "x" }], null,
            CancellationToken.None);

        var ex = (await call.Should().ThrowAsync<ProviderHttpException>($"row {row}")).Which;
        ex.StatusCode.Should().Be(expected);
        ex.Message.Should().Contain(word).And.NotContain("SECRET-NOTE-TEXT");
    }

    [Fact]
    public async Task FetchModels_ReadsEveryPage_AndOnlyTheModelsThatGenerateContent()
    {
        var handler = new ScriptedProviderHandler((req, _) => req.RequestUri!.Query.Contains("pageToken")
            ? ProviderTestKit.Json(HttpStatusCode.OK, "{\"models\":[{\"name\":\"models/gemini-3-flash\",\"displayName\":\"Flash\",\"inputTokenLimit\":1048576,\"supportedGenerationMethods\":[\"generateContent\",\"countTokens\"]}]}")
            : ProviderTestKit.Json(HttpStatusCode.OK, "{\"models\":[{\"name\":\"models/gemini-3-pro\",\"displayName\":\"Pro\",\"supportedGenerationMethods\":[\"generateContent\"]},{\"name\":\"models/text-embedding-005\",\"supportedGenerationMethods\":[\"embedContent\"]}],\"nextPageToken\":\"tok/2+\"}"));

        var list = await ProviderTestKit.Gemini(handler).ListModelsAsync(Target(), Key, CancellationToken.None);

        list.Select(m => (m.Id, m.Name, m.ContextWindow)).Should().Equal(("gemini-3-pro", "Pro", null), ("gemini-3-flash", "Flash", 1048576));
        handler.Seen.Select(s => s.Url).Should().Equal($"{Base}/models?pageSize=1000", $"{Base}/models?pageSize=1000&pageToken=tok%2F2%2B");
        handler.Seen.Should().OnlyContain(s => !s.Url.Contains(Key));
        handler.Headers.Should().OnlyContain(h => h["x-goog-api-key"] == Key);
    }

    [Theory]
    [InlineData(1, "../../evil")]
    [InlineData(2, "gemini-3-pro:streamGenerateContent?key=x")]
    [InlineData(3, "tunedModels/mine")]
    public async Task AModelIdThatIsNotAModelName_IsRefusedBeforeARequest(int row, string model)
    {
        var handler = new ScriptedProviderHandler((_, _) => Ok([new { text = "x" }]));
        var call = () => ProviderTestKit.Gemini(handler).CompleteWithToolsAsync(Call(model), [new ChatToolMessage { Role = "user", Content = "x" }], null,
            CancellationToken.None);
        await call.Should().ThrowAsync<ProviderUnavailableException>($"row {row}");
        handler.Seen.Should().BeEmpty();
    }
}
