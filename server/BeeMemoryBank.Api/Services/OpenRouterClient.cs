using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Api.Models;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Thin client for OpenRouter's OpenAI-compatible chat-completions endpoint.
///
/// Phase 0 scope (plan §2): NON-STREAMING completion, SINGLE key, egress PINNED to
/// <c>https://openrouter.ai</c>. Multi-key failover, categories, and streaming arrive in
/// later phases (plan §2 Phase 2/4). The browser never calls OpenRouter directly — this is
/// the only egress path and it lives server-side (plan §1: "Egress pinned to
/// https://openrouter.ai; browser never calls OpenRouter").
/// </summary>
public sealed class OpenRouterClient
{
    // Egress is pinned. Do NOT make this configurable — keeps the only outbound LLM call on a
    // known host and prevents an SSRF-style redirect of vault content to an attacker host.
    private const string CompletionUrl = "https://openrouter.ai/api/v1/chat/completions";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenRouterClient> _logger;

    public OpenRouterClient(HttpClient httpClient, ILogger<OpenRouterClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>Requests a non-streaming completion. The plaintext <paramref name="apiKey"/>
    /// is used only for this request and is not retained by the client.</summary>
    public async Task<ChatCompleteResponse> CompleteAsync(
        string apiKey,
        string model,
        IReadOnlyList<ChatRoleMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));
        if (messages.Count == 0)
            throw new ArgumentException("At least one message is required.", nameof(messages));

        var payload = new CompletionRequest
        {
            Model = model,
            Messages = messages,
            Stream = false
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, CompletionUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

        using var resp = await _httpClient.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenRouter completion failed: HTTP {Status} body={Body}", (int)resp.StatusCode, body);
            throw new OpenRouterHttpException((int)resp.StatusCode,
                $"OpenRouter completion failed (HTTP {(int)resp.StatusCode}).");
        }

        var doc = JsonSerializer.Deserialize<CompletionResponse>(body, JsonOpts)
                  ?? throw new InvalidOperationException("OpenRouter returned an empty response body.");

        var choice = doc.Choices.Count > 0
            ? doc.Choices[0]
            : throw new InvalidOperationException("OpenRouter returned no completion choices.");

        return new ChatCompleteResponse(
            Content: choice.Message?.Content ?? "",
            PromptTokens: doc.Usage?.PromptTokens ?? 0,
            CompletionTokens: doc.Usage?.CompletionTokens ?? 0,
            Model: doc.Model ?? model);
    }

    /// <summary>
    /// Phase 1: NON-STREAMING completion that supports an OpenAI-style tool-calling loop. The
    /// plaintext <paramref name="apiKey"/> is used only for this request and is not retained.
    /// Pass the curated tool definitions (see <see cref="ChatToolDispatcher.ToolDefinitions"/>);
    /// pass <c>tools</c> as null/empty to skip tool-calling. Returns the assistant turn with any
    /// tool calls it emitted (the caller executes them and re-loops). Egress is still pinned to
    /// <see href="https://openrouter.ai"/> (same constant as <see cref="CompleteAsync"/>).
    /// </summary>
    public async Task<ToolCompletionResult> CompleteWithToolsAsync(
        string apiKey,
        string model,
        IReadOnlyList<ChatToolMessage> messages,
        IReadOnlyList<ChatToolDefinition>? tools,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));
        if (messages.Count == 0)
            throw new ArgumentException("At least one message is required.", nameof(messages));

        var payload = new ToolsCompletionRequest
        {
            Model = model,
            Messages = ToWire(messages),
            Tools = tools is { Count: > 0 } ? tools : null,
            ToolChoice = tools is { Count: > 0 } ? "auto" : null,
            Stream = false
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, CompletionUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

        using var resp = await _httpClient.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenRouter tool completion failed: HTTP {Status} body={Body}", (int)resp.StatusCode, body);
            throw new OpenRouterHttpException((int)resp.StatusCode,
                $"OpenRouter completion failed (HTTP {(int)resp.StatusCode}).");
        }

        var doc = JsonSerializer.Deserialize<CompletionResponse>(body, JsonOpts)
                  ?? throw new InvalidOperationException("OpenRouter returned an empty response body.");

        var choice = doc.Choices.Count > 0
            ? doc.Choices[0]
            : throw new InvalidOperationException("OpenRouter returned no completion choices.");

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
            Model: doc.Model ?? model);
    }

    /// <summary>
    /// Phase 2: STREAMING completion with tool-calling support. Runs an OpenAI-style streaming
    /// chat completion (<c>stream:true</c>), reads the upstream SSE frame-by-frame, and invokes
    /// <paramref name="onTextDelta"/> for each incremental <c>content</c> delta as it arrives
    /// (so the caller can forward text deltas to the browser in real time). Tool-call deltas are
    /// accumulated by their <c>index</c> internally; the method returns the fully-assembled turn
    /// as a <see cref="ToolCompletionResult"/> (same shape as <see cref="CompleteWithToolsAsync"/>),
    /// so the caller's tool-loop logic is identical to the non-streaming path.
    ///
    /// <para>The plaintext <paramref name="apiKey"/> is used only for this request and is not
    /// retained. Egress is still pinned to <see cref="CompletionUrl"/>.</para>
    ///
    /// <para>Honors <paramref name="cancellationToken"/> throughout: <see cref="HttpClient.SendAsync"/>
    /// uses <see cref="HttpCompletionOption.ResponseHeadersRead"/> and the token is forwarded into
    /// both the send and every line read. A client disconnect (passed in as the request's
    /// <c>RequestAborted</c>) therefore aborts the upstream OpenRouter call — no wasted tokens/billing
    /// (plan §1 "Cancellation", §2 Phase 2 accept criterion).</para>
    /// </summary>
    public async Task<ToolCompletionResult> StreamWithToolsAsync(
        string apiKey,
        string model,
        IReadOnlyList<ChatToolMessage> messages,
        IReadOnlyList<ChatToolDefinition>? tools,
        Func<string, CancellationToken, ValueTask> onTextDelta,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));
        if (messages.Count == 0)
            throw new ArgumentException("At least one message is required.", nameof(messages));

        var payload = new ToolsCompletionRequest
        {
            Model = model,
            Messages = ToWire(messages),
            Tools = tools is { Count: > 0 } ? tools : null,
            ToolChoice = tools is { Count: > 0 } ? "auto" : null,
            Stream = true
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, CompletionUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        // stream:true → expect a (potentially long-lived) text/event-stream body. ResponseHeadersRead
        // returns as soon as the status+headers are available so we can pump frames incrementally
        // instead of buffering the whole completion.
        req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            // Drain the (short) error body for logging, then surface a non-success to the loop.
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("OpenRouter streaming completion failed: HTTP {Status} body={Body}", (int)resp.StatusCode, body);
            throw new OpenRouterHttpException((int)resp.StatusCode,
                $"OpenRouter completion failed (HTTP {(int)resp.StatusCode}).");
        }

        // Read the upstream stream as it arrives and parse SSE `data:` frames line by line.
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var contentBuilder = new StringBuilder();
        // tool_calls stream in by index: the first delta for an index carries (id, name), subsequent
        // deltas carry argument fragments. Accumulate per-index, then materialize at [DONE].
        var toolCallsByIndex = new SortedDictionary<int, AccumulatedToolCall>();
        string? resolvedModel = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break; // upstream closed

            // SSE frames are `data: <payload>\n\n`. Blank lines are separators we ignore; comment
            // lines (`:`) are keep-alives we ignore.
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var streamPayload = line.Substring("data: ".Length);
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

            if (chunk.Choices is null || chunk.Choices.Count == 0)
                continue;

            var delta = chunk.Choices[0].Delta;
            if (delta is null) continue;

            // Text content delta → forward to the caller immediately.
            if (delta.Content is { Length: > 0 } content)
            {
                contentBuilder.Append(content);
                if (onTextDelta is not null)
                    await onTextDelta(content, cancellationToken);
            }

            // Tool-call deltas → accumulate by index. The first delta for an index carries the
            // call id + function name; later deltas append argument fragments.
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

        return new ToolCompletionResult(
            Content: contentBuilder.ToString(),
            ToolCalls: toolCalls,
            Model: resolvedModel ?? model);
    }

    // Accumulator for one streaming tool call (keyed by the delta's `index`).
    private sealed class AccumulatedToolCall
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    // ── OpenRouter wire shapes (subset) ────────────────────────────────────────
    // Plain POCOs with explicit [JsonPropertyName] for robust System.Text.Json
    // deserialization (avoids primary-constructor vs parameterless-ctor ambiguity).

    private sealed class CompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = "";
        [JsonPropertyName("messages")] public IReadOnlyList<ChatRoleMessage> Messages { get; init; } = [];
        [JsonPropertyName("stream")] public bool Stream { get; init; }
    }

    // Phase 1 tool-calling request shape. tool_calls / tool_call_id on individual messages are
    // carried by ChatToolMessage (defined in ChatToolModels.cs). Phase 5: messages are mapped to
    // WireMessage (below) so a user turn carrying an image serializes as a multimodal content
    // ARRAY [{type:text},{type:image_url}] (vision models) instead of a plain string.
    private sealed class ToolsCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = "";
        [JsonPropertyName("messages")] public IReadOnlyList<WireMessage> Messages { get; init; } = [];
        [JsonPropertyName("tools")] public IReadOnlyList<ChatToolDefinition>? Tools { get; init; }
        [JsonPropertyName("tool_choice")] public string? ToolChoice { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
    }

    // Phase 5: the on-the-wire message shape. `content` is `object?` so it serializes as a JSON
    // STRING for plain text turns and as a JSON ARRAY of content parts for vision turns
    // (System.Text.Json serializes the runtime type of an `object` value). tool_calls/tool_call_id
    // are carried verbatim so the tool loop is unaffected.
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

    /// <summary>Maps the in-memory <see cref="ChatToolMessage"/> list to the egress wire shape. A
    /// message carrying <see cref="ChatToolMessage.ImageDataUrl"/> becomes a multimodal content
    /// array (text part + image part) for vision models; every other message serializes content as
    /// a plain string (or null for assistant tool-call turns). Used by the tool-calling completion
    /// paths; image-gen uses the same mapping (its messages carry no image, so they stay plain
    /// text).</summary>
    private static List<WireMessage> ToWire(IReadOnlyList<ChatToolMessage> messages)
    {
        var list = new List<WireMessage>(messages.Count);
        foreach (var m in messages)
        {
            object? content;
            if (!string.IsNullOrEmpty(m.ImageDataUrl))
            {
                content = new List<WireContentPart>
                {
                    new() { Type = "text", Text = m.Content ?? "" },
                    new() { Type = "image_url", ImageUrl = new WireImageUrl { Url = m.ImageDataUrl } }
                };
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

    /// <summary>
    /// Phase 5: NON-STREAMING completion for an image-generation model. Routes through the SAME
    /// pinned <c>chat/completions</c> endpoint as the text/vision paths — per the plan ("many
    /// OpenRouter image-capable models respond via the standard chat/completions endpoint with an
    /// image content part in the response, so check that first; only add a new endpoint if the
    /// standard one categorically doesn't work"). No tools are declared (image generation does not
    /// use the tool loop). The response <c>content</c> is parsed TOLERANTLY because it can arrive
    /// as either a plain string (text-only answer) or an ARRAY of content parts containing
    /// <c>image_url</c> entries (the generated image). It also accepts the OpenAI images-API shape
    /// (a root <c>data: [{url}|{b64_json}]</c> array) as a fallback. The returned <c>ImageSources</c>
    /// are verbatim strings — <c>data:</c> URLs, http(s) URLs, or base64 (already wrapped) — for the
    /// caller to materialize into bytes. Egress is still pinned to <see cref="CompletionUrl"/>.
    /// </summary>
    public async Task<ImageGenResult> GenerateImageAsync(
        string apiKey,
        string model,
        IReadOnlyList<ChatToolMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));
        if (messages.Count == 0)
            throw new ArgumentException("At least one message is required.", nameof(messages));

        var payload = new ToolsCompletionRequest
        {
            Model = model,
            Messages = ToWire(messages),
            Tools = null,
            ToolChoice = null,
            Stream = false
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, CompletionUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

        using var resp = await _httpClient.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenRouter image-gen completion failed: HTTP {Status} body={Body}", (int)resp.StatusCode, body);
            throw new OpenRouterHttpException((int)resp.StatusCode,
                $"OpenRouter completion failed (HTTP {(int)resp.StatusCode}).");
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch { throw new InvalidOperationException("OpenRouter returned a malformed (non-JSON) response body."); }

        var resolvedModel = doc.RootElement.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String
            ? mEl.GetString()!
            : model;

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

        return new ImageGenResult(
            Text: textBuilder.Length == 0 ? null : textBuilder.ToString(),
            ImageSources: imageSources,
            Model: resolvedModel);
    }

    /// <summary>Reads a <c>content</c> JSON value that is either a plain string or an array of
    /// content parts, appending text to <paramref name="text"/> and any image references to
    /// <paramref name="images"/>. Recognized image part shapes: <c>{type:image_url, image_url:{url}}</c>
    /// and (some providers) <c>{type:image, image:{url}}</c>.</summary>
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
        // Present on assistant turns that requested tool calls (Phase 1 tool loop).
        [JsonPropertyName("tool_calls")] public List<ChoiceToolCall>? ToolCalls { get; set; }
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
    }

    // Phase 2 streaming chunk shapes (OpenAI-compatible SSE `data:` payload). Property names are
    // snake_case to match the wire format exactly.
    private sealed class StreamChunk
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("choices")] public List<StreamChoice>? Choices { get; set; }
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
/// Thrown when OpenRouter returns a non-success HTTP status for a completion request (non-streaming
/// or streaming). Carries the <see cref="StatusCode"/> so Phase 4 multi-key failover
/// (<c>ChatEndpoints</c>) can classify it (401→disable, 402/429→cooldown, 5xx→retry-next) without
/// parsing the message string.
///
/// <para>Deliberately derives from <see cref="InvalidOperationException"/>: every existing completion
/// call-site catches <c>InvalidOperationException</c>, so this keeps that behaviour intact, while new
/// Phase 4 code catches the more specific type FIRST to inspect <see cref="StatusCode"/> and drive
/// failover. Transport errors (timeout, refused, DNS) surface as <see cref="HttpRequestException"/>
/// from <c>HttpClient.SendAsync</c> and are NOT wrapped here — the failover helper catches those
/// separately (retry-next, no cooldown).</para>
/// </summary>
public sealed class OpenRouterHttpException : InvalidOperationException
{
    /// <summary>The upstream HTTP status code (e.g. 401, 402, 429, 500).</summary>
    public int StatusCode { get; }

    public OpenRouterHttpException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Phase 5: the parsed result of an image-generation completion. <see cref="ImageSources"/> are
/// verbatim strings exactly as OpenRouter returned them — <c>data:</c> URLs, http(s) URLs, or
/// already-wrapped base64. The chat endpoint materializes each into bytes (decoding data: URLs
/// directly, fetching http(s) URLs server-side) so the image renders inline through the CSP-allowed
/// <c>/api/chat/attachments/{id}</c> route and persists with the conversation. <see cref="Text"/>
/// carries any caption/text the model emitted alongside the image(s).</summary>
public sealed record ImageGenResult(string? Text, List<string> ImageSources, string Model);
