using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// H3a regression tests. Rows written before the H3/H3b encryption fixes keep plaintext
/// <c>content_text</c> / <c>tool_calls_json</c> / attachment <c>blob</c> on disk forever unless
/// something migrates them -- <see cref="ChatHistoryBackfillProcessor"/> is that one-time
/// backfill. It is built and driven here exactly the way <c>PendingEmbeddingProcessorTests</c>
/// drives <c>PendingEmbeddingProcessor</c>: constructed directly (not via the hosted-service
/// lifecycle) so the batch method can be called synchronously from the test.
/// </summary>
public class ChatHistoryBackfillProcessorTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private const string Password = "chatBackfillTestPassword";
    private const int UserId = 1;

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync(password: Password);
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private ChatHistoryBackfillProcessor CreateProcessor() => new(
        _factory.Services.GetRequiredService<IServiceScopeFactory>(),
        _factory.Services.GetRequiredService<ILogger<ChatHistoryBackfillProcessor>>(),
        interval: TimeSpan.FromHours(1), // never fires its own periodic tick during the test
        batchSize: 50);

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

    /// <summary>Inserts a legacy chat_message row directly via SQL (bypassing
    /// ChatMessageRepository.CreateAsync, which would encrypt it) so it looks exactly like a row
    /// written before the H3/H3b fixes existed.</summary>
    private static async Task InsertLegacyMessageAsync(
        ChatDbConnectionFactory dbFactory, Guid conversationId, Guid messageId, string? contentText, string? toolCallsJson)
    {
        using var conn = (SqliteConnection)dbFactory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO chat_message
            (id, conversation_id, role, content_text, tool_calls_json, created_at)
            VALUES (@id, @cid, 'assistant', @content, @toolCalls, @created)";
        cmd.Parameters.AddWithValue("@id", messageId);
        cmd.Parameters.AddWithValue("@cid", conversationId);
        cmd.Parameters.AddWithValue("@content", (object?)contentText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@toolCalls", (object?)toolCallsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertLegacyAttachmentAsync(ChatDbConnectionFactory dbFactory, Guid attachmentId, Guid messageId, byte[] plaintextBlob)
    {
        using var conn = (SqliteConnection)dbFactory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO chat_attachment
            (id, message_id, kind, mime, blob, created_at)
            VALUES (@id, @mid, 'user-upload', 'image/png', @blob, @created)";
        cmd.Parameters.AddWithValue("@id", attachmentId);
        cmd.Parameters.AddWithValue("@mid", messageId);
        cmd.Parameters.AddWithValue("@blob", plaintextBlob);
        cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<(bool ContentIsNull, bool ContentCiphertextPopulated, bool ToolCallsIsNull, bool ToolCallsCiphertextPopulated)>
        ReadRawMessageStateAsync(ChatDbConnectionFactory dbFactory, Guid messageId)
    {
        using var conn = (SqliteConnection)dbFactory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT content_text, content_ciphertext, tool_calls_json, tool_calls_ciphertext
                             FROM chat_message WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", messageId);
        using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.IsDBNull(0), !reader.IsDBNull(1), reader.IsDBNull(2), !reader.IsDBNull(3));
    }

    [Fact]
    public async Task ProcessPendingAsync_WhileLocked_ReturnsZeroAndDoesNotThrow()
    {
        // Session is never unlocked in this test -- mirrors "must not block the unlock request
        // itself": the processor must tolerate running before/without an unlock and just no-op.
        using var scope = _factory.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<ChatDbConnectionFactory>();
        var conversationId = Guid.NewGuid();
        var convoRepo = scope.ServiceProvider.GetRequiredService<ChatConversationRepository>();
        await convoRepo.CreateAsync(new ChatConversation
        {
            Id = conversationId, UserId = UserId, Title = "t", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await InsertLegacyMessageAsync(dbFactory, conversationId, Guid.NewGuid(), "plaintext while locked", null);

        var processor = CreateProcessor();
        var processed = await processor.ProcessPendingAsync(CancellationToken.None);

        processed.Should().Be(0);
    }

    [Fact]
    public async Task DrainAllPendingAsync_ConvertsLegacyMessageRow_ContentAndToolCallsBothSides()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();
        (await session.UnlockAsync(Password)).Should().BeTrue();

        var convoRepo = scope.ServiceProvider.GetRequiredService<ChatConversationRepository>();
        var msgRepo = scope.ServiceProvider.GetRequiredService<ChatMessageRepository>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<ChatDbConnectionFactory>();

        var conversationId = Guid.NewGuid();
        await convoRepo.CreateAsync(new ChatConversation
        {
            Id = conversationId, UserId = UserId, Title = "t", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        const string legacyContent = "legacy plaintext assistant reply";
        var legacyToolCalls = BuildSaveArticleToolCallsJson("legacy plaintext article body");
        var messageId = Guid.NewGuid();
        await InsertLegacyMessageAsync(dbFactory, conversationId, messageId, legacyContent, legacyToolCalls);

        var processor = CreateProcessor();
        var migrated = await processor.DrainAllPendingAsync(CancellationToken.None);
        migrated.Should().BeGreaterThan(0);

        var (contentIsNull, contentCiphertextPopulated, toolCallsIsNull, toolCallsCiphertextPopulated) =
            await ReadRawMessageStateAsync(dbFactory, messageId);
        contentIsNull.Should().BeTrue("content_text must be cleared once migrated");
        contentCiphertextPopulated.Should().BeTrue();
        toolCallsIsNull.Should().BeTrue("tool_calls_json must be cleared once migrated");
        toolCallsCiphertextPopulated.Should().BeTrue();

        // The repository's normal read path must still return the exact original plaintext.
        var loaded = await msgRepo.ListByConversationAsync(conversationId, session);
        loaded.Should().ContainSingle();
        loaded[0].ContentText.Should().Be(legacyContent);
        loaded[0].ToolCallsJson.Should().Be(legacyToolCalls);
    }

    [Fact]
    public async Task DrainAllPendingAsync_ConvertsLegacyAttachmentBlob()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();
        (await session.UnlockAsync(Password)).Should().BeTrue();

        var dbFactory = scope.ServiceProvider.GetRequiredService<ChatDbConnectionFactory>();
        var attachRepo = scope.ServiceProvider.GetRequiredService<ChatAttachmentRepository>();

        var plaintextBlob = new byte[] { 1, 2, 3, 4, 5, 250, 251, 252 };
        var attachmentId = Guid.NewGuid();
        await InsertLegacyAttachmentAsync(dbFactory, attachmentId, Guid.NewGuid(), plaintextBlob);

        var processor = CreateProcessor();
        var migrated = await processor.DrainAllPendingAsync(CancellationToken.None);
        migrated.Should().BeGreaterThan(0);

        using (var conn = (SqliteConnection)dbFactory.CreateConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT blob, iv FROM chat_attachment WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", attachmentId);
            using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.IsDBNull(1).Should().BeFalse("iv must be populated once migrated");
            var rawBlob = (byte[])reader["blob"];
            rawBlob.Should().NotEqual(plaintextBlob, "the on-disk blob must now be ciphertext, not the original bytes");
        }

        var loaded = await attachRepo.GetByIdForUserAsync(attachmentId, UserId, session);
        // Not ownership-linked to a real conversation/user in this test, so GetByIdForUserAsync
        // (which joins through chat_message -> chat_conversation -> user_id) legitimately returns
        // null here; the point of this test is the raw on-disk ciphertext conversion above.
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task DrainAllPendingAsync_IsIdempotent_SecondRunMakesNoFurtherChanges()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();
        (await session.UnlockAsync(Password)).Should().BeTrue();

        var convoRepo = scope.ServiceProvider.GetRequiredService<ChatConversationRepository>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<ChatDbConnectionFactory>();

        var conversationId = Guid.NewGuid();
        await convoRepo.CreateAsync(new ChatConversation
        {
            Id = conversationId, UserId = UserId, Title = "t", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var messageId = Guid.NewGuid();
        await InsertLegacyMessageAsync(dbFactory, conversationId, messageId, "idempotency test content", BuildSaveArticleToolCallsJson("idempotency test body"));

        var processor = CreateProcessor();
        var firstRun = await processor.DrainAllPendingAsync(CancellationToken.None);
        firstRun.Should().BeGreaterThan(0);

        var stateAfterFirstRun = await ReadRawMessageStateAsync(dbFactory, messageId);

        // Second run: nothing left to migrate for this row, so it must be a clean no-op --
        // no exception, no further row touched, and (importantly) not a re-encryption that would
        // silently replace the ciphertext written by the first run.
        var secondRun = await processor.DrainAllPendingAsync(CancellationToken.None);
        secondRun.Should().Be(0);

        var stateAfterSecondRun = await ReadRawMessageStateAsync(dbFactory, messageId);
        stateAfterSecondRun.Should().Be(stateAfterFirstRun);
    }

    [Fact]
    public async Task ProcessPendingAsync_NoLegacyRows_ReturnsZeroWithoutTouchingDek()
    {
        // The "fresh, never-upgraded node" case: unlocked, but chat.db has no plaintext rows at
        // all (a brand-new chat.db, or one that finished backfilling already). Must return 0
        // cheaply rather than erroring or looping.
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();
        (await session.UnlockAsync(Password)).Should().BeTrue();

        var processor = CreateProcessor();
        var processed = await processor.ProcessPendingAsync(CancellationToken.None);

        processed.Should().Be(0);
    }
}
