using System.Text;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A conversation title is the first 120 characters of the user's first message, and a provider
/// key's key_prefix is the first 12 characters of the API key: both are content, so both are sealed
/// under the chat key and the plaintext columns stay empty on disk (content re-keying design,
/// X1 and Y6). Rows from before that are sealed by the chat backfill.
/// </summary>
public class ChatTitleAndKeyPrefixSealingTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private const string Password = "titleSealingTestPassword";
    private const int UserId = 1;
    private const string Sentinel = "SENTINEL-7f3a-first-words-of-a-secret-message";

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync(password: Password);
        (await Session.UnlockAsync(Password)).Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private SessionService Session => _factory.Services.GetRequiredService<SessionService>();

    private T Get<T>() where T : notnull => _factory.Services.CreateScope().ServiceProvider.GetRequiredService<T>();

    private SqliteConnection ChatDb() => (SqliteConnection)Get<ChatDbConnectionFactory>().CreateConnection();

    private async Task<Guid> CreateConversationAsync(string title)
    {
        var id = Guid.NewGuid();
        await Get<ChatConversationRepository>().CreateAsync(new ChatConversation
        {
            Id = id, UserId = UserId, Title = title, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        return id;
    }

    [Fact]
    public async Task ATitle_IsSealedOnDisk_AndReadsBack()
    {
        var id = await CreateConversationAsync(Sentinel);

        using (var conn = ChatDb())
        {
            var raw = await conn.QuerySingleAsync<(string Title, byte[] Ciphertext)>(
                "SELECT title, title_ciphertext FROM chat_conversation WHERE id = @id", new { id });
            raw.Title.Should().BeEmpty("the plaintext column is written empty");
            Encoding.Latin1.GetString(raw.Ciphertext).Should().NotContain(Sentinel);
        }

        (await Get<ChatConversationRepository>().GetByIdForUserAsync(id, UserId))!.Title.Should().Be(Sentinel);
        (await Get<ChatConversationRepository>().ListByUserAsync(UserId)).Single().Title.Should().Be(Sentinel);
    }

    [Fact]
    public async Task ARename_IsSealedOnDisk()
    {
        var id = await CreateConversationAsync("first");

        await Get<ChatConversationRepository>().UpdateTitleAsync(id, Sentinel);

        using (var conn = ChatDb())
            (await conn.QuerySingleAsync<string>("SELECT title FROM chat_conversation WHERE id = @id", new { id }))
                .Should().BeEmpty();
        (await Get<ChatConversationRepository>().GetByIdForUserAsync(id, UserId))!.Title.Should().Be(Sentinel);
    }

    [Fact]
    public async Task WhileLocked_TheListStillLoads_WithAPlaceholderTitle()
    {
        await CreateConversationAsync(Sentinel);
        Session.Lock();

        var list = await Get<ChatConversationRepository>().ListByUserAsync(UserId);

        list.Single().Title.Should().Be(ChatConversationRepository.LockedTitlePlaceholder);
    }

    [Fact]
    public async Task ASealedTitleMovedOntoAnotherConversation_DoesNotOpen()
    {
        var source = await CreateConversationAsync(Sentinel);
        var target = await CreateConversationAsync("other");
        using (var conn = ChatDb())
            await conn.ExecuteAsync(
                @"UPDATE chat_conversation SET
                    title_ciphertext = (SELECT title_ciphertext FROM chat_conversation WHERE id = @source),
                    title_iv = (SELECT title_iv FROM chat_conversation WHERE id = @source)
                  WHERE id = @target",
                new { source, target });

        (await Get<ChatConversationRepository>().GetByIdForUserAsync(target, UserId))!.Title
            .Should().Be(ChatConversationRepository.UndecryptableTitlePlaceholder);
    }

    [Fact]
    public async Task ALegacyPlaintextTitle_IsSealedByTheBackfill()
    {
        var id = Guid.NewGuid();
        using (var conn = ChatDb())
            await conn.ExecuteAsync(
                "INSERT INTO chat_conversation (id, user_id, title, created_at, updated_at) VALUES (@id, @UserId, @Sentinel, @now, @now)",
                new { id, UserId, Sentinel, now = DateTime.UtcNow.ToString("o") });

        await CreateProcessor().DrainAllPendingAsync(CancellationToken.None);

        using (var conn = ChatDb())
            (await conn.QuerySingleAsync<string>("SELECT title FROM chat_conversation WHERE id = @id", new { id }))
                .Should().BeEmpty("the backfill moves a legacy title into the sealed columns");
        (await Get<ChatConversationRepository>().GetByIdForUserAsync(id, UserId))!.Title.Should().Be(Sentinel);
    }

    [Fact]
    public async Task AKeyPrefix_IsSealedOnDisk_AndTheListingShowsIt()
    {
        var repo = Get<ChatSettingsRepository>();
        var key = new ChatApiKey { Id = Guid.NewGuid(), Label = "k", KeyPrefix = "sk-SENTINEL1", Enabled = true, CreatedAt = DateTime.UtcNow };
        await repo.SealSecretAsync(key, "sk-SENTINEL1-and-the-rest-of-the-secret");
        await repo.CreateAsync(key);

        using (var conn = ChatDb())
        {
            var raw = await conn.QuerySingleAsync<(string Prefix, byte[] Ciphertext)>(
                "SELECT key_prefix, key_prefix_ciphertext FROM chat_api_key WHERE id = @Id", new { key.Id });
            raw.Prefix.Should().BeEmpty();
            Encoding.Latin1.GetString(raw.Ciphertext).Should().NotContain("SENTINEL1");
        }

        var listed = await repo.ListAsync();
        await repo.OpenPrefixesAsync(listed);
        listed.Single().KeyPrefix.Should().Be("sk-SENTINEL1");
    }

    [Fact]
    public async Task ALegacyPlaintextKeyPrefix_IsSealedByTheBackfill()
    {
        var repo = Get<ChatSettingsRepository>();
        var key = new ChatApiKey { Id = Guid.NewGuid(), Label = "k", KeyPrefix = "sk-SENTINEL2", Enabled = true, CreatedAt = DateTime.UtcNow };
        await repo.SealSecretAsync(key, "sk-SENTINEL2-rest");
        await repo.CreateAsync(key);
        using (var conn = ChatDb())
            await conn.ExecuteAsync(
                "UPDATE chat_api_key SET key_prefix = 'sk-SENTINEL2', key_prefix_ciphertext = NULL, key_prefix_iv = NULL, key_prefix_key_v = NULL WHERE id = @Id",
                new { key.Id });

        await CreateProcessor().DrainAllPendingAsync(CancellationToken.None);

        using (var conn = ChatDb())
            (await conn.QuerySingleAsync<string>("SELECT key_prefix FROM chat_api_key WHERE id = @Id", new { key.Id }))
                .Should().BeEmpty();
        var listed = await repo.ListAsync();
        await repo.OpenPrefixesAsync(listed);
        listed.Single().KeyPrefix.Should().Be("sk-SENTINEL2");
    }

    private ChatHistoryBackfillProcessor CreateProcessor() => new(
        _factory.Services.GetRequiredService<IServiceScopeFactory>(),
        _factory.Services.GetRequiredService<ILogger<ChatHistoryBackfillProcessor>>(),
        interval: TimeSpan.FromHours(1),
        batchSize: 50);
}
