using System.Text.Json.Serialization;

namespace BeeMemoryBank.Api.Models;

// ── OpenAI-format tool-calling wire models ─────────────────────────────
// These extend the flat ChatRoleMessage (role+content) with the structured
// fields needed for a non-streaming tool-call loop: assistant `tool_calls`,
// tool-role `tool_call_id`, and the `tools` array + tool definitions. They are
// deliberately separate types so the plain /complete path (which uses
// ChatRoleMessage) is untouched.

/// <summary>A single message in a tool-aware completion conversation.</summary>
public sealed class ChatToolMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")] public string? Content { get; set; }
    /// <summary>Present only on assistant messages that requested tool calls.</summary>
    [JsonPropertyName("tool_calls")] public List<ChatToolCall>? ToolCalls { get; set; }
    /// <summary>Present only on role="tool" messages, echoing the call this answers.</summary>
    [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }

    /// <summary>Vision: an in-memory-only carrier for the image(s) attached to this
    /// message. When set, a protocol client (<see cref="BeeMemoryBank.Api.Services.Providers.OpenAiChatClient"/>) builds a
    /// multimodal egress content array <c>[{type:text},{type:image_url},...]</c> instead of a plain
    /// string. It is deliberately <c>[JsonIgnore]</c> so it NEVER persists (the bytes live in
    /// chat_attachment keyed by message_id) and never leaks onto the wire as a top-level field —
    /// the egress client reads it explicitly via its wire-message mapping. Only used transiently for
    /// the turn it belongs to.</summary>
    [JsonIgnore] public List<string>? ImageDataUrls { get; set; }

    /// <summary>Provider-opaque content of an assistant turn that asked for tools (Anthropic's thinking blocks with their signatures,
    /// Gemini's thought signatures): what the protocol client returned in <see cref="ToolCompletionResult.ProviderState"/>, handed back to
    /// the same client on the next round of the tool loop (and after <c>/confirm</c>, from <c>chat_message</c>, where it is sealed like the
    /// tool calls). <c>[JsonIgnore]</c>: never on any wire as a field of its own; only the client of the protocol, provider and model that
    /// made it puts it back into a request, every other client ignores it.</summary>
    [JsonIgnore] public ProviderTurnState? ProviderState { get; set; }
}

/// <summary>
/// What a protocol client needs to send an assistant turn back exactly as its model produced it, beyond the text and the tool calls every
/// protocol shares: Anthropic's <c>thinking</c> / <c>redacted_thinking</c> blocks (with their signatures), Gemini's parts with their
/// <c>thoughtSignature</c>. <see cref="Content"/> is the provider's own JSON for the turn's content (Anthropic: the <c>content</c> block array;
/// Gemini: the <c>parts</c> array). It is valid only for the model that wrote it: a client uses it only when <see cref="Protocol"/>,
/// <see cref="ProviderId"/> and <see cref="Model"/> are the call's own, so a fallback to another model (or provider) drops it and the turn
/// is rebuilt from its text and tool calls. Never shown to the user.
/// </summary>
public sealed record ProviderTurnState(
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("providerId")] Guid ProviderId,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("content")] System.Text.Json.JsonElement Content)
{
    /// <summary>Whether this state was written by <paramref name="protocol"/> for the same provider and model as <paramref name="providerId"/>
    /// and <paramref name="model"/>.</summary>
    public bool IsFor(string protocol, Guid providerId, string model) =>
        Protocol == protocol && ProviderId == providerId && string.Equals(Model, model, StringComparison.Ordinal);

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);

    /// <summary>The state stored with a message, or null when there is none or it does not read (an unreadable state is dropped: the turn
    /// is then rebuilt from its text and tool calls, as for another model).</summary>
    public static ProviderTurnState? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var state = System.Text.Json.JsonSerializer.Deserialize<ProviderTurnState>(json);
            return state is { Protocol.Length: > 0, Model.Length: > 0 } && state.Content.ValueKind == System.Text.Json.JsonValueKind.Array
                ? state with { Content = state.Content.Clone() }
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>An assistant-issued tool call (OpenAI "function" call shape).</summary>
public sealed class ChatToolCall
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public ChatToolCallFunction Function { get; set; } = new();
}

public sealed class ChatToolCallFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>JSON-encoded argument object, as a STRING (OpenAI convention).</summary>
    [JsonPropertyName("arguments")] public string Arguments { get; set; } = "{}";
}

/// <summary>A tool/function declared to the model (the `tools` request array).</summary>
public sealed class ChatToolDefinition
{
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public ChatToolFunction Function { get; set; } = new();
}

public sealed class ChatToolFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    /// <summary>JSON Schema for the function parameters.</summary>
    [JsonPropertyName("parameters")] public System.Text.Json.JsonElement Parameters { get; set; }
}

/// <summary>The assistant turn returned by a tool-aware completion. ToolCalls is null
/// when the model produced a plain text answer (loop terminator). PromptTokens /
/// CompletionTokens are nullable: the streaming path requests usage via
/// stream_options.include_usage, but a provider/route can still omit it (fields stay null,
/// never crash). Trailing optional params keep existing construction sites compiling.
/// <see cref="ProviderState"/> is the provider-opaque content of the turn the next round must send back (see
/// <see cref="ProviderTurnState"/>); null for the OpenAI format and whenever there is none.</summary>
public sealed record ToolCompletionResult(
    string? Content,
    List<ResolvedToolCall>? ToolCalls,
    string Model,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    ProviderTurnState? ProviderState = null);

/// <summary>A tool call resolved from the model response (arguments kept as the raw
/// JSON string the model emitted, plus a parsed view for dispatch).</summary>
public sealed record ResolvedToolCall(string Id, string Name, string ArgumentsJson);

// ── POST /api/chat/message request/response ──────────────────────────────────

/// <summary>Conversation-less, ephemeral turn request. The client sends the user message plus
/// any short prior history; the server does NOT persist anything.</summary>
public record ChatMessageRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] List<ChatRoleMessage> Messages,
    [property: JsonPropertyName("systemPrompt")] string? SystemPrompt);

/// <summary>One executed tool call in the loop, surfaced for UI transparency.</summary>
public record ChatToolCallLogEntry(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("durationMs")] int DurationMs,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>Final assistant answer + a log of every tool call the loop executed.</summary>
public record ChatMessageResponse(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("iterations")] int Iterations,
    [property: JsonPropertyName("toolCalls")] List<ChatToolCallLogEntry> ToolCalls);
