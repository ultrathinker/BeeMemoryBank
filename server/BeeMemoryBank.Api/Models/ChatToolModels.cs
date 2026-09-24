using System.Text.Json.Serialization;

namespace BeeMemoryBank.Api.Models;

// ── OpenAI / OpenRouter tool-calling wire models ─────────────────────────────
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
    /// message. When set, <see cref="BeeMemoryBank.Api.Services.OpenRouterClient"/> builds a
    /// multimodal egress content array <c>[{type:text},{type:image_url},...]</c> instead of a plain
    /// string. It is deliberately <c>[JsonIgnore]</c> so it NEVER persists (the bytes live in
    /// chat_attachment keyed by message_id) and never leaks onto the wire as a top-level field —
    /// the egress client reads it explicitly via its wire-message mapping. Only used transiently for
    /// the turn it belongs to.</summary>
    [JsonIgnore] public List<string>? ImageDataUrls { get; set; }
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
/// never crash). Trailing optional params keep existing construction sites compiling.</summary>
public sealed record ToolCompletionResult(
    string? Content,
    List<ResolvedToolCall>? ToolCalls,
    string Model,
    int? PromptTokens = null,
    int? CompletionTokens = null);

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
