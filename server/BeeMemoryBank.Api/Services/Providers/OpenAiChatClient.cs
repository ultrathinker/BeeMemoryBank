using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Api.Models;

namespace BeeMemoryBank.Api.Services.Providers;

/// <summary>
/// The client of every provider that speaks OpenAI's chat-completions format (<see cref="ProviderProtocols.OpenAiChat"/>): OpenRouter,
/// Z.ai, OpenAI, DeepSeek, the OpenAI-compatible endpoints of Anthropic and Google, Ollama and the other local servers ...
///
/// <para>Server-side only - the browser never calls a provider. Every request goes to <c>{approved base URL}/chat/completions</c> through
/// <see cref="ProviderEgress"/>, which refuses anything but the provider's approved host (and, for a provider on the open internet, any
/// non-public address) and follows no redirect. What differs between providers of this format is in the provider's
/// <see cref="ProviderWireOptions"/>: how a reasoning effort is sent, whether a stream asks for a usage chunk, which field carries the
/// output limit, whether image generation is offered. A provider that reports no usage breaks nothing: the counts stay empty.</para>
///
/// <para>The plaintext key of a <see cref="ModelCall"/> is used for that one request and not kept; an empty key (a local server that needs
/// none) sends no Authorization header. Non-streaming and streaming completions, tool calls, vision content parts and image generation
/// keep the request and response shapes the chat relied on when this was the OpenRouter-only client.</para>
/// </summary>
public sealed class OpenAiChatClient : IModelProtocolClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly ProviderEgress _egress;
    private readonly ILogger<OpenAiChatClient> _logger;

    public OpenAiChatClient(ProviderEgress egress, ILogger<OpenAiChatClient> logger)
    {
        _egress = egress;
        _logger = logger;
    }

    public string Protocol => ProviderProtocols.OpenAiChat;

    public bool SupportsImageGeneration(ProviderWireOptions options) => options.ImageGeneration;

    private static void Validate(ModelCall call, int messageCount)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (string.IsNullOrWhiteSpace(call.Model))
            throw new ArgumentException("Model is required.", nameof(call));
        if (messageCount == 0)
            throw new ArgumentException("At least one message is required.", nameof(call));
        if (call.Target.Protocol != ProviderProtocols.OpenAiChat)
            throw new ProviderUnavailableException($"Provider '{call.Target.Name}' does not speak the {ProviderProtocols.OpenAiChat} protocol.");
    }

    private static HttpRequestMessage Post(ModelCall call, string relative, object payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, call.Target.Endpoint(relative));
        if (!string.IsNullOrEmpty(call.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", call.ApiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");
        return req;
    }

    /// <summary>A failed request: logged with the status, the error's type word and the request id only - never the body, which can quote
    /// what was sent.</summary>
    private ProviderHttpException Failure(ModelCall call, HttpResponseMessage resp, string? body, string what)
    {
        var status = (int)resp.StatusCode;
        _logger.LogWarning("{Provider} {What} failed: HTTP {Status} {Code} request-id={RequestId}", call.Target.Name, what, status,
            ProviderWire.ErrorCode(body, "type") ?? ProviderWire.ErrorCode(body, "code"), ProviderWire.RequestId(resp));
        return new ProviderHttpException(status, $"{call.Target.Name} completion failed (HTTP {status}).");
    }

    /// <summary>A plain completion. The chat's form sends no limit, no temperature and no reasoning field; Dream's form sends all three
    /// the provider's way. No body of an upstream error is logged, here or on any other path (a provider's refusal can quote an excerpt of
    /// what it was sent, a moderation error repeats the flagged input, and what is sent is the text of notes). For a model that reasons first,
    /// the output limit covers the reasoning and the answer together. The answer says why the provider stopped and whether the model reasoned.</summary>
    public async Task<ChatCompleteResponse> CompleteAsync(ModelCall call, IReadOnlyList<ChatRoleMessage> messages, CompletionOptions options,
        CancellationToken ct)
    {
        Validate(call, messages.Count);
        var wire = call.Target.Options;
        var payload = new CompletionRequest
        {
            Model = call.Model,
            Messages = messages,
            Stream = false,
            MaxTokens = wire.MaxTokensField == MaxTokensFields.MaxTokens ? options.MaxTokens : null,
            MaxCompletionTokens = wire.MaxTokensField == MaxTokensFields.MaxCompletionTokens ? options.MaxTokens : null,
            Temperature = options.Temperature,
        };
        ApplyReasoning(payload, wire, options.ReasoningEffort);

        using var req = Post(call, "chat/completions", payload);
        using var resp = await _egress.SendAsync(call.Target, req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw Failure(call, resp, body, "completion");

        var doc = JsonSerializer.Deserialize<CompletionResponse>(body, JsonOpts)
                  ?? throw new InvalidOperationException($"{call.Target.Name} returned an empty response body.");

        var choice = doc.Choices.Count > 0
            ? doc.Choices[0]
            : throw new InvalidOperationException($"{call.Target.Name} returned no completion choices.");

        return new ChatCompleteResponse(
            Content: choice.Message?.Content ?? "",
            PromptTokens: doc.Usage?.PromptTokens ?? 0,
            CompletionTokens: doc.Usage?.CompletionTokens ?? 0,
            Model: doc.Model ?? call.Model)
        {
            FinishReason = choice.FinishReason,
            HasReasoning = ReportsReasoning(choice.Message, doc.Usage),
        };
    }

    /// <summary>The reasoning effort, the way this provider takes it (or not at all).</summary>
    private static void ApplyReasoning(CompletionRequest payload, ProviderWireOptions wire, string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort)) return;
        switch (wire.Reasoning)
        {
            case ReasoningStyles.OpenRouter:
                payload.Reasoning = new ReasoningPayload { Effort = effort };
                break;
            case ReasoningStyles.OpenAi:
                payload.ReasoningEffort = effort;
                break;
            case ReasoningStyles.Zai:
                // Z.ai has no effort levels, only thinking on or off: a low effort is "think as little as you may", which is off.
                payload.Thinking = new ThinkingPayload { Type = effort is "low" or "minimal" or "none" ? "disabled" : "enabled" };
                break;
        }
    }

    /// <summary>
    /// NON-STREAMING completion that supports an OpenAI-style tool-calling loop. Pass the curated tool definitions (see
    /// <see cref="ChatToolDispatcher.ToolDefinitions"/>); pass <c>tools</c> as null/empty to skip tool-calling. Returns the assistant turn with
    /// any tool calls it emitted (the caller executes them and re-loops).
    /// </summary>
    public async Task<ToolCompletionResult> CompleteWithToolsAsync(ModelCall call, IReadOnlyList<ChatToolMessage> messages,
        IReadOnlyList<ChatToolDefinition>? tools, CancellationToken ct)
    {
        Validate(call, messages.Count);
        var payload = new ToolsCompletionRequest
        {
            Model = call.Model,
            Messages = ToWire(messages),
            Tools = tools is { Count: > 0 } ? tools : null,
            ToolChoice = tools is { Count: > 0 } ? "auto" : null,
            Stream = false
        };

        using var req = Post(call, "chat/completions", payload);
        using var resp = await _egress.SendAsync(call.Target, req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw Failure(call, resp, body, "tool completion");

        var doc = JsonSerializer.Deserialize<CompletionResponse>(body, JsonOpts)
                  ?? throw new InvalidOperationException($"{call.Target.Name} returned an empty response body.");

        var choice = doc.Choices.Count > 0
            ? doc.Choices[0]
            : throw new InvalidOperationException($"{call.Target.Name} returned no completion choices.");

        var msg = choice.Message;
        List<ResolvedToolCall>? toolCalls = null;
        if (msg?.ToolCalls is { Count: > 0 })
        {
            toolCalls = msg.ToolCalls
                .Where(tc => tc.Function?.Name != null)
                .Select(tc => new ResolvedToolCall(
                    tc.Id ?? "",
                    tc.Function!.Name!,
                    tc.Function.Arguments ?? "{}"))
                .ToList();
        }

        return new ToolCompletionResult(
            Content: msg?.Content,
            ToolCalls: toolCalls,
            Model: doc.Model ?? call.Model,
            PromptTokens: doc.Usage?.PromptTokens,
            CompletionTokens: doc.Usage?.CompletionTokens);
    }

    /// <summary>
    /// STREAMING completion with tool-calling support. Runs an OpenAI-style streaming chat completion (<c>stream:true</c>), reads the upstream
    /// SSE frame-by-frame, and invokes <paramref name="onTextDelta"/> for each incremental <c>content</c> delta as it arrives (so the caller
    /// can forward text deltas to the browser in real time). Tool-call deltas are accumulated by their <c>index</c> internally; the method
    /// returns the fully-assembled turn as a <see cref="ToolCompletionResult"/> (same shape as <see cref="CompleteWithToolsAsync"/>), so the
    /// caller's tool-loop logic is identical to the non-streaming path.
    ///
    /// <para>Honors <paramref name="ct"/> throughout: the request is sent with <see cref="HttpCompletionOption.ResponseHeadersRead"/> and the
    /// token is forwarded into both the send and every line read. A client disconnect (passed in as the request's <c>RequestAborted</c>)
    /// therefore aborts the upstream call - no wasted tokens/billing. A final usage chunk is asked for only where the provider takes
    /// <c>stream_options</c> (<see cref="ProviderWireOptions.StreamUsage"/>); the counts stay null when none arrives.</para>
    /// </summary>
    public async Task<ToolCompletionResult> StreamWithToolsAsync(ModelCall call, IReadOnlyList<ChatToolMessage> messages,
        IReadOnlyList<ChatToolDefinition>? tools, Func<string, CancellationToken, ValueTask> onTextDelta, CancellationToken ct)
    {
        Validate(call, messages.Count);
        var payload = new ToolsCompletionRequest
        {
            Model = call.Model,
            Messages = ToWire(messages),
            Tools = tools is { Count: > 0 } ? tools : null,
            ToolChoice = tools is { Count: > 0 } ? "auto" : null,
            Stream = true,
            StreamOptions = call.Target.Options.StreamUsage ? new StreamOptionsPayload { IncludeUsage = true } : null
        };

        // stream:true → expect a (potentially long-lived) text/event-stream body. ResponseHeadersRead returns as soon as the status+headers
        // are available so frames are pumped incrementally instead of buffering the whole completion.
        using var req = Post(call, "chat/completions", payload);
        using var resp = await _egress.SendAsync(call.Target, req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            // Drain the (short) error body for its error type, then surface a non-success to the loop.
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw Failure(call, resp, body, "streaming completion");
        }

        // Read the upstream stream as it arrives and parse SSE `data:` frames line by line.
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var contentBuilder = new StringBuilder();
        // tool_calls stream in by index: the first delta for an index carries (id, name), subsequent deltas carry argument fragments.
        // Accumulate per-index, then materialize at [DONE].
        var toolCallsByIndex = new SortedDictionary<int, AccumulatedToolCall>();
        string? resolvedModel = null;
        // Captured from the final usage chunk (stream_options.include_usage). Null until/if it arrives.
        int? promptTokens = null;
        int? completionTokens = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break; // upstream closed

            // SSE frames are `data: <payload>\n\n` (some servers omit the space). Blank lines are separators we ignore; comment lines (`:`)
            // are keep-alives we ignore.
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var streamPayload = line.Substring("data:".Length).TrimStart(' ');
            if (streamPayload == "[DONE]")
                break;

            StreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<StreamChunk>(streamPayload, JsonOpts);
            }
            catch (JsonException)
            {
                // Malformed frame (rare) — skip rather than kill the whole stream.
                continue;
            }
            if (chunk is null) continue;
            if (!string.IsNullOrEmpty(chunk.Model))
                resolvedModel = chunk.Model;

            // The usage chunk arrives with an empty/absent choices array (the choices guard below would otherwise skip it), so capture usage
            // FIRST. Later usage chunks (if any) overwrite.
            if (chunk.Usage is not null)
            {
                promptTokens = chunk.Usage.PromptTokens;
                completionTokens = chunk.Usage.CompletionTokens;
            }

            if (chunk.Choices is null || chunk.Choices.Count == 0)
                continue;

            var delta = chunk.Choices[0].Delta;
            if (delta is null) continue;

            // Text content delta → forward to the caller immediately.
            if (delta.Content is { Length: > 0 } content)
            {
                contentBuilder.Append(content);
                if (onTextDelta is not null)
                    await onTextDelta(content, ct);
            }

            // Tool-call deltas → accumulate by index. The first delta for an index carries the call id + function name; later deltas append
            // argument fragments.
            if (delta.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in delta.ToolCalls)
                {
                    if (!toolCallsByIndex.TryGetValue(tc.Index, out var acc))
                    {
                        acc = new AccumulatedToolCall();
                        toolCallsByIndex[tc.Index] = acc;
                    }
                    if (!string.IsNullOrEmpty(tc.Id))
                        acc.Id = tc.Id;
                    if (tc.Function is not null)
                    {
                        if (!string.IsNullOrEmpty(tc.Function.Name))
                            acc.Name = tc.Function.Name;
                        if (tc.Function.Arguments is not null)
                            acc.Arguments.Append(tc.Function.Arguments);
                    }
                }
            }
        }

        // Materialize the assembled turn.
        List<ResolvedToolCall>? toolCalls = null;
        if (toolCallsByIndex.Count > 0)
        {
            toolCalls = toolCallsByIndex
                .Where(kv => !string.IsNullOrEmpty(kv.Value.Name))
                .Select(kv => new ResolvedToolCall(
                    kv.Value.Id ?? "",
                    kv.Value.Name!,
                    kv.Value.Arguments.ToString()))
                .ToList();
            if (toolCalls.Count == 0)
                toolCalls = null;
        }

        // Diagnostic: some models accept the `tools` schema but never actually emit a real tool_calls delta — instead they narrate a fake
        // tool invocation as plain text content. That looks like "the tool didn't work" from the user's side but is a model-capability
        // limitation, not a dispatch bug. Only counts are logged: the model's output routinely quotes vault content it just read via a tool
        // result (e.g. an article body).
        if (tools is { Count: > 0 })
        {
            _logger.LogDebug(
                "{Provider} tool-call turn: model={Model} toolCalls={ToolCallCount} contentLength={ContentLength}",
                call.Target.Name, resolvedModel ?? call.Model, toolCalls?.Count ?? 0, contentBuilder.Length);
        }

        return new ToolCompletionResult(
            Content: contentBuilder.ToString(),
            ToolCalls: toolCalls,
            Model: resolvedModel ?? call.Model,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens);
    }

    // Accumulator for one streaming tool call (keyed by the delta's `index`).
    private sealed class AccumulatedToolCall
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    /// <summary>
    /// NON-STREAMING completion for an image-generation model, through the SAME <c>chat/completions</c> endpoint as the text/vision paths
    /// (OpenRouter's image models answer there with an image content part). Only for a provider whose options offer image generation; any
    /// other is refused before a request is made. No tools are declared. The response <c>content</c> is parsed TOLERANTLY: a plain string,
    /// an ARRAY of content parts with <c>image_url</c> entries, a sibling <c>message.images</c> array, or the OpenAI images-API shape (a root
    /// <c>data: [{url}|{b64_json}]</c>). The returned <c>ImageSources</c> are verbatim strings for the caller to materialize into bytes.
    /// </summary>
    public async Task<ImageGenResult> GenerateImageAsync(ModelCall call, IReadOnlyList<ChatToolMessage> messages, CancellationToken ct)
    {
        Validate(call, messages.Count);
        if (!SupportsImageGeneration(call.Target.Options))
            throw new ProviderUnavailableException($"Image generation is not available through provider '{call.Target.Name}'.");

        var payload = new ToolsCompletionRequest
        {
            Model = call.Model,
            Messages = ToWire(messages),
            Tools = null,
            ToolChoice = null,
            Stream = false
        };

        using var req = Post(call, "chat/completions", payload);
        using var resp = await _egress.SendAsync(call.Target, req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw Failure(call, resp, body, "image-gen completion");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch { throw new InvalidOperationException($"{call.Target.Name} returned a malformed (non-JSON) response body."); }

        using (doc)
        {
            var resolvedModel = doc.RootElement.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String
                ? mEl.GetString()!
                : call.Model;

            var textBuilder = new StringBuilder();
            var imageSources = new List<string>();

            // Shape 1: chat/completions — choices[0].message.content (string OR array of parts).
            if (doc.RootElement.TryGetProperty("choices", out var choicesEl)
                && choicesEl.ValueKind == JsonValueKind.Array
                && choicesEl.GetArrayLength() > 0)
            {
                var choice = choicesEl[0];
                if (choice.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
                {
                    if (msgEl.TryGetProperty("content", out var contentEl))
                        ExtractContent(contentEl, textBuilder, imageSources);

                    // Shape 1.5: some providers (e.g. Gemini image-output models via OpenRouter) return the generated image(s) in a sibling
                    // `message.images` array instead of embedding them in `content` — each entry shaped like a content part.
                    if (msgEl.TryGetProperty("images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in imagesEl.EnumerateArray())
                        {
                            if (part.ValueKind != JsonValueKind.Object) continue;
                            if (part.TryGetProperty("image_url", out var iuEl)
                                && iuEl.ValueKind == JsonValueKind.Object
                                && iuEl.TryGetProperty("url", out var uEl)
                                && uEl.ValueKind == JsonValueKind.String)
                                imageSources.Add(uEl.GetString()!);
                        }
                    }
                }
            }

            // Shape 2 (fallback): OpenAI images-API — root data: [{url}|{b64_json}].
            if (imageSources.Count == 0
                && doc.RootElement.TryGetProperty("data", out var dataEl)
                && dataEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (item.TryGetProperty("b64_json", out var b64El) && b64El.ValueKind == JsonValueKind.String)
                        imageSources.Add("data:image/png;base64," + b64El.GetString());
                    else if (item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                        imageSources.Add(urlEl.GetString()!);
                }
            }

            // Only the shape is logged, never the body: this response can carry the model's full text answer (which may quote vault content the
            // prompt included).
            if (imageSources.Count == 0)
                _logger.LogWarning("{Provider} image-gen completion returned no image sources ({Length} characters of text).", call.Target.Name,
                    textBuilder.Length);

            return new ImageGenResult(
                Text: textBuilder.Length == 0 ? null : textBuilder.ToString(),
                ImageSources: imageSources,
                Model: resolvedModel);
        }
    }

    /// <summary>The provider's own model list, <c>GET {base}/models</c> (OpenAI format: <c>{data: [{id, ...}]}</c>). Only the id, a name and
    /// a context window are read; any other shape yields what could be read, never an error.</summary>
    public async Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(ProviderEgressTarget target, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, target.Endpoint("models"));
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await _egress.SendAsync(target, req, HttpCompletionOption.ResponseContentRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("{Provider} model list failed: HTTP {Status}", target.Name, (int)resp.StatusCode);
            throw new ProviderHttpException((int)resp.StatusCode, $"{target.Name} model list failed (HTTP {(int)resp.StatusCode}).");
        }
        var body = await resp.Content.ReadAsStringAsync(ct);
        var list = new List<ProviderModelInfo>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("data", out var d) ? d
                : doc.RootElement.TryGetProperty("models", out var m) ? m : default;
            if (data.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = Str(item, "id") ?? Str(item, "name");
                if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsWhiteSpace)) continue;
                var name = Str(item, "name") ?? Str(item, "display_name");
                list.Add(new ProviderModelInfo(id, name is { Length: <= 200 } ? name : null, Int(item, "context_length") ?? Int(item, "context_window")));
                if (list.Count >= 1000) break;
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{target.Name} answered the model list with something that is not JSON.");
        }
        return list;

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0 ? n : null;
    }

    /// <summary>Reads a <c>content</c> JSON value that is either a plain string or an array of content parts, appending text to
    /// <paramref name="text"/> and any image references to <paramref name="images"/>. Recognized image part shapes:
    /// <c>{type:image_url, image_url:{url}}</c> and (some providers) <c>{type:image, image:{url}}</c>.</summary>
    private static void ExtractContent(JsonElement contentEl, StringBuilder text, List<string> images)
    {
        switch (contentEl.ValueKind)
        {
            case JsonValueKind.String:
                text.Append(contentEl.GetString() ?? "");
                break;
            case JsonValueKind.Array:
                foreach (var part in contentEl.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object) continue;
                    var type = part.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String
                        ? tEl.GetString() : null;
                    if (type == "text" && part.TryGetProperty("text", out var txEl) && txEl.ValueKind == JsonValueKind.String)
                        text.Append(txEl.GetString());
                    else if (part.TryGetProperty("image_url", out var iuEl)
                             && iuEl.ValueKind == JsonValueKind.Object
                             && iuEl.TryGetProperty("url", out var uEl)
                             && uEl.ValueKind == JsonValueKind.String)
                        images.Add(uEl.GetString()!);
                    else if (part.TryGetProperty("image", out var imEl)
                             && imEl.ValueKind == JsonValueKind.Object
                             && imEl.TryGetProperty("url", out var u2El)
                             && u2El.ValueKind == JsonValueKind.String)
                        images.Add(u2El.GetString()!);
                }
                break;
        }
    }

    // ── OpenAI chat-completions wire shapes (subset) ───────────────────────────
    // Plain POCOs with explicit [JsonPropertyName] for robust System.Text.Json deserialization.

    private sealed class CompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = "";
        [JsonPropertyName("messages")] public IReadOnlyList<ChatRoleMessage> Messages { get; init; } = [];
        [JsonPropertyName("stream")] public bool Stream { get; init; }
        // Null for the chat (JsonOpts leaves nulls out); set by the Dream model step, in the provider's field.
        [JsonPropertyName("max_tokens")] public int? MaxTokens { get; init; }
        [JsonPropertyName("max_completion_tokens")] public int? MaxCompletionTokens { get; init; }
        [JsonPropertyName("temperature")] public double? Temperature { get; init; }
        // At most one of the three reasoning fields, the provider's (ProviderWireOptions.Reasoning).
        [JsonPropertyName("reasoning")] public ReasoningPayload? Reasoning { get; set; }
        [JsonPropertyName("reasoning_effort")] public string? ReasoningEffort { get; set; }
        [JsonPropertyName("thinking")] public ThinkingPayload? Thinking { get; set; }
    }

    // OpenRouter's unified reasoning parameter.
    private sealed class ReasoningPayload
    {
        [JsonPropertyName("effort")] public string Effort { get; init; } = "";
    }

    // Z.ai's thinking switch.
    private sealed class ThinkingPayload
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "";
    }

    // Tool-calling request shape. tool_calls / tool_call_id on individual messages are carried by ChatToolMessage (defined in
    // ChatToolModels.cs). Messages are mapped to WireMessage (below) so a user turn carrying an image serializes as a multimodal content
    // ARRAY [{type:text},{type:image_url}] (vision models) instead of a plain string.
    private sealed class ToolsCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = "";
        [JsonPropertyName("messages")] public IReadOnlyList<WireMessage> Messages { get; init; } = [];
        [JsonPropertyName("tools")] public IReadOnlyList<ChatToolDefinition>? Tools { get; init; }
        [JsonPropertyName("tool_choice")] public string? ToolChoice { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
        // stream_options.include_usage is set ONLY by StreamWithToolsAsync, and only for a provider that takes it. JsonOpts ignores nulls, so
        // CompleteWithToolsAsync / GenerateImageAsync (Stream=false) keep a byte-identical wire payload — sending stream_options on a
        // non-streaming request is rejected by some providers.
        [JsonPropertyName("stream_options")] public StreamOptionsPayload? StreamOptions { get; init; }
    }

    // OpenAI-compatible: requests a final SSE chunk carrying the aggregated `usage` object before `data: [DONE]`. The consumer tolerates its
    // absence (fields stay null).
    private sealed class StreamOptionsPayload
    {
        [JsonPropertyName("include_usage")] public bool IncludeUsage { get; init; }
    }

    // The on-the-wire message shape. `content` is `object?` so it serializes as a JSON STRING for plain text turns and as a JSON ARRAY of
    // content parts for vision turns. tool_calls/tool_call_id are carried verbatim so the tool loop is unaffected.
    private sealed class WireMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public object? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<ChatToolCall>? ToolCalls { get; set; }
        [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }
    }

    private sealed class WireContentPart
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("image_url")] public WireImageUrl? ImageUrl { get; set; }
    }

    private sealed class WireImageUrl
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
    }

    /// <summary>Maps the in-memory <see cref="ChatToolMessage"/> list to the egress wire shape. A message carrying
    /// <see cref="ChatToolMessage.ImageDataUrls"/> becomes a multimodal content array (one text part + one image part per attached image) for
    /// vision models; every other message serializes content as a plain string (or null for assistant tool-call turns).</summary>
    private static List<WireMessage> ToWire(IReadOnlyList<ChatToolMessage> messages)
    {
        var list = new List<WireMessage>(messages.Count);
        foreach (var m in messages)
        {
            object? content;
            if (m.ImageDataUrls is { Count: > 0 })
            {
                var parts = new List<WireContentPart> { new() { Type = "text", Text = m.Content ?? "" } };
                foreach (var url in m.ImageDataUrls)
                    parts.Add(new WireContentPart { Type = "image_url", ImageUrl = new WireImageUrl { Url = url } });
                content = parts;
            }
            else
            {
                content = m.Content;
            }
            list.Add(new WireMessage
            {
                Role = m.Role,
                Content = content,
                ToolCalls = m.ToolCalls,
                ToolCallId = m.ToolCallId
            });
        }
        return list;
    }

    private sealed class CompletionResponse
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("choices")] public List<Choice> Choices { get; set; } = [];
        [JsonPropertyName("usage")] public Usage? Usage { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("message")] public ChoiceMessage? Message { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }

    private sealed class ChoiceMessage
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        // Present on assistant turns that requested tool calls (tool loop).
        [JsonPropertyName("tool_calls")] public List<ChoiceToolCall>? ToolCalls { get; set; }
        // What a reasoning model thought before it answered (OpenRouter `reasoning`, DeepSeek/Z.ai `reasoning_content`): read only to know
        // that it did. Kept as raw JSON, so whatever shape a provider sends cannot break the reading of a completion the chat also uses.
        [JsonPropertyName("reasoning")] public JsonElement? Reasoning { get; set; }
        [JsonPropertyName("reasoning_content")] public JsonElement? ReasoningContent { get; set; }
        [JsonPropertyName("reasoning_details")] public JsonElement? ReasoningDetails { get; set; }
    }

    private sealed class ChoiceToolCall
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("function")] public ChoiceToolCallFunction? Function { get; set; }
    }

    private sealed class ChoiceToolCallFunction
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    }

    private sealed class Usage
    {
        [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
        [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
        [JsonPropertyName("completion_tokens_details")] public JsonElement? CompletionTokensDetails { get; set; }
    }

    /// <summary>True when the provider says the model reasoned: a reasoning text, reasoning details, or reasoning tokens in the usage.
    /// Tolerant of any shape (a provider that sends something else simply reads as "no").</summary>
    private static bool ReportsReasoning(ChoiceMessage? message, Usage? usage)
    {
        foreach (var candidate in new[] { message?.Reasoning, message?.ReasoningContent })
        {
            if (candidate is { } text
                && (text.ValueKind == JsonValueKind.String ? !string.IsNullOrWhiteSpace(text.GetString())
                    : text.ValueKind is JsonValueKind.Object or JsonValueKind.Array))
                return true;
        }
        if (message?.ReasoningDetails is { ValueKind: JsonValueKind.Array } details && details.GetArrayLength() > 0) return true;
        return usage?.CompletionTokensDetails is { ValueKind: JsonValueKind.Object } d
               && d.TryGetProperty("reasoning_tokens", out var n) && n.ValueKind == JsonValueKind.Number
               && n.TryGetInt64(out var tokens) && tokens > 0;
    }

    // Streaming chunk shapes (OpenAI-compatible SSE `data:` payload). Property names are snake_case to match the wire format exactly.
    private sealed class StreamChunk
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("choices")] public List<StreamChoice>? Choices { get; set; }
        // Present (with an empty/absent choices array) on the final usage chunk when stream_options.include_usage was requested (some
        // providers send it unasked). Nullable — a provider can omit it.
        [JsonPropertyName("usage")] public Usage? Usage { get; set; }
    }

    private sealed class StreamChoice
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("delta")] public StreamDelta? Delta { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }

    private sealed class StreamDelta
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<StreamToolCallDelta>? ToolCalls { get; set; }
    }

    private sealed class StreamToolCallDelta
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("function")] public StreamToolCallFunctionDelta? Function { get; set; }
    }

    private sealed class StreamToolCallFunctionDelta
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    }
}

/// <summary>
/// Thrown when a provider returns a non-success HTTP status for a request (non-streaming or streaming). Carries the
/// <see cref="StatusCode"/> so multi-key failover (<see cref="ModelRouter"/>) can classify it (401→disable, 402/429→cooldown,
/// anything else→retry-next) without parsing the message string.
///
/// <para>Deliberately derives from <see cref="InvalidOperationException"/>: every completion call-site catches
/// <c>InvalidOperationException</c>, so this keeps that behaviour intact, while failover code catches the more specific type FIRST to
/// inspect <see cref="StatusCode"/>. Transport errors (timeout, refused, DNS) surface as <see cref="HttpRequestException"/> and are NOT
/// wrapped here — the failover catches those separately (retry-next, no cooldown).</para>
/// </summary>
public sealed class ProviderHttpException : InvalidOperationException
{
    /// <summary>The upstream HTTP status code (e.g. 401, 402, 429, 500).</summary>
    public int StatusCode { get; }

    public ProviderHttpException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// The parsed result of an image-generation completion. <see cref="ImageSources"/> are verbatim strings exactly as the provider returned
/// them — <c>data:</c> URLs, http(s) URLs, or already-wrapped base64. The chat endpoint materializes each into bytes (decoding data: URLs
/// directly, fetching http(s) URLs server-side) so the image renders inline through the CSP-allowed <c>/api/chat/attachments/{id}</c> route
/// and persists with the conversation. <see cref="Text"/> carries any caption/text the model emitted alongside the image(s).</summary>
public sealed record ImageGenResult(string? Text, List<string> ImageSources, string Model);
