using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BeeMemoryBank.Api.Models;

namespace BeeMemoryBank.Api.Services.Providers;

/// <summary>
/// The client of Anthropic's own Messages format (<see cref="ProviderProtocols.AnthropicMessages"/>): <c>POST {base}/messages</c> and
/// <c>GET {base}/models</c>, base <c>https://api.anthropic.com/v1</c>, the key in <c>x-api-key</c>, <c>anthropic-version</c>
/// <see cref="ApiVersion"/>. Written by hand like <see cref="OpenAiChatClient"/>: every request is built here and leaves through
/// <see cref="ProviderEgress"/> (the approved host only, no redirect, the address checked at connect), so what is sent can be read in one place.
///
/// <list type="bullet">
/// <item><b>Mapping.</b> The leading system messages become the top-level <c>system</c> blocks; a system message later in the conversation
/// (the attachment manifest, a vision model's description) stays where it is, as text of the user turn it belongs to. Turns of one role in a
/// row are merged. OpenAI-style tool calls become <c>tool_use</c> blocks, tool results <c>tool_result</c> blocks of one user turn; a call
/// that has no result (the loop paused on a write before it) gets an "was not run" result, because the API refuses a <c>tool_use</c> left
/// unanswered. Images from data URLs become base64 image blocks.</item>
/// <item><b>No sampling fields.</b> With <see cref="ApiVersion"/> a request with <c>temperature</c>, <c>top_p</c> or <c>top_k</c> is refused on
/// every model, so none is ever sent (Dream's temperature 0 is dropped here). <c>max_tokens</c> is required: the caller's, else
/// <see cref="ProviderWire.DefaultMaxOutputTokens"/>.</item>
/// <item><b>Thinking.</b> Current models think adaptively; nothing is sent for the chat (the model's default). Dream's effort goes to
/// <c>output_config.effort</c> when the provider's reasoning style is <see cref="ReasoningStyles.Anthropic"/>; no <c>thinking</c> field is
/// sent (<c>budget_tokens</c> and <c>{"type": "disabled"}</c> are refused by current models). An answer that thought carries its
/// <c>thinking</c> / <c>redacted_thinking</c> blocks with their signatures: they come back as <see cref="ToolCompletionResult.ProviderState"/>
/// (the turn's whole content, as the API wrote it) and are sent back unmodified on the next round of the tool loop - only to the same
/// provider and model; another model gets the turn rebuilt from its text and tool calls. Thinking text is never shown.</item>
/// <item><b>Prompt caching</b> (<see cref="ProviderWireOptions.PromptCaching"/>): <c>cache_control: {type: ephemeral}</c> on the last system
/// block and on the last tool, so the chat's system prompt and tool list are read from the cache on every round. The usage the chat sees
/// is <c>input_tokens + cache_creation_input_tokens + cache_read_input_tokens</c> as the prompt, <c>output_tokens</c> as the completion.</item>
/// <item><b>Stops.</b> <c>max_tokens</c> is the chat's "length"; <c>refusal</c> is a <see cref="ProviderRefusalException"/> for the chat (a
/// clear message, no retry) and the finish reason "refusal" for a plain completion.</item>
/// <item><b>Errors.</b> An HTTP status is a <see cref="ProviderHttpException"/> with it (the router: 401 disables the key, 429 pauses it, 529
/// "overloaded" and 5xx try the next key, a 400 is a request error for every provider: the chain's next model); an <c>error</c> event inside
/// the stream is one too, with the status of its type. The exception and the log name the provider, the status and the error's type word (the log also the request
/// id), never the body.</item>
/// </list>
/// No image generation through this protocol.
/// </summary>
public sealed partial class AnthropicMessagesClient : IModelProtocolClient
{
    /// <summary>The API version this client speaks (the <c>anthropic-version</c> header).</summary>
    public const string ApiVersion = "2026-10-21";

    private static readonly JsonSerializerOptions WriteOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ProviderEgress _egress;
    private readonly ILogger<AnthropicMessagesClient> _logger;

    public AnthropicMessagesClient(ProviderEgress egress, ILogger<AnthropicMessagesClient> logger)
    {
        _egress = egress;
        _logger = logger;
    }

    public string Protocol => ProviderProtocols.AnthropicMessages;

    public bool SupportsImageGeneration(ProviderWireOptions options) => false;

    private void Validate(ModelCall call, int messageCount)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (string.IsNullOrWhiteSpace(call.Model))
            throw new ArgumentException("Model is required.", nameof(call));
        if (messageCount == 0)
            throw new ArgumentException("At least one message is required.", nameof(call));
        if (call.Target.Protocol != Protocol)
            throw new ProviderUnavailableException($"Provider '{call.Target.Name}' does not speak the {Protocol} protocol.");
    }

    private static void Authorize(HttpRequestMessage req, string apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
    }

    private static HttpRequestMessage Post(ModelCall call, JsonObject payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, call.Target.Endpoint("messages"));
        Authorize(req, call.ApiKey);
        req.Content = new StringContent(payload.ToJsonString(WriteOpts), Encoding.UTF8, "application/json");
        return req;
    }

    /// <summary>A failed request: logged with the status, the error's type word and the request id only - never the body, which can quote
    /// what was sent.</summary>
    private ProviderHttpException Failure(ModelCall call, HttpResponseMessage resp, string? body, string what)
    {
        var status = (int)resp.StatusCode;
        var code = ProviderWire.ErrorCode(body, "type");
        _logger.LogWarning("{Provider} {What} failed: HTTP {Status} {Code} request-id={RequestId}", call.Target.Name, what, status, code,
            ProviderWire.RequestId(resp));
        return new ProviderHttpException(status, code is null
            ? $"{call.Target.Name} completion failed (HTTP {status})."
            : $"{call.Target.Name} completion failed (HTTP {status}, {code}).");
    }

    /// <summary>The HTTP status an <c>error</c> event of the stream stands for, by its type.</summary>
    internal static int StatusOfErrorType(string? type) => type switch
    {
        "invalid_request_error" => 400,
        "authentication_error" => 401,
        "billing_error" => 402,
        "permission_error" => 403,
        "not_found_error" => 404,
        "request_too_large" => 413,
        "rate_limit_error" => 429,
        "timeout_error" => 504,
        "overloaded_error" => 529,
        _ => 500,
    };

    // ── requests ─────────────────────────────────────────────────────────────────

    /// <summary>A plain completion: Dream's call (an output limit, the effort the provider's way, no temperature - see the class remarks) or
    /// the chat's (the default output limit and nothing else).</summary>
    public async Task<ChatCompleteResponse> CompleteAsync(ModelCall call, IReadOnlyList<ChatRoleMessage> messages, CompletionOptions options,
        CancellationToken ct)
    {
        Validate(call, messages.Count);
        var payload = BuildRequest(call, messages.Select(m => new ChatToolMessage { Role = m.Role, Content = m.Content }).ToList(), null,
            options.MaxTokens, options.ReasoningEffort, stream: false);

        using var req = Post(call, payload);
        using var resp = await _egress.SendAsync(call.Target, req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw Failure(call, resp, body, "completion");

        var turn = ParseMessage(call, body);
        return new ChatCompleteResponse(turn.Text, turn.PromptTokens ?? 0, turn.CompletionTokens ?? 0, turn.Model)
        {
            FinishReason = FinishReason(turn.StopReason),
            HasReasoning = turn.State is not null,
        };
    }

    public async Task<ToolCompletionResult> CompleteWithToolsAsync(ModelCall call, IReadOnlyList<ChatToolMessage> messages,
        IReadOnlyList<ChatToolDefinition>? tools, CancellationToken ct)
    {
        Validate(call, messages.Count);
        var payload = BuildRequest(call, messages, tools, null, null, stream: false);

        using var req = Post(call, payload);
        using var resp = await _egress.SendAsync(call.Target, req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw Failure(call, resp, body, "tool completion");

        var turn = ParseMessage(call, body);
        if (turn.StopReason == "refusal") throw Refusal(call);
        return turn.ToResult();
    }

    /// <summary>The streamed completion of a chat round: text deltas to <paramref name="onTextDelta"/> as they arrive, tool input assembled
    /// per content block from its <c>input_json_delta</c> fragments, thinking kept (never forwarded) for the next round. A stream that ends
    /// before <c>message_stop</c> broke off (an <see cref="IOException"/>, like a dropped connection).</summary>
    public async Task<ToolCompletionResult> StreamWithToolsAsync(ModelCall call, IReadOnlyList<ChatToolMessage> messages,
        IReadOnlyList<ChatToolDefinition>? tools, Func<string, CancellationToken, ValueTask> onTextDelta, CancellationToken ct)
    {
        Validate(call, messages.Count);
        var payload = BuildRequest(call, messages, tools, null, null, stream: true);

        using var req = Post(call, payload);
        using var resp = await _egress.SendAsync(call.Target, req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw Failure(call, resp, body, "streaming completion");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var blocks = new SortedDictionary<int, StreamBlock>();
        var text = new StringBuilder();
        string? model = null, stopReason = null;
        int? input = null, cacheCreation = null, cacheRead = null, output = null;
        var stopped = false;

        await foreach (var data in ProviderWire.ReadEventDataAsync(stream, ct))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var ev = doc.RootElement;
                switch (ProviderWire.Str(ev, "type"))
                {
                    case "message_start":
                        if (ev.TryGetProperty("message", out var message))
                        {
                            model = ProviderWire.Str(message, "model") ?? model;
                            if (message.TryGetProperty("usage", out var u)) ReadUsage(u, ref input, ref cacheCreation, ref cacheRead, ref output);
                        }
                        break;
                    case "content_block_start":
                        if (ProviderWire.Int(ev, "index") is { } startIndex && ev.TryGetProperty("content_block", out var cb)
                            && JsonNode.Parse(cb.GetRawText()) is JsonObject start)
                            blocks[startIndex] = new StreamBlock(start);
                        break;
                    case "content_block_delta":
                        if (ProviderWire.Int(ev, "index") is not { } index || !ev.TryGetProperty("delta", out var delta)) break;
                        if (!blocks.TryGetValue(index, out var block))
                            blocks[index] = block = new StreamBlock(new JsonObject { ["type"] = "text", ["text"] = "" });
                        switch (ProviderWire.Str(delta, "type"))
                        {
                            case "text_delta" when ProviderWire.Str(delta, "text") is { Length: > 0 } fragment:
                                block.Text.Append(fragment);
                                text.Append(fragment);
                                await onTextDelta(fragment, ct);
                                break;
                            case "input_json_delta" when ProviderWire.Str(delta, "partial_json") is { } json:
                                block.Json.Append(json);
                                break;
                            case "thinking_delta" when ProviderWire.Str(delta, "thinking") is { } thought:
                                block.Text.Append(thought);
                                break;
                            case "signature_delta" when ProviderWire.Str(delta, "signature") is { } signature:
                                block.Signature.Append(signature);
                                break;
                        }
                        break;
                    case "message_delta":
                        if (ev.TryGetProperty("delta", out var md))
                            stopReason = ProviderWire.Str(md, "stop_reason") ?? stopReason;
                        if (ev.TryGetProperty("usage", out var mu)) ReadUsage(mu, ref input, ref cacheCreation, ref cacheRead, ref output);
                        break;
                    case "message_stop":
                        stopped = true;
                        break;
                    case "error":
                    {
                        var type = ev.TryGetProperty("error", out var err) ? ProviderWire.Str(err, "type") : null;
                        var safe = type is not null && TypeWord().IsMatch(type) ? type : "error";
                        _logger.LogWarning("{Provider} streaming completion: error event {Type}", call.Target.Name, safe);
                        throw new ProviderHttpException(StatusOfErrorType(type), $"{call.Target.Name} completion failed mid-stream ({safe}).");
                    }
                }
            }
            if (stopped) break;
        }

        if (!stopped)
            throw new IOException($"The answer of {call.Target.Name} ended before it was complete.");
        if (stopReason == "refusal") throw Refusal(call);

        var content = new JsonArray();
        var toolCalls = new List<ResolvedToolCall>();
        var thinking = false;
        foreach (var (_, b) in blocks)
        {
            var node = b.Finish(out var argumentsJson);
            content.Add(node);
            switch ((string?)node["type"])
            {
                case "tool_use":
                    toolCalls.Add(new ResolvedToolCall((string?)node["id"] ?? "", (string?)node["name"] ?? "", argumentsJson));
                    break;
                case "thinking" or "redacted_thinking":
                    thinking = true;
                    break;
            }
        }
        toolCalls.RemoveAll(c => c.Name.Length == 0);

        return new ToolCompletionResult(
            Content: text.ToString(),
            ToolCalls: toolCalls.Count == 0 ? null : toolCalls,
            Model: model ?? call.Model,
            PromptTokens: Prompt(input, cacheCreation, cacheRead),
            CompletionTokens: output,
            ProviderState: thinking ? new ProviderTurnState(Protocol, call.Target.ProviderId, call.Model, JsonSerializer.SerializeToElement(content)) : null);
    }

    public Task<ImageGenResult> GenerateImageAsync(ModelCall call, IReadOnlyList<ChatToolMessage> messages, CancellationToken ct) =>
        throw new ProviderUnavailableException($"Image generation is not available through provider '{call.Target.Name}'.");

    /// <summary>The provider's own model list, <c>GET {base}/models</c> (<c>data[].id</c>, <c>display_name</c>), page by page through
    /// <c>after_id</c> while <c>has_more</c> (at most ten pages of a thousand).</summary>
    public async Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(ProviderEgressTarget target, string apiKey, CancellationToken ct)
    {
        var list = new List<ProviderModelInfo>();
        string? after = null;
        for (var page = 0; page < 10; page++)
        {
            var relative = "models?limit=1000" + (after is null ? "" : "&after_id=" + Uri.EscapeDataString(after));
            using var req = new HttpRequestMessage(HttpMethod.Get, target.Endpoint(relative));
            Authorize(req, apiKey);
            using var resp = await _egress.SendAsync(target, req, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Provider} model list failed: HTTP {Status} request-id={RequestId}", target.Name, (int)resp.StatusCode,
                    ProviderWire.RequestId(resp));
                throw new ProviderHttpException((int)resp.StatusCode, $"{target.Name} model list failed (HTTP {(int)resp.StatusCode}).");
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) break;
                foreach (var item in data.EnumerateArray())
                {
                    var id = ProviderWire.Str(item, "id");
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsWhiteSpace)) continue;
                    var name = ProviderWire.Str(item, "display_name");
                    list.Add(new ProviderModelInfo(id, name is { Length: <= 200 } ? name : null,
                        ProviderWire.Int(item, "max_input_tokens") is int w && w > 0 ? w : null));
                    if (list.Count >= 1000) return list;
                }
                var more = root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
                after = ProviderWire.Str(root, "last_id");
                if (!more || string.IsNullOrEmpty(after)) break;
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"{target.Name} answered the model list with something that is not JSON.");
            }
        }
        return list;
    }

    // ── request mapping ────────────────────────────────────────────────────────

    /// <summary>The request body (see the class remarks). Internal for the mapping tests.</summary>
    internal JsonObject BuildRequest(ModelCall call, IReadOnlyList<ChatToolMessage> messages, IReadOnlyList<ChatToolDefinition>? tools,
        int? maxTokens, string? effort, bool stream)
    {
        var wire = call.Target.Options;
        var (system, turns) = MapMessages(call, messages);
        if (wire.PromptCaching && system.Count > 0)
            ((JsonObject)system[^1]!)["cache_control"] = Ephemeral();

        var payload = new JsonObject
        {
            ["model"] = call.Model,
            ["max_tokens"] = maxTokens is > 0 ? maxTokens.Value : ProviderWire.DefaultMaxOutputTokens,
        };
        if (system.Count > 0) payload["system"] = system;
        payload["messages"] = turns;
        if (tools is { Count: > 0 })
        {
            var list = new JsonArray();
            foreach (var t in tools)
            {
                var schema = t.Function.Parameters.ValueKind == JsonValueKind.Object
                    ? JsonNode.Parse(t.Function.Parameters.GetRawText())
                    : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                list.Add(new JsonObject { ["name"] = t.Function.Name, ["description"] = t.Function.Description, ["input_schema"] = schema });
            }
            if (wire.PromptCaching) ((JsonObject)list[^1]!)["cache_control"] = Ephemeral();
            payload["tools"] = list;
        }
        if (!string.IsNullOrWhiteSpace(effort) && wire.Reasoning == ReasoningStyles.Anthropic)
            payload["output_config"] = new JsonObject { ["effort"] = effort };
        if (stream) payload["stream"] = true;
        return payload;
    }

    private static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    /// <summary>
    /// The conversation as <c>system</c> blocks and alternating <c>messages</c>. A turn's content is always a block array. A
    /// <c>tool_use</c> left without a result gets one ("not run", an error) before anything else is said, results always come first in their
    /// user turn, and a note (a later system message) or an unmatched tool result waits until the open calls are answered.
    /// </summary>
    private (JsonArray System, JsonArray Messages) MapMessages(ModelCall call, IReadOnlyList<ChatToolMessage> messages)
    {
        var system = new JsonArray();
        var turns = new List<(string Role, JsonArray Content)>();
        var open = new List<string>();
        var waiting = new List<string>();
        var leading = true;

        void Add(string role, JsonNode block)
        {
            if (turns.Count > 0 && turns[^1].Role == role) turns[^1].Content.Add(block);
            else turns.Add((role, new JsonArray { block }));
        }

        void CloseOpen()
        {
            foreach (var id in open)
                Add("user", new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = "This tool call was not run.", ["is_error"] = true });
            open.Clear();
            foreach (var note in waiting) Add("user", Text(note));
            waiting.Clear();
        }

        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "system":
                    if (string.IsNullOrWhiteSpace(m.Content)) break;
                    if (leading) system.Add(Text(m.Content));
                    else if (open.Count > 0) waiting.Add(m.Content);
                    else Add("user", Text(m.Content));
                    break;

                case "tool":
                {
                    leading = false;
                    var id = WireId(m.ToolCallId);
                    if (open.Remove(id))
                        Add("user", new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = m.Content ?? "" });
                    else if (open.Count > 0)
                        waiting.Add("[Result of an earlier tool call]\n" + m.Content);
                    else
                        Add("user", Text("[Result of an earlier tool call]\n" + m.Content));
                    break;
                }

                case "assistant":
                {
                    leading = false;
                    CloseOpen();
                    foreach (var block in AssistantBlocks(call, m))
                    {
                        if ((string?)block["type"] == "tool_use" && (string?)block["id"] is { } useId) open.Add(useId);
                        Add("assistant", block);
                    }
                    break;
                }

                default:
                {
                    leading = false;
                    CloseOpen();
                    foreach (var url in m.ImageDataUrls ?? [])
                    {
                        if (ProviderWire.TryParseDataUrl(url, out var mediaType, out var data))
                            Add("user", new JsonObject
                            {
                                ["type"] = "image",
                                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = data },
                            });
                        else if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
                            Add("user", new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "url", ["url"] = url } });
                    }
                    if (!string.IsNullOrWhiteSpace(m.Content)) Add("user", Text(m.Content));
                    break;
                }
            }
        }
        CloseOpen();

        if (turns.Count == 0)
            throw new ArgumentException("At least one message besides the system prompt is required.", nameof(messages));
        if (turns[0].Role != "user")
            turns.Insert(0, ("user", new JsonArray { Text("(The conversation continues.)") }));

        var array = new JsonArray();
        foreach (var (role, content) in turns)
            array.Add(new JsonObject { ["role"] = role, ["content"] = content });
        return (system, array);
    }

    /// <summary>An assistant turn's blocks: what the model wrote, unmodified, when the turn is this provider's and this model's own (its
    /// thinking and signatures included); otherwise rebuilt from the text and tool calls every protocol shares.</summary>
    private IEnumerable<JsonNode> AssistantBlocks(ModelCall call, ChatToolMessage m)
    {
        if (m.ProviderState is { } state && state.IsFor(Protocol, call.Target.ProviderId, call.Model)
            && JsonNode.Parse(state.Content.GetRawText()) is JsonArray own && own.Count > 0)
        {
            foreach (var block in own)
                if (block is not null) yield return block.DeepClone();
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(m.Content)) yield return Text(m.Content);
        foreach (var tc in m.ToolCalls ?? [])
        {
            if (string.IsNullOrEmpty(tc.Function?.Name)) continue;
            yield return new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = WireId(tc.Id),
                ["name"] = tc.Function.Name,
                ["input"] = ProviderWire.ArgumentsObject(tc.Function.Arguments),
            };
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdShape();

    [GeneratedRegex("^[a-z_]{1,64}$")]
    private static partial Regex TypeWord();

    /// <summary>A tool call id as the API takes it (letters, digits, '_' and '-'): another provider's id that is not (Kimi's
    /// <c>functions.name:0</c>) becomes a stable stand-in, the same for the call and its result.</summary>
    internal static string WireId(string? id) =>
        id is not null && IdShape().IsMatch(id)
            ? id
            : "id_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id ?? "")))[..32];

    // ── response mapping ──────────────────────────────────────────────────────

    private sealed record Turn(string Text, List<ResolvedToolCall> ToolCalls, string Model, int? PromptTokens, int? CompletionTokens,
        string? StopReason, ProviderTurnState? State)
    {
        public ToolCompletionResult ToResult() =>
            new(Text, ToolCalls.Count == 0 ? null : ToolCalls, Model, PromptTokens, CompletionTokens, State);
    }

    private Turn ParseMessage(ModelCall call, string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { throw new InvalidOperationException($"{call.Target.Name} returned a malformed (non-JSON) response body."); }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"{call.Target.Name} returned no message content.");

            var text = new StringBuilder();
            var calls = new List<ResolvedToolCall>();
            var thinking = false;
            foreach (var block in content.EnumerateArray())
            {
                switch (ProviderWire.Str(block, "type"))
                {
                    case "text":
                        text.Append(ProviderWire.Str(block, "text"));
                        break;
                    case "tool_use" when ProviderWire.Str(block, "name") is { Length: > 0 } name:
                        calls.Add(new ResolvedToolCall(ProviderWire.Str(block, "id") ?? "", name,
                            block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}"));
                        break;
                    case "thinking" or "redacted_thinking":
                        thinking = true;
                        break;
                }
            }

            int? input2 = null, creation = null, read = null, output = null;
            if (root.TryGetProperty("usage", out var usage)) ReadUsage(usage, ref input2, ref creation, ref read, ref output);
            return new Turn(text.ToString(), calls, ProviderWire.Str(root, "model") ?? call.Model, Prompt(input2, creation, read), output,
                ProviderWire.Str(root, "stop_reason"),
                thinking ? new ProviderTurnState(Protocol, call.Target.ProviderId, call.Model, content.Clone()) : null);
        }
    }

    private static void ReadUsage(JsonElement usage, ref int? input, ref int? cacheCreation, ref int? cacheRead, ref int? output)
    {
        input = ProviderWire.Int(usage, "input_tokens") ?? input;
        cacheCreation = ProviderWire.Int(usage, "cache_creation_input_tokens") ?? cacheCreation;
        cacheRead = ProviderWire.Int(usage, "cache_read_input_tokens") ?? cacheRead;
        output = ProviderWire.Int(usage, "output_tokens") ?? output;
    }

    /// <summary>What the prompt cost: the uncached input, what was written to the cache and what was read from it.</summary>
    private static int? Prompt(int? input, int? cacheCreation, int? cacheRead) =>
        input is null && cacheCreation is null && cacheRead is null ? null : (input ?? 0) + (cacheCreation ?? 0) + (cacheRead ?? 0);

    /// <summary>The chat's finish reason for Anthropic's stop reason.</summary>
    internal static string? FinishReason(string? stopReason) => stopReason switch
    {
        null => null,
        "end_turn" or "stop_sequence" => "stop",
        "max_tokens" or "model_context_window_exceeded" => "length",
        "tool_use" => "tool_calls",
        _ => stopReason,
    };

    private static ProviderRefusalException Refusal(ModelCall call) =>
        new($"The model at {call.Target.Name} declined to answer this request (refusal). Rephrase it, or ask another model.");

    /// <summary>One content block of a stream, as its <c>content_block_start</c> opened it and its deltas filled it.</summary>
    private sealed class StreamBlock(JsonObject start)
    {
        public StringBuilder Text { get; } = new();
        public StringBuilder Json { get; } = new();
        public StringBuilder Signature { get; } = new();

        /// <summary>The finished block as the API would have written it in a whole message, and (for a <c>tool_use</c> block) the argument
        /// JSON exactly as streamed (malformed JSON stays malformed, so the loop reports it).</summary>
        public JsonObject Finish(out string argumentsJson)
        {
            argumentsJson = "{}";
            switch ((string?)start["type"])
            {
                case "text":
                    start["text"] = ((string?)start["text"] ?? "") + Text;
                    break;
                case "thinking":
                    start["thinking"] = ((string?)start["thinking"] ?? "") + Text;
                    if (Signature.Length > 0) start["signature"] = Signature.ToString();
                    break;
                case "tool_use":
                    argumentsJson = Json.Length == 0 ? "{}" : Json.ToString();
                    start["input"] = ProviderWire.ArgumentsObject(argumentsJson);
                    break;
            }
            return start;
        }
    }
}
