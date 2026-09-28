using System.Text;

namespace BeeMemoryBank.Rekey.Steps;

/// <summary>
/// One sealed column of chat.db, as the chat repositories of the API write it (ChatMessageRepository,
/// ChatAttachmentRepository, ChatSettingsRepository, ChatConversationRepository). The re-key library does not
/// reference the API, so the shape is spelled here once, for the pre-flight and the chat step alike.
/// </summary>
/// <param name="Plain">The legacy plaintext column rows from before chat encryption still use (null: none).</param>
/// <param name="Bytes">Binary content (an attachment) rather than UTF-8 text.</param>
/// <param name="Aad">The AAD, from the row's id.</param>
internal sealed record ChatColumn(string Table, string Cipher, string Iv, string Version, string? Plain, bool Bytes, Func<string, byte[]> Aad)
{
    public string Name => $"{Table}.{Cipher}";
}

internal static class ChatColumns
{
    /// <summary>The key-version values of a chat.db column (ChatDataProtector): 1 = the chat key, NULL = legacy
    /// (the master DEK, or plaintext), -1 = marked unreadable.</summary>
    public const long ChatKeyVersion = 1, LegacyUnreadable = -1;

    /// <summary>Every sealed column; one the schema does not have yet is skipped by whoever walks them.</summary>
    public static readonly ChatColumn[] All =
    [
        new("chat_message", "content_ciphertext", "content_iv", "content_key_v", "content_text", false, _ => "bmb-chat-message-content-v1"u8.ToArray()),
        new("chat_message", "tool_calls_ciphertext", "tool_calls_iv", "tool_calls_key_v", "tool_calls_json", false, _ => "bmb-chat-message-toolcalls-v1"u8.ToArray()),
        // A legacy attachment's plaintext lives in the same column as its ciphertext: blob with a NULL iv.
        new("chat_attachment", "blob", "iv", "key_v", "blob", true, _ => "bmb-chat-attachment-blob-v1"u8.ToArray()),
        new("chat_api_key", "ciphertext", "iv", "key_v", null, false, _ => "bmb-openrouter-key-v1"u8.ToArray()),
        new("chat_api_key", "key_prefix_ciphertext", "key_prefix_iv", "key_prefix_key_v", null, false,
            id => Encoding.UTF8.GetBytes($"bmb-chat-api-key-prefix-v1:{Guid.Parse(id):D}")),
        new("chat_conversation", "title_ciphertext", "title_iv", "title_key_v", null, false,
            id => Encoding.UTF8.GetBytes($"bmb-chat-conversation-title-v1:{Guid.Parse(id):D}")),
    ];
}
