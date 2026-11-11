using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BeeMemoryBank.Api.Models;

namespace BeeMemoryBank.Api.Services.Providers;

/// <summary>
/// The client of Google's own Gemini format (<see cref="ProviderProtocols.Gemini"/>): <c>POST {base}/models/{model}:generateContent</c> and
/// <c>:streamGenerateContent?alt=sse</c>, <c>GET {base}/models</c>, base <c>https://generativelanguage.googleapis.com/v1beta</c>. The key goes
/// in the <c>x-goog-api-key</c> header and never in the address (<c>?key=</c> would put it into every log that records URLs). Written by
/// hand like <see cref="OpenAiChatClient"/>; every request leaves through <see cref="ProviderEgress"/>.
///
/// <list type="bullet">
/// <item><b>Mapping.</b> The leading system messages become <c>systemInstruction</c>; a later system message stays where it is, as text of
/// its user turn. Roles <c>user</c> / <c>model</c>, turns of one role merged; text, <c>inlineData</c> images from data URLs,
/// <c>functionCall</c> and <c>functionResponse</c> parts. Gemini may give a call no id: the client makes one (<see cref="SyntheticIdPrefix"/>),
/// which stays with the call in the chat's transcript and is never sent to Gemini; a result is matched to its call's name. A call left
/// without a result gets a "not run" result, because Gemini wants as many responses as calls.</item>
/// <item><b>Tools.</b> <c>tools: [{functionDeclarations}]</c> with each tool's JSON schema cut down to the subset Gemini accepts
/// (<see cref="GeminiSchema"/>).</item>
/// <item><b>Thought signatures.</b> Gemini 3 models sign their parts (<c>thoughtSignature</c>, notably on a <c>functionCall</c>) and refuse
/// the next request of a tool loop if a signature does not come back on the same part. The parts of a signed answer are kept as
/// <see cref="ToolCompletionResult.ProviderState"/> and sent back verbatim - only to the same provider and model. A call of the current
/// round that has no signature of this model (the chain moved here from another model) is marked with <see cref="SkipSignature"/>, the
/// value Google documents for history that another model wrote.</item>
/// <item><b>Generation.</b> The chat sends no <c>generationConfig</c>. Dream sends <c>maxOutputTokens</c>, <c>temperature</c> and, when the
/// provider's reasoning style is <see cref="ReasoningStyles.Gemini"/> and the model is Gemini 3 or later, <c>thinkingConfig.thinkingLevel</c>.</item>
/// <item><b>Answers.</b> Usage: <c>promptTokenCount</c> as the prompt, <c>candidatesTokenCount + thoughtsTokenCount</c> as the completion.
/// <c>MAX_TOKENS</c> is the chat's "length". <c>SAFETY</c>, <c>RECITATION</c>, <c>BLOCKLIST</c>, <c>PROHIBITED_CONTENT</c> ... and a prompt
/// blocked by <c>promptFeedback.blockReason</c> are a <see cref="ProviderRefusalException"/> for the chat (a clear message, no retry);
/// <c>MALFORMED_FUNCTION_CALL</c> is an answer that cannot be used.</item>
/// <item><b>Errors.</b> An HTTP status is a <see cref="ProviderHttpException"/> (<c>RESOURCE_EXHAUSTED</c> arrives as 429, which pauses the
/// key; an invalid key arrives as 400 <c>API_KEY_INVALID</c> and is treated as 401, which disables it); an error inside the stream is one
/// too. The exception and the log name the provider, the status and Google's status word (the log also the request id), never the body.</item>
/// </list>
/// No image generation through this protocol in this version.
/// </summary>
public sealed partial class GeminiClient : IModelProtocolClient
{
    /// <summary>The thought signature Google documents for a function call that no Gemini 3 model of this conversation signed (history written
    /// by another model): it tells the API to skip the signature check for that part.</summary>
    public const string SkipSignature = "skip_thought_signature_validator";

    /// <summary>The prefix of a call id this client made up because Gemini gave none; such an id is never sent to Gemini.</summary>
    public const string SyntheticIdPrefix = "bmb_call_";

    private static readonly JsonSerializerOptions WriteOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ProviderEgress _egress;
    private readonly ILogger<GeminiClient> _logger;

    public GeminiClient(ProviderEgress egress, ILogger<GeminiClient> logger)
    {
        _egress = egress;
        _logger = logger;
    }

    public string Protocol => ProviderProtocols.Gemini;

    public bool SupportsImageGeneration(ProviderWireOptions options) => false;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex ModelShape();

    [GeneratedRegex("^gemini-(\\d+)(?:[.-]|$)")]
    private static partial Regex Version();

    /// <summary>The model id as it goes into the path (<c>models/</c> left off); anything that is not a plain model name is refused before a
    /// request is made.</summary>
    internal static string ModelName(ModelCall call)
    {
        var model = call.Model.StartsWith("models/", StringComparison.Ordinal) ? call.Model["models/".Length..] : call.Model;
        return ModelShape().IsMatch(model)
            ? model
            : throw new ProviderUnavailableException($"'{call.Model}' is not a Gemini model name.");
    }

    /// <summary>The major version of a <c>gemini-N...</c> model, or null for any other name.</summary>
    internal static int? MajorVersion(string model)
    {
        var m = Version().Match(model.StartsWith("models/", StringComparison.Ordinal) ? model["models/".Length..] : model);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

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
            req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
    }

    private static HttpRequestMessage Post(ModelCall call, string method, JsonObject payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, call.Target.Endpoint($"models/{ModelName(call)}:{method}"));
        Authorize(req, call.ApiKey);
        req.Content = new StringContent(payload.ToJsonString(WriteOpts), Encoding.UTF8, "application/json");
        return req;
    }

    /// <summary>The status a Google error stands for: its HTTP status, except an invalid key (400 <c>API_KEY_INVALID</c>), which is the key's
    /// fault and counts as 401.</summary>
    internal static int StatusOf(int httpStatus, JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array
            && details.EnumerateArray().Any(d => ProviderWire.Str(d, "reason") == "API_KEY_INVALID"))
            return 401;
        return httpStatus;
    }

    /// <summary>The HTTP status of a Google status word (an error inside a stream carries both; the number wins when it is one).</summary>
    internal static int StatusOfWord(string? status) => status switch
    {
        "INVALID_ARGUMENT" or "FAILED_PRECONDITION" or "OUT_OF_RANGE" => 400,
        "UNAUTHENTICATED" => 401,
        "PERMISSION_DENIED" => 403,
        "NOT_FOUND" => 404,
        "RESOURCE_EXHAUSTED" => 429,
        "UNAVAILABLE" => 503,
        "DEADLINE_EXCEEDED" => 504,
        _ => 500,
    };

    /// <summary>A failed request: logged with the status, Google's status word and the request id only - never the body, which can quote
    /// what was sent.</summary>
    private ProviderHttpException Failure(ModelCall call, HttpResponseMessage resp, string? body, string what)
    {
        var httpStatus = (int)resp.StatusCode;
        var status = httpStatus;
        string? word = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                status = StatusOf(httpStatus, doc.RootElement);
                word = ProviderWire.ErrorCode(doc.RootElement, "status");
            }
        }
        catch (JsonException) { /* not JSON: the status alone */ }
        _logger.LogWarning("{Provider} {What} failed: HTTP {Status} {Word} request-id={RequestId}", call.Target.Name, what, httpStatus, word,
            ProviderWire.RequestId(resp));
        return new ProviderHttpException(status, word is null
            ? $"{call.Target.Name} completion failed (HTTP {httpStatus})."
            : $"{call.Target.Name} completion failed (HTTP {httpStatus}, {word}).");
    }

    // ── requests ─────────────────────────────────────────────────────────────────

    public async Task<ChatCompleteResponse> CompleteAsync(ModelCall call, IReadOnlyList<ChatRoleMessage> messages, CompletionOptions options,
        CancellationToken ct)
    {
        Validate(call, messages.Count);
        var payload = BuildRequest(call, messages.Select(m => new ChatToolMessage { Role = m.Role, Content = m.Content }).ToList(), null,
            options.MaxTokens, options.Temperature, options.ReasoningEffort);

        using var req = Post(call, "generateContent", payload);
        using var resp = await _egress.SendAsync(call.Target, req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw Failure(call, resp, body, "completion");

        var turn = ParseResponse(call, body);
        return new ChatCompleteResponse(turn.Text, turn.PromptTokens ?? 0, turn.CompletionTokens ?? 0, turn.Model)
        {
            FinishReason = turn.BlockReason is not null ? "content_filter" : FinishReason(turn.FinishReason),
            HasReasoning = turn.Reasoned,
        };
    }

    public async Task<ToolCompletionResult> CompleteWithToolsAsync(ModelCall call, IReadOnlyList<ChatToolMessage> messages,
        IReadOnlyList<ChatToolDefinition>? tools, CancellationToken ct)
    {
        Validate(call, messages.Count);
        var payload = BuildRequest(call, messages, tools, null, null, null);

        using var req = Post(call, "generateContent", payload);
        using var resp = await _egress.SendAsync(call.Target, req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw Failure(call, resp, body, "tool completion");

        var turn = ParseResponse(call, body);
        ThrowIfUnusable(call, turn.BlockReason, turn.FinishReason);
        return turn.ToResult();
    }

    /// <summary>The streamed completion of a chat round: each chunk is a whole <c>GenerateContentResponse</c> with the next parts; text goes
    /// to <paramref name="onTextDelta"/> as it arrives, calls arrive whole. A stream that ends without a finish reason broke off.</summary>
    public async Task<ToolCompletionResult> StreamWithToolsAsync(ModelCall call, IReadOnlyList<ChatToolMessage> messages,
        IReadOnlyList<ChatToolDefinition>? tools, Func<string, CancellationToken, ValueTask> onTextDelta, CancellationToken ct)
    {
        Validate(call, messages.Count);
        var payload = BuildRequest(call, messages, tools, null, null, null);

        using var req = Post(call, "streamGenerateContent?alt=sse", payload);
        using var resp = await _egress.SendAsync(call.Target, req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw Failure(call, resp, body, "streaming completion");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var answer = new Answer(call);
        await foreach (var data in ProviderWire.ReadEventDataAsync(stream, ct))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    var word = ProviderWire.ErrorCode(root, "status");
                    var status = ProviderWire.Int(error, "code") is int code and >= 400 and <= 599 ? code : StatusOfWord(word);
                    _logger.LogWarning("{Provider} streaming completion: error {Status} {Word} inside the stream", call.Target.Name, status, word);
                    throw new ProviderHttpException(StatusOf(status, root), $"{call.Target.Name} completion failed mid-stream ({word ?? status.ToString()}).");
                }
                foreach (var fragment in answer.Read(root))
                    await onTextDelta(fragment, ct);
            }
            if (answer.BlockReason is not null) break;
        }

        if (answer.BlockReason is null && answer.FinishReason is null)
            throw new IOException($"The answer of {call.Target.Name} ended before it was complete.");
        ThrowIfUnusable(call, answer.BlockReason, answer.FinishReason);
        return answer.ToTurn().ToResult();
    }

    public Task<ImageGenResult> GenerateImageAsync(ModelCall call, IReadOnlyList<ChatToolMessage> messages, CancellationToken ct) =>
        throw new ProviderUnavailableException($"Image generation is not available through provider '{call.Target.Name}'.");

    /// <summary>The provider's own model list, <c>GET {base}/models</c>: the models that support <c>generateContent</c>, their id without
    /// <c>models/</c>, display name and input limit, page by page through <c>nextPageToken</c> (at most ten pages of a thousand).</summary>
    public async Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(ProviderEgressTarget target, string apiKey, CancellationToken ct)
    {
        var list = new List<ProviderModelInfo>();
        string? token = null;
        for (var page = 0; page < 10; page++)
        {
            var relative = "models?pageSize=1000" + (token is null ? "" : "&pageToken=" + Uri.EscapeDataString(token));
            using var req = new HttpRequestMessage(HttpMethod.Get, target.Endpoint(relative));
            Authorize(req, apiKey);
            using var resp = await _egress.SendAsync(target, req, HttpCompletionOption.ResponseContentRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                try { using var e = JsonDocument.Parse(body); status = StatusOf(status, e.RootElement); }
                catch (JsonException) { /* the status alone */ }
                _logger.LogWarning("{Provider} model list failed: HTTP {Status} request-id={RequestId}", target.Name, (int)resp.StatusCode,
                    ProviderWire.RequestId(resp));
                throw new ProviderHttpException(status, $"{target.Name} model list failed (HTTP {(int)resp.StatusCode}).");
            }
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) break;
                foreach (var item in models.EnumerateArray())
                {
                    if (!item.TryGetProperty("supportedGenerationMethods", out var methods) || methods.ValueKind != JsonValueKind.Array
                        || !methods.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "generateContent"))
                        continue;
                    var id = ProviderWire.Str(item, "name");
                    if (id is null) continue;
                    if (id.StartsWith("models/", StringComparison.Ordinal)) id = id["models/".Length..];
                    if (!ModelShape().IsMatch(id)) continue;
                    var name = ProviderWire.Str(item, "displayName");
                    list.Add(new ProviderModelInfo(id, name is { Length: <= 200 } ? name : null,
                        ProviderWire.Int(item, "inputTokenLimit") is int w && w > 0 ? w : null));
                    if (list.Count >= 1000) return list;
                }
                token = ProviderWire.Str(root, "nextPageToken");
                if (string.IsNullOrEmpty(token)) break;
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
        int? maxTokens, double? temperature, string? effort)
    {
        var (system, contents) = MapMessages(call, messages);
        var payload = new JsonObject();
        if (system.Count > 0) payload["systemInstruction"] = new JsonObject { ["parts"] = system };
        payload["contents"] = contents;
        if (tools is { Count: > 0 })
        {
            var declarations = new JsonArray();
            foreach (var t in tools)
            {
                var declaration = new JsonObject { ["name"] = t.Function.Name, ["description"] = t.Function.Description };
                if (GeminiSchema.Convert(t.Function.Parameters) is { } parameters) declaration["parameters"] = parameters;
                declarations.Add(declaration);
            }
            payload["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = declarations } };
        }

        var generation = new JsonObject();
        if (maxTokens is > 0) generation["maxOutputTokens"] = maxTokens.Value;
        if (temperature is { } t2) generation["temperature"] = t2;
        if (!string.IsNullOrWhiteSpace(effort) && call.Target.Options.Reasoning == ReasoningStyles.Gemini && MajorVersion(call.Model) >= 3)
            generation["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = effort };
        if (generation.Count > 0) payload["generationConfig"] = generation;
        return payload;
    }

    private static JsonObject TextPart(string text) => new() { ["text"] = text };

    /// <summary>Whether this id was made up by this client (and so is not Gemini's to see).</summary>
    internal static bool IsSynthetic(string? id) => id is null || id.Length == 0 || id.StartsWith(SyntheticIdPrefix, StringComparison.Ordinal);

    private static string NewCallId() => SyntheticIdPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    private (JsonArray System, JsonArray Contents) MapMessages(ModelCall call, IReadOnlyList<ChatToolMessage> messages)
    {
        var system = new JsonArray();
        var turns = new List<(string Role, JsonArray Parts)>();
        var open = new List<(string Id, string Name)>();
        var waiting = new List<string>();
        var leading = true;
        var lastUser = -1;
        for (var i = 0; i < messages.Count; i++)
            if (messages[i].Role == "user") lastUser = i;
        var signsCalls = MajorVersion(call.Model) >= 3;

        void Add(string role, JsonNode part)
        {
            if (turns.Count > 0 && turns[^1].Role == role) turns[^1].Parts.Add(part);
            else turns.Add((role, new JsonArray { part }));
        }

        JsonObject Response(string id, string name, JsonNode response)
        {
            var r = new JsonObject { ["name"] = name, ["response"] = response };
            if (!IsSynthetic(id)) r["id"] = id;
            return new JsonObject { ["functionResponse"] = r };
        }

        void CloseOpen()
        {
            foreach (var (id, name) in open)
                Add("user", Response(id, name, new JsonObject { ["error"] = "This tool call was not run." }));
            open.Clear();
            foreach (var note in waiting) Add("user", TextPart(note));
            waiting.Clear();
        }

        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            switch (m.Role)
            {
                case "system":
                    if (string.IsNullOrWhiteSpace(m.Content)) break;
                    if (leading) system.Add(TextPart(m.Content));
                    else if (open.Count > 0) waiting.Add(m.Content);
                    else Add("user", TextPart(m.Content));
                    break;

                case "tool":
                {
                    leading = false;
                    var at = open.FindIndex(o => o.Id == m.ToolCallId);
                    if (at >= 0)
                    {
                        var (id, name) = open[at];
                        open.RemoveAt(at);
                        Add("user", Response(id, name, ToolResponse(m.Content)));
                    }
                    else if (open.Count > 0)
                        waiting.Add("[Result of an earlier tool call]\n" + m.Content);
                    else
                        Add("user", TextPart("[Result of an earlier tool call]\n" + m.Content));
                    break;
                }

                case "assistant":
                {
                    leading = false;
                    CloseOpen();
                    foreach (var part in ModelParts(call, m, currentRound: i > lastUser && signsCalls))
                        Add("model", part);
                    foreach (var tc in m.ToolCalls ?? [])
                        if (!string.IsNullOrEmpty(tc.Function?.Name)) open.Add((tc.Id, tc.Function.Name));
                    break;
                }

                default:
                {
                    leading = false;
                    CloseOpen();
                    if (!string.IsNullOrWhiteSpace(m.Content)) Add("user", TextPart(m.Content));
                    foreach (var url in m.ImageDataUrls ?? [])
                        if (ProviderWire.TryParseDataUrl(url, out var mimeType, out var data))
                            Add("user", new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = mimeType, ["data"] = data } });
                    break;
                }
            }
        }
        CloseOpen();

        if (turns.Count == 0)
            throw new ArgumentException("At least one message besides the system prompt is required.", nameof(messages));
        if (turns[0].Role != "user")
            turns.Insert(0, ("user", new JsonArray { TextPart("(The conversation continues.)") }));

        var contents = new JsonArray();
        foreach (var (role, parts) in turns)
            contents.Add(new JsonObject { ["role"] = role, ["parts"] = parts });
        return (system, contents);
    }

    /// <summary>A model turn's parts: what this model wrote, verbatim with its signatures, when the turn is this provider's and this model's
    /// own; otherwise rebuilt from the text and tool calls (the first call of the current round marked with <see cref="SkipSignature"/> for
    /// a model that signs its calls).</summary>
    private IEnumerable<JsonNode> ModelParts(ModelCall call, ChatToolMessage m, bool currentRound)
    {
        if (m.ProviderState is { } state && state.IsFor(Protocol, call.Target.ProviderId, call.Model)
            && JsonNode.Parse(state.Content.GetRawText()) is JsonArray own && own.Count > 0)
        {
            foreach (var part in own)
                if (part is not null) yield return part.DeepClone();
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(m.Content)) yield return TextPart(m.Content);
        var first = true;
        foreach (var tc in m.ToolCalls ?? [])
        {
            if (string.IsNullOrEmpty(tc.Function?.Name)) continue;
            var fc = new JsonObject { ["name"] = tc.Function.Name, ["args"] = ProviderWire.ArgumentsObject(tc.Function.Arguments) };
            if (!IsSynthetic(tc.Id)) fc["id"] = tc.Id;
            var part = new JsonObject { ["functionCall"] = fc };
            if (currentRound && first) part["thoughtSignature"] = SkipSignature;
            first = false;
            yield return part;
        }
    }

    /// <summary>A tool result as Gemini's <c>response</c> object: a JSON object as it is (an <c>{"error": ...}</c> result is Google's own
    /// shape for a failure), anything else under <c>output</c>.</summary>
    private static JsonNode ToolResponse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new JsonObject { ["output"] = "" };
        try
        {
            var node = JsonNode.Parse(content);
            return node as JsonObject ?? new JsonObject { ["output"] = node };
        }
        catch (JsonException)
        {
            return new JsonObject { ["output"] = content };
        }
    }

    // ── response mapping ──────────────────────────────────────────────────────

    private sealed record Turn(string Text, List<ResolvedToolCall> ToolCalls, string Model, int? PromptTokens, int? CompletionTokens,
        string? FinishReason, string? BlockReason, bool Reasoned, ProviderTurnState? State)
    {
        public ToolCompletionResult ToResult() =>
            new(Text, ToolCalls.Count == 0 ? null : ToolCalls, Model, PromptTokens, CompletionTokens, State);
    }

    /// <summary>The parts of one answer, whole or chunk by chunk: text fragments joined into the part they belong to (a fragment that
    /// carries a signature closes its part), calls kept whole with their signatures, the usage and finish reason of the last chunk that has
    /// them.</summary>
    private sealed class Answer(ModelCall call)
    {
        private readonly JsonArray _parts = [];
        private readonly StringBuilder _text = new();
        private readonly List<ResolvedToolCall> _calls = [];
        private string? _model;
        private int? _prompt, _candidates, _thoughts;
        private bool _signed, _thought;

        public string? FinishReason { get; private set; }
        public string? BlockReason { get; private set; }

        /// <summary>Reads one response (or chunk); returns the new answer text it carried, fragment by fragment.</summary>
        public List<string> Read(JsonElement root)
        {
            var fragments = new List<string>();
            _model = ProviderWire.Str(root, "modelVersion") ?? _model;
            if (root.TryGetProperty("promptFeedback", out var feedback) && ProviderWire.Str(feedback, "blockReason") is { } block)
                BlockReason = block;
            if (root.TryGetProperty("usageMetadata", out var usage))
            {
                _prompt = ProviderWire.Int(usage, "promptTokenCount") ?? _prompt;
                _candidates = ProviderWire.Int(usage, "candidatesTokenCount") ?? _candidates;
                _thoughts = ProviderWire.Int(usage, "thoughtsTokenCount") ?? _thoughts;
            }
            if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() == 0)
                return fragments;
            var candidate = candidates[0];
            FinishReason = ProviderWire.Str(candidate, "finishReason") ?? FinishReason;
            if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts)
                || parts.ValueKind != JsonValueKind.Array)
                return fragments;

            foreach (var part in parts.EnumerateArray())
            {
                if (JsonNode.Parse(part.GetRawText()) is not JsonObject node) continue;
                var thought = part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True;
                var signature = ProviderWire.Str(part, "thoughtSignature");
                if (signature is not null) _signed = true;
                if (thought) _thought = true;

                if (part.TryGetProperty("functionCall", out var fc) && ProviderWire.Str(fc, "name") is { Length: > 0 } name)
                {
                    var id = ProviderWire.Str(fc, "id");
                    _calls.Add(new ResolvedToolCall(string.IsNullOrEmpty(id) ? NewCallId() : id, name,
                        fc.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object ? args.GetRawText() : "{}"));
                    _parts.Add(node);
                    continue;
                }

                if (IsPlainText(node))
                {
                    var text = (string?)node["text"] ?? "";
                    if (!thought && text.Length > 0)
                    {
                        _text.Append(text);
                        fragments.Add(text);
                    }
                    // A fragment of the part before it: joined to it, unless that part is already closed by a signature.
                    if (_parts.Count > 0 && _parts[^1] is JsonObject last && IsPlainText(last) && !last.ContainsKey("thoughtSignature")
                        && ((bool?)last["thought"] ?? false) == thought)
                    {
                        last["text"] = (string?)last["text"] + text;
                        if (signature is not null) last["thoughtSignature"] = signature;
                        continue;
                    }
                }
                _parts.Add(node);
            }
            return fragments;
        }

        /// <summary>A part of text only (a thought or not, signed or not).</summary>
        private static bool IsPlainText(JsonObject part) =>
            part["text"] is JsonValue && part.All(kv => kv.Key is "text" or "thought" or "thoughtSignature");

        public Turn ToTurn() => new(_text.ToString(), _calls, _model ?? call.Model, _prompt,
            _candidates is null && _thoughts is null ? null : (_candidates ?? 0) + (_thoughts ?? 0),
            FinishReason, BlockReason, _thought || _thoughts > 0,
            _signed ? new ProviderTurnState(ProviderProtocols.Gemini, call.Target.ProviderId, call.Model, JsonSerializer.SerializeToElement(_parts)) : null);
    }

    private Turn ParseResponse(ModelCall call, string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { throw new InvalidOperationException($"{call.Target.Name} returned a malformed (non-JSON) response body."); }
        using (doc)
        {
            var root = doc.RootElement;
            var answer = new Answer(call);
            answer.Read(root);
            if (answer.BlockReason is null && !(root.TryGetProperty("candidates", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0))
                throw new InvalidOperationException($"{call.Target.Name} returned no candidates.");
            return answer.ToTurn();
        }
    }

    /// <summary>The chat's finish reason for Gemini's.</summary>
    internal static string? FinishReason(string? reason) => reason switch
    {
        null => null,
        "STOP" => "stop",
        "MAX_TOKENS" => "length",
        "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY" or "IMAGE_PROHIBITED_CONTENT" or "LANGUAGE"
            => "content_filter",
        "MALFORMED_FUNCTION_CALL" or "UNEXPECTED_TOOL_CALL" or "TOO_MANY_TOOL_CALLS" => "malformed_function_call",
        _ => reason.ToLowerInvariant(),
    };

    /// <summary>A blocked prompt, a blocked answer or a call Gemini could not form: said plainly to the person, never retried.</summary>
    private static void ThrowIfUnusable(ModelCall call, string? blockReason, string? finishReason)
    {
        if (blockReason is not null)
            throw new ProviderRefusalException(
                $"{call.Target.Name} blocked this request ({Word(blockReason)}). Rephrase it, or ask another model.");
        switch (FinishReason(finishReason))
        {
            case "content_filter":
                throw new ProviderRefusalException(
                    $"{call.Target.Name} blocked the answer ({Word(finishReason!)}). Rephrase the request, or ask another model.");
            case "malformed_function_call":
                throw new InvalidOperationException(
                    $"The model at {call.Target.Name} produced a tool call that could not be read ({Word(finishReason!)}). Try again, or ask another model.");
        }
    }

    [GeneratedRegex("^[A-Z_]{1,64}$")]
    private static partial Regex WordShape();

    private static string Word(string reason) => WordShape().IsMatch(reason) ? reason : "blocked";
}

/// <summary>
/// The JSON schema of a chat tool, cut down to what Gemini's <c>parameters</c> (an OpenAPI 3.0 schema subset) accepts: <c>type</c>,
/// <c>format</c> (only the ones Gemini knows for the type; any other is said in the description), <c>title</c>, <c>description</c>,
/// <c>nullable</c>, <c>enum</c> (strings), <c>properties</c>, <c>required</c> (only properties that exist), <c>items</c>, <c>anyOf</c>,
/// the length, count and range limits, <c>pattern</c>, <c>example</c>, <c>propertyOrdering</c>. Left out: <c>additionalProperties</c>,
/// <c>$schema</c>, <c>$ref</c>, <c>$defs</c>, <c>default</c>, <c>allOf</c>, <c>not</c> and everything else. <c>["string", "null"]</c> is
/// <c>string</c> + <c>nullable</c>, <c>const</c> a one-value <c>enum</c>, <c>oneOf</c> an <c>anyOf</c>. An object without properties has no
/// <c>parameters</c> at all (Gemini refuses an empty object).
/// </summary>
internal static class GeminiSchema
{
    private static readonly string[] Copied =
    [
        "title", "description", "nullable", "maxItems", "minItems", "minProperties", "maxProperties", "minLength", "maxLength", "pattern",
        "example", "minimum", "maximum", "propertyOrdering",
    ];

    private static readonly Dictionary<string, string[]> Formats = new(StringComparer.Ordinal)
    {
        ["string"] = ["enum", "date-time"],
        ["number"] = ["float", "double"],
        ["integer"] = ["int32", "int64"],
    };

    /// <summary>The <c>parameters</c> of a function declaration, or null when the tool takes none.</summary>
    public static JsonObject? Convert(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return null;
        var converted = ConvertNode(schema);
        return converted["properties"] is JsonObject { Count: > 0 } ? converted : null;
    }

    private static JsonObject ConvertNode(JsonElement s)
    {
        var o = new JsonObject();
        if (s.ValueKind != JsonValueKind.Object) return o;

        string? type = null;
        if (s.TryGetProperty("type", out var t))
        {
            if (t.ValueKind == JsonValueKind.String) type = t.GetString();
            else if (t.ValueKind == JsonValueKind.Array)
            {
                var types = t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
                type = types.FirstOrDefault(x => x != "null");
                if (types.Contains("null")) o["nullable"] = true;
            }
        }
        if (type is not null) o["type"] = type;

        foreach (var key in Copied)
            if (s.TryGetProperty(key, out var v) && !o.ContainsKey(key))
                o[key] = JsonNode.Parse(v.GetRawText());

        if (s.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() is { } format)
        {
            if (type is not null && Formats.TryGetValue(type, out var allowed) && allowed.Contains(format)) o["format"] = format;
            else o["description"] = ((string?)o["description"] is { Length: > 0 } d ? d + " " : "") + $"(format: {format})";
        }

        List<JsonElement>? values = null;
        if (s.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array) values = e.EnumerateArray().ToList();
        else if (s.TryGetProperty("const", out var c)) values = [c];
        if (values is { Count: > 0 } && values.All(x => x.ValueKind == JsonValueKind.String))
        {
            o["enum"] = new JsonArray(values.Select(x => (JsonNode?)JsonValue.Create(x.GetString())).ToArray());
            o["type"] ??= "string";
        }

        if (s.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            var converted = new JsonObject();
            foreach (var p in props.EnumerateObject()) converted[p.Name] = ConvertNode(p.Value);
            o["properties"] = converted;
            if (s.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            {
                var names = req.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
                    .Where(converted.ContainsKey).Distinct().ToList();
                if (names.Count > 0) o["required"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            }
        }

        if (s.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object) o["items"] = ConvertNode(items);

        var any = s.TryGetProperty("anyOf", out var a) && a.ValueKind == JsonValueKind.Array ? a
            : s.TryGetProperty("oneOf", out var one) && one.ValueKind == JsonValueKind.Array ? one : default;
        if (any.ValueKind == JsonValueKind.Array)
            o["anyOf"] = new JsonArray(any.EnumerateArray().Select(x => (JsonNode?)ConvertNode(x)).ToArray());

        return o;
    }
}
