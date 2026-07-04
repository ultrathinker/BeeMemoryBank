using System.Text.Json.Serialization;

namespace BeeMemoryBank.Api.Models;

// ── chat_api_key row + DTOs ──────────────────────────────────────────────────

/// <summary>
/// Persisted OpenRouter API key. <c>Ciphertext</c>/<c>Iv</c> are the output of
/// <c>ArticleEncryptor.Encrypt(plaintext, masterDek, aad)</c> (AES-256-GCM under the master DEK).
/// The plaintext key is held in memory only at creation/decryption time and is NEVER persisted,
/// logged, or returned to the browser after creation (plan §1).
/// </summary>
public class ChatApiKey
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Iv { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public DateTime? DisabledUntil { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Returned ONCE at key creation — the only time the full key leaves the encrypt path.</summary>
public record ChatKeyCreatedResponse(Guid Id, string Label, string KeyPrefix, string ApiKey);

/// <summary>Listing shape — exposes only <c>KeyPrefix</c>, never the secret.</summary>
public record ChatKeyResponse(
    Guid Id,
    string Label,
    string KeyPrefix,
    bool Enabled,
    int Priority,
    DateTime? LastUsedAt,
    string? LastError,
    DateTime? DisabledUntil,
    DateTime CreatedAt);

public record CreateChatKeyRequest(string Label, string ApiKey, int Priority = 0);

/// <summary>Mutable metadata for a stored key. All fields optional; omitted fields are unchanged.</summary>
public record UpdateChatKeyRequest(
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("priority")] int? Priority);

// ── chat_model row + DTOs ────────────────────────────────────────────────────

public class ChatModelRow
{
    public Guid Id { get; set; }
    public string ModelId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Category { get; set; } = "text";
    public bool DefaultForCategory { get; set; }
    public bool Enabled { get; set; } = true;
}

public record ChatModelResponse(
    Guid Id,
    string ModelId,
    string Label,
    string Category,
    bool DefaultForCategory,
    bool Enabled);

public record CreateChatModelRequest(
    string ModelId,
    string Label,
    string Category = "text",
    bool DefaultForCategory = false,
    bool Enabled = true);

/// <summary>Toggles a model's enabled flag (admin catalogue).</summary>
public record UpdateChatModelRequest(
    [property: JsonPropertyName("enabled")] bool? Enabled);

/// <summary>Toggles the auto-approve-writes setting (skips the human confirm gate).</summary>
public record UpdateAutoApproveRequest(
    [property: JsonPropertyName("enabled")] bool Enabled);

// ── chat_conversation / chat_message rows ────────────────────────────────────

public class ChatConversation
{
    public Guid Id { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ChatMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = "";
    public string? ContentText { get; set; }
    public string? ToolCallsJson { get; set; }
    public string? ToolCallId { get; set; }
    public string? Model { get; set; }
    public int? TokensIn { get; set; }
    public int? TokensOut { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ── chat_attachment row (Phase 5: vision + image generation) ──────────────────
// Schema is unchanged from Phase 0 (id, message_id, kind, mime, blob, created_at) — Phase 5
// added NO migration and NO additive column. `kind` distinguishes a user-uploaded image sent to
// a vision model ("user-upload") from an image produced by an image-gen model ("generated-image").
public class ChatAttachment
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public string Kind { get; set; } = ""; // "user-upload" | "generated-image"
    public string Mime { get; set; } = "";
    public byte[]? Blob { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A reference to an attachment surfaced on a transcript message (no blob — the UI
/// fetches the bytes via GET /api/chat/attachments/{id}, which is ownership-checked). Included on
/// <see cref="ChatMessageRowResponse"/> so reopening a conversation renders inline images.</summary>
public record ChatAttachmentRef(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("mime")] string Mime);

/// <summary>Allowed <c>kind</c> values for <c>chat_attachment</c>.</summary>
public static class ChatAttachmentKind
{
    public const string UserUpload = "user-upload";
    public const string GeneratedImage = "generated-image";
}

// ── Completion ───────────────────────────────────────────────────────────────

public record ChatRoleMessage(string Role, string Content);

public record ChatCompleteRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] List<ChatRoleMessage> Messages);

public record ChatCompleteResponse(
    string Content,
    int PromptTokens,
    int CompletionTokens,
    string Model);

// ── Phase 2: streaming + conversation persistence ──────────────────────────

/// <summary>Streaming tool-loop turn request. The server owns persistence: if
/// <see cref="ConversationId"/> is null it creates a conversation, otherwise it loads that
/// conversation's history (scoped to the caller), appends <see cref="Message"/>, runs the tool
/// loop, and persists every turn. The browser receives the new conversation id via an early SSE
/// <c>conversation</c> event.</summary>
public record ChatStreamRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("conversationId")] Guid? ConversationId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("systemPrompt")] string? SystemPrompt,
    [property: JsonPropertyName("attachment")] ChatStreamAttachment? Attachment);

/// <summary>Phase 5: an optional image attached to the user turn. Only honored when the selected
/// model is category='vision' (rejected otherwise — see ChatEndpoints /stream). The bytes travel
/// inline in the /stream request (base64) rather than via a separate pre-message upload endpoint:
/// the existing <c>chat_attachment.message_id</c> is NOT NULL, and the user message is created
/// inside /stream, so linking the attachment to the just-created message avoids any schema change
/// (plan §2 Phase 5 allows "whichever is simpler"). Server-side MIME allow-list + size cap +
/// magic-byte check + resize are applied before storage and before the egress vision request.</summary>
public record ChatStreamAttachment(
    [property: JsonPropertyName("mime")] string Mime,
    [property: JsonPropertyName("dataBase64")] string DataBase64);

/// <summary>Conversation list entry (history sidebar). Scoped to the caller's own user_id.</summary>
public record ChatConversationResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt);

/// <summary>One stored message in a conversation transcript. Only <c>user</c>/<c>assistant</c>
/// text turns are rendered as bubbles; <c>tool</c> and assistant tool-call turns are kept for
/// reconstruction but surfaced collapsed/hidden by the UI. <c>Attachments</c> (Phase 5) lists any
/// images linked to the message (user-upload on a user turn, generated-image on an assistant
/// turn) so the UI renders them inline and offers a "Save to Bee" action.</summary>
public record ChatMessageRowResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("contentText")] string? ContentText,
    [property: JsonPropertyName("toolCallsJson")] string? ToolCallsJson,
    [property: JsonPropertyName("toolCallId")] string? ToolCallId,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("attachments")] List<ChatAttachmentRef>? Attachments);

/// <summary>Rename request body for PATCH /api/chat/conversations/{id}.</summary>
public record RenameConversationRequest(
    [property: JsonPropertyName("title")] string Title);
