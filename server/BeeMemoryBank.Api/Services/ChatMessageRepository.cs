using Dapper;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// CRUD for <c>chat_message</c> (chat.db). Node-local — never synced, never snapshotted.
/// Persisted transcript is consumed starting in Phase 2; the repo is scaffolded here per plan §2.
/// </summary>
public sealed class ChatMessageRepository(ChatDbConnectionFactory factory) : ChatRepositoryBase(factory)
{
    private const string Cols = @"id AS Id, conversation_id AS ConversationId, role AS Role,
        content_text AS ContentText, tool_calls_json AS ToolCallsJson, tool_call_id AS ToolCallId,
        model AS Model, tokens_in AS TokensIn, tokens_out AS TokensOut, created_at AS CreatedAt";

    public async Task<List<Models.ChatMessage>> ListByConversationAsync(Guid conversationId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Models.ChatMessage>(
            $"SELECT {Cols} FROM chat_message WHERE conversation_id = @conversationId ORDER BY created_at ASC",
            new { conversationId })).ToList();
    }

    public async Task CreateAsync(Models.ChatMessage message)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO chat_message
              (id, conversation_id, role, content_text, tool_calls_json, tool_call_id,
               model, tokens_in, tokens_out, created_at)
              VALUES (@Id, @ConversationId, @Role, @ContentText, @ToolCallsJson, @ToolCallId,
                      @Model, @TokensIn, @TokensOut, @CreatedAt)",
            message);
    }
}
