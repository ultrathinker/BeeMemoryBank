using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// H3b regression tests. <c>tool_calls_json</c> holds the assistant's raw tool-call arguments,
/// which for any WRITE tool (bee_save_article, bee_update_article, bee_append_to_article,
/// bee_replace_in_article) ARE decrypted vault content -- the exact gap the original H3 fix
/// (content_text only) left open. <see cref="ChatMessageRepository.CreateAsync"/> now encrypts it
/// the same way content_text is encrypted (its own AAD, its own ciphertext/iv column pair) -- see
/// ChatMessageRepository's class remarks.
/// </summary>
public class ChatToolCallsEncryptionTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private const string Password = "toolCallsEncryptionTestPassword";
    private const int UserId = 1;

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync(password: Password);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();
        (await session.UnlockAsync(Password)).Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private static string BuildSaveArticleToolCallsJson(string articleBody) => JsonSerializer.Serialize(new List<ChatToolCall>
    {
        new()
        {
            Id = "call_" + Guid.NewGuid().ToString("N")[..12],
            Type = "function",
            Function = new ChatToolCallFunction
            {
                Name = "bee_save_article",
                Arguments = JsonSerializer.Serialize(new { path = "/Secret", content = articleBody })
            }
        }
    });

    private async Task<Guid> CreateConversationAsync(ChatConversationRepository convoRepo)
    {
        var conversationId = Guid.NewGuid();
        await convoRepo.CreateAsync(new ChatConversation
        {
            Id = conversationId,
            UserId = UserId,
            Title = "Tool-calls encryption test conversation",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        return conversationId;
    }

    [Fact]
    public async Task CreateAsync_ToolCallsJson_IsNotReadableAsPlaintextInDb()
    {
        const string secretArticleBody = "the quick brown fox jumps over the lazy dog -- decrypted vault content that must never hit disk in the clear";
        var toolCallsJson = BuildSaveArticleToolCallsJson(secretArticleBody);

        using var scope = _factory.Services.CreateScope();
        var convoRepo = scope.ServiceProvider.GetRequiredService<ChatConversationRepository>();
        var msgRepo = scope.ServiceProvider.GetRequiredService<ChatMessageRepository>();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<ChatDbConnectionFactory>();

        var conversationId = await CreateConversationAsync(convoRepo);
        var messageId = Guid.NewGuid();

        await msgRepo.CreateAsync(new ChatMessage
        {
            Id = messageId,
            ConversationId = conversationId,
            Role = "assistant",
            ToolCallsJson = toolCallsJson,
            CreatedAt = DateTime.UtcNow
        }, session);

        // Read the RAW row straight off disk, bypassing the repository's own decrypt path
        // entirely -- this is exactly what an attacker (or a curious admin) reading chat.db
        // directly on the filesystem would see.
        using var conn = (SqliteConnection)dbFactory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tool_calls_json, tool_calls_ciphertext, tool_calls_iv FROM chat_message WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", messageId);
        using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.IsDBNull(0).Should().BeTrue("tool_calls_json must be NULL on disk once encrypted -- no plaintext copy left behind");
        reader.IsDBNull(1).Should().BeFalse("tool_calls_ciphertext must be populated");
        reader.IsDBNull(2).Should().BeFalse("tool_calls_iv must be populated");

        var ciphertextBytes = (byte[])reader["tool_calls_ciphertext"];
        // AES-GCM ciphertext is opaque bytes -- decoding it as Latin1 can never legitimately
        // reproduce the plaintext substring, so this is a direct "is the secret recoverable from
        // the raw bytes without the DEK" check, not just an encoding-shape assertion.
        var rawBytesAsText = Encoding.Latin1.GetString(ciphertextBytes);
        rawBytesAsText.Should().NotContain(secretArticleBody);
        rawBytesAsText.Should().NotContain("bee_save_article");

        // The repository's own read path decrypts it back to the exact original JSON.
        var loaded = await msgRepo.ListByConversationAsync(conversationId, session);
        loaded.Should().ContainSingle();
        loaded[0].ToolCallsJson.Should().Be(toolCallsJson);
    }

    [Fact]
    public async Task ListByConversationAsync_LegacyPlaintextRow_StillLoadsContentAndToolCalls()
    {
        using var scope = _factory.Services.CreateScope();
        var convoRepo = scope.ServiceProvider.GetRequiredService<ChatConversationRepository>();
        var msgRepo = scope.ServiceProvider.GetRequiredService<ChatMessageRepository>();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<ChatDbConnectionFactory>();

        var conversationId = await CreateConversationAsync(convoRepo);
        var messageId = Guid.NewGuid();
        const string legacyContent = "legacy plaintext assistant reply, written before the H3 fix";
        var legacyToolCalls = BuildSaveArticleToolCallsJson("legacy plaintext article body, written before the H3b fix");

        // Insert the row the way a pre-H3/H3b node would have: plain content_text and
        // tool_calls_json, no ciphertext columns at all. Deliberately bypasses
        // ChatMessageRepository.CreateAsync -- the whole point is to simulate a row nobody has
        // touched since before encryption existed.
        using (var conn = (SqliteConnection)dbFactory.CreateConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO chat_message
                (id, conversation_id, role, content_text, tool_calls_json, created_at)
                VALUES (@id, @cid, 'assistant', @content, @toolCalls, @created)";
            cmd.Parameters.AddWithValue("@id", messageId);
            cmd.Parameters.AddWithValue("@cid", conversationId);
            cmd.Parameters.AddWithValue("@content", legacyContent);
            cmd.Parameters.AddWithValue("@toolCalls", legacyToolCalls);
            cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }

        var loaded = await msgRepo.ListByConversationAsync(conversationId, session);
        loaded.Should().ContainSingle();
        loaded[0].ContentText.Should().Be(legacyContent);
        loaded[0].ToolCallsJson.Should().Be(legacyToolCalls);
    }
}
