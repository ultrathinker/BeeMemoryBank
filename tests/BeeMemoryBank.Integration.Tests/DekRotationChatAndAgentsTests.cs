using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What a DEK rotation must NOT destroy.
///
/// <para><b>chat.db.</b> Chat history, attachments and stored LLM provider keys live in a separate
/// SQLite file that the rotation transaction cannot reach. They used to be sealed straight under the
/// master DEK, so every rotation turned all of them into "[unable to decrypt]". They are now sealed
/// under a node chat key whose wrapped form the rotation carries forward — these tests pin that they
/// survive a rotation on the initiator and on an auto-accepting peer, including after a restart
/// (a fresh <see cref="SessionService"/> whose retired-DEK cache is empty), and that rows written
/// before the change are still readable and get migrated.</para>
///
/// <para><b>Agents.</b> Only an agent that carries a wrapped master DEK can be affected by a
/// rotation; every other agent must keep authenticating afterwards.</para>
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class DekRotationChatAndAgentsTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string Password = "chatRotationPwd1!";
    private const int AdminUserId = 1;

    // The AADs chat.db rows are sealed with — the pre-change rows used exactly these, under the master DEK.
    private static readonly byte[] ContentAad = "bmb-chat-message-content-v1"u8.ToArray();
    private static readonly byte[] ToolCallsAad = "bmb-chat-message-toolcalls-v1"u8.ToArray();
    private static readonly byte[] BlobAad = "bmb-chat-attachment-blob-v1"u8.ToArray();
    private static readonly byte[] ProviderKeyAad = "bmb-openrouter-key-v1"u8.ToArray();

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        var login = await _client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password });
        login.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ───── Seeding ───────────────────────────────────────────────────────────────────────────

    /// <summary>Everything a node's chat.db can hold, written the way the app writes it today
    /// (chat key) AND the way it was written before (directly under the master DEK, and plaintext).</summary>
    private sealed record ChatSeed(
        Guid ConversationId,
        Dictionary<Guid, (string Content, string? ToolCalls)> Messages,
        Dictionary<Guid, byte[]> Attachments,
        Dictionary<Guid, string> ProviderKeys);

    private async Task<ChatSeed> SeedChatAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var session = sp.GetRequiredService<SessionService>();
        var chatDb = sp.GetRequiredService<ChatDbConnectionFactory>();

        var conversationId = Guid.NewGuid();
        await sp.GetRequiredService<ChatConversationRepository>().CreateAsync(new ChatConversation
        {
            Id = conversationId, UserId = AdminUserId, Title = "rotation", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var messages = new Dictionary<Guid, (string, string?)>();
        var attachments = new Dictionary<Guid, byte[]>();
        var providerKeys = new Dictionary<Guid, string>();

        // 1. Current write path.
        var current = Guid.NewGuid();
        const string currentToolCalls = """[{"id":"call_1","type":"function","function":{"name":"bee_save_article","arguments":"{\"content\":\"secret body\"}"}}]""";
        await sp.GetRequiredService<ChatMessageRepository>().CreateAsync(new ChatMessage
        {
            Id = current, ConversationId = conversationId, Role = "assistant",
            ContentText = "written with the chat key", ToolCallsJson = currentToolCalls, CreatedAt = DateTime.UtcNow
        }, session);
        messages[current] = ("written with the chat key", currentToolCalls);

        var currentAttachment = Guid.NewGuid();
        var currentBlob = RandomNumberGenerator.GetBytes(64);
        await sp.GetRequiredService<ChatAttachmentRepository>().CreateAsync(new ChatAttachment
        {
            Id = currentAttachment, MessageId = current, Kind = ChatAttachmentKind.UserUpload, Mime = "image/png",
            Blob = (byte[])currentBlob.Clone(), CreatedAt = DateTime.UtcNow
        }, session);
        attachments[currentAttachment] = currentBlob;

        var settings = sp.GetRequiredService<ChatSettingsRepository>();
        var currentKey = new ChatApiKey
        {
            Id = Guid.NewGuid(), Label = "current", KeyPrefix = "sk-or-cur", Enabled = true, CreatedAt = DateTime.UtcNow
        };
        await settings.SealSecretAsync(currentKey, "sk-or-current-secret");
        await settings.CreateAsync(currentKey);
        providerKeys[currentKey.Id] = "sk-or-current-secret";

        // 2. Rows as the pre-change code wrote them: sealed directly under the master DEK.
        var masterDek = session.GetMasterDek();
        try
        {
            using var conn = (SqliteConnection)chatDb.CreateConnection();

            var legacy = Guid.NewGuid();
            var (c, cIv) = ArticleEncryptor.Encrypt("legacy under the master DEK", masterDek, ContentAad);
            var (t, tIv) = ArticleEncryptor.Encrypt("[]", masterDek, ToolCallsAad);
            await conn.ExecuteAsync(
                @"INSERT INTO chat_message (id, conversation_id, role, content_ciphertext, content_iv, tool_calls_ciphertext, tool_calls_iv, created_at)
                  VALUES (@id, @cid, 'assistant', @c, @cIv, @t, @tIv, @now)",
                new { id = legacy, cid = conversationId, c, cIv, t, tIv, now = DateTime.UtcNow.AddSeconds(1).ToString("o") });
            messages[legacy] = ("legacy under the master DEK", "[]");

            var legacyAttachment = Guid.NewGuid();
            var legacyBlob = RandomNumberGenerator.GetBytes(48);
            var (b, bIv) = MediaEncryptor.Encrypt(legacyBlob, masterDek, BlobAad);
            await conn.ExecuteAsync(
                @"INSERT INTO chat_attachment (id, message_id, kind, mime, blob, iv, created_at)
                  VALUES (@id, @mid, 'user-upload', 'image/png', @b, @bIv, @now)",
                new { id = legacyAttachment, mid = legacy, b, bIv, now = DateTime.UtcNow.AddSeconds(1).ToString("o") });
            attachments[legacyAttachment] = legacyBlob;

            var legacyKeyId = Guid.NewGuid();
            var (k, kIv) = ArticleEncryptor.Encrypt("sk-or-legacy-secret", masterDek, ProviderKeyAad);
            await conn.ExecuteAsync(
                @"INSERT INTO chat_api_key (id, label, key_prefix, ciphertext, iv, enabled, priority, created_at)
                  VALUES (@id, 'legacy', 'sk-or-leg', @k, @kIv, 1, 1, @now)",
                new { id = legacyKeyId, k, kIv, now = DateTime.UtcNow.AddSeconds(1).ToString("o") });
            providerKeys[legacyKeyId] = "sk-or-legacy-secret";

            // 3. A row from before chat encryption existed at all.
            var plaintext = Guid.NewGuid();
            await conn.ExecuteAsync(
                @"INSERT INTO chat_message (id, conversation_id, role, content_text, created_at)
                  VALUES (@id, @cid, 'user', 'legacy plaintext', @now)",
                new { id = plaintext, cid = conversationId, now = DateTime.UtcNow.AddSeconds(2).ToString("o") });
            messages[plaintext] = ("legacy plaintext", null);
        }
        finally
        {
            Array.Clear(masterDek);
        }

        return new ChatSeed(conversationId, messages, attachments, providerKeys);
    }

    /// <summary>
    /// Reads everything back through the repositories built on <paramref name="protector"/> and
    /// <paramref name="session"/> and asserts it is byte-for-byte what was written.
    /// </summary>
    private async Task AssertChatReadableAsync(ChatSeed seed, SessionService session, ChatDataProtector protector, string because)
    {
        var chatDb = _factory.Services.GetRequiredService<ChatDbConnectionFactory>();
        var msgRepo = new ChatMessageRepository(chatDb, protector);
        var attachRepo = new ChatAttachmentRepository(chatDb, protector);
        var settingsRepo = new ChatSettingsRepository(chatDb, protector);

        var messages = await msgRepo.ListByConversationAsync(seed.ConversationId, session);
        messages.Should().HaveCount(seed.Messages.Count);
        foreach (var m in messages)
        {
            var (content, toolCalls) = seed.Messages[m.Id];
            m.ContentText.Should().Be(content, because);
            m.ToolCallsJson.Should().Be(toolCalls, because);
        }

        var attachments = await attachRepo.ListByConversationAsync(seed.ConversationId, session);
        attachments.Should().HaveCount(seed.Attachments.Count);
        foreach (var a in attachments)
            a.Blob.Should().Equal(seed.Attachments[a.Id], because);

        var keys = await settingsRepo.ListAsync();
        var secrets = await settingsRepo.OpenSecretsAsync(keys);
        for (var i = 0; i < keys.Count; i++)
            secrets[i].Should().Be(seed.ProviderKeys[keys[i].Id], because);
    }

    /// <summary>
    /// A process restart, as far as keys are concerned: a brand-new SessionService unlocked from
    /// the key slots on disk (so its retired-DEK cache is empty) and a brand-new chat key provider
    /// with nothing cached.
    /// </summary>
    private async Task<(SessionService Session, ChatDataProtector Protector)> SimulateRestartAsync()
    {
        var freshSession = new SessionService(
            _factory.Services.GetRequiredService<IKeySlotRepository>(),
            _factory.Services.GetRequiredService<IServiceScopeFactory>());
        (await freshSession.UnlockAsync(Password)).Should().BeTrue("the post-rotation password/slot must unlock");

        var candidates = freshSession.GetCandidateDeks();
        try
        {
            candidates.Should().HaveCount(1, "a restarted process has no retired DEKs to fall back on");
        }
        finally
        {
            foreach (var c in candidates) Array.Clear(c);
        }

        var freshProtector = new ChatDataProtector(
            _factory.Services.GetRequiredService<IDbConnectionFactory>(), freshSession,
            NullLogger<ChatDataProtector>.Instance);
        return (freshSession, freshProtector);
    }

    private async Task AssertNoChatRowLeftUnderTheMasterDekAsync()
    {
        using var conn = _factory.Services.GetRequiredService<ChatDbConnectionFactory>().CreateConnection();
        (await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM chat_message WHERE (content_ciphertext IS NOT NULL AND content_key_v IS NOT 1) OR (tool_calls_ciphertext IS NOT NULL AND tool_calls_key_v IS NOT 1) OR content_text IS NOT NULL"))
            .Should().Be(0, "every message column must be on the chat key before the rotation commits");
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM chat_attachment WHERE key_v IS NOT 1"))
            .Should().Be(0);
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM chat_api_key WHERE key_v IS NOT 1"))
            .Should().Be(0);
    }

    private async Task<byte[]> ReadWrappedChatKeyAsync()
    {
        using var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<byte[]>(
            "SELECT wrapped_key FROM tbl_node_data_key WHERE key_name = 'chat'") ?? [];
    }

    // ───── Rotation drivers ──────────────────────────────────────────────────────────────────

    private async Task RotateAsInitiatorAsync()
    {
        var propose = await _client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password });
        propose.StatusCode.Should().Be(HttpStatusCode.OK);
        var commitEventId = (await propose.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("commitEventId").GetGuid().ToString();

        var accept = await _client.PostAsJsonAsync("/api/dek-rotation/accept", new { commitEventId, masterPassword = Password });
        accept.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var progress = await (await _client.GetAsync("/api/dek-rotation/progress")).Content.ReadFromJsonAsync<JsonElement>();
            var step = Enum.Parse<DekRotationFlowStep>(progress.GetProperty("currentStep").GetString()!);
            if (step == DekRotationFlowStep.Completed)
            {
                // Completed is published only after the accept path has left maintenance mode.
                _factory.Services.GetRequiredService<MaintenanceModeService>().IsInMaintenance
                    .Should().BeFalse("Completed must not be observable while the node still answers 503");
                return;
            }
            step.Should().NotBe(DekRotationFlowStep.Failed, progress.ToString());
            await Task.Delay(200);
        }
        throw new TimeoutException("DEK rotation did not complete within 60s");
    }

    /// <summary>
    /// Applies a rotation committed by a (simulated) whitelisted peer through the server's real
    /// auto-accept path — signature check, pre-rewrap hooks, DekRewrapper with isInitiator=false.
    /// Uses the legacy commit shape (new DEK wrapped under the old one), which the applier still
    /// accepts, so no envelope needs building. Returns the new master DEK.
    /// </summary>
    private async Task<byte[]> RotateAsAutoAcceptingPeerAsync()
    {
        var (peerPub, peerSeed) = Ed25519Signer.GenerateKeyPair();
        var peerNodeId = Guid.NewGuid();
        await _factory.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = peerNodeId, DisplayName = "Initiator", Ed25519PublicKey = peerPub, Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var session = _factory.Services.GetRequiredService<SessionService>();
        var oldDek = session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);
        int epoch;
        using (var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            epoch = await conn.ExecuteScalarAsync<int>("SELECT dek_epoch FROM tbl_node_identity");

        var (encNewDek, iv) = MasterKeyManager.WrapMasterDek(newDek, oldDek);
        Array.Clear(oldDek);
        var payload = new DekRotationCommitPayload(
            ProposedEventId: Guid.NewGuid().ToString(), NewDekEpoch: epoch + 1,
            RotationTs: DateTime.UtcNow.ToString("O"), OriginatorNodeId: peerNodeId.ToString(),
            EncryptedNewDek: Convert.ToBase64String(encNewDek), Iv: Convert.ToBase64String(iv));

        var commit = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = peerNodeId, LamportTs = 1_000_000,
            EventType = EventTypes.DekRotationCommit, Payload = JsonSerializer.Serialize(payload),
            ProtocolVersion = 1, CreatedAt = DateTime.UtcNow,
        };
        commit.Signature = Ed25519Signer.Sign(peerSeed, EventSignature.BuildPayload(commit));

        // What EventApplier records when the commit arrives; RewrapAll marks it Applied and stores
        // the chain material the restarted session's lazy slot rewrap walks.
        await _factory.Services.GetRequiredService<IDekRotationStateRepository>().UpsertAsync(new DekRotationStateRow(
            EventId: commit.EventId.ToString(), State: DekRotationState.Committing, ProposedEventId: payload.ProposedEventId,
            RotationTs: payload.RotationTs, AppliedAt: null, ErrorMessage: null,
            LastProcessedIdArticle: null, LastProcessedIdArticleVersion: null, LastProcessedIdMedia: null,
            LastProcessedIdConflictVersion: null, LastProcessedIdComment: null,
            CreatedAt: DateTime.UtcNow.ToString("O"), UpdatedAt: DateTime.UtcNow.ToString("O")));

        await _factory.Services.GetRequiredService<DekRotationService>().AutoAcceptCommitAsync(commit);

        var now = session.GetMasterDek();
        try { now.Should().Equal(newDek, "the peer must have swapped to the rotated DEK"); }
        finally { Array.Clear(now); }
        return newDek;
    }

    // ───── Chat survives rotation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task InitiatorRotation_ChatHistoryAttachmentsAndProviderKeys_StayReadable_EvenAfterRestart()
    {
        var seed = await SeedChatAsync();
        var session = _factory.Services.GetRequiredService<SessionService>();
        var protector = _factory.Services.GetRequiredService<ChatDataProtector>();
        await AssertChatReadableAsync(seed, session, protector, "before the rotation, legacy rows read through the master DEK");

        var wrappedBefore = await ReadWrappedChatKeyAsync();
        wrappedBefore.Should().NotBeEmpty("writing chat data creates the node chat key");

        await RotateAsInitiatorAsync();

        (await ReadWrappedChatKeyAsync()).Should().NotEqual(wrappedBefore, "the rotation re-wraps the chat key under the new DEK");
        await AssertNoChatRowLeftUnderTheMasterDekAsync();
        await AssertChatReadableAsync(seed, session, protector, "after the rotation, in the same process");

        var (freshSession, freshProtector) = await SimulateRestartAsync();
        using (freshProtector)
            await AssertChatReadableAsync(seed, freshSession, freshProtector, "after a restart, with no retired DEK in memory");
        freshSession.Lock();
    }

    [Fact]
    public async Task PeerAutoAccept_ChatHistoryAttachmentsAndProviderKeys_StayReadable_EvenAfterRestart()
    {
        var seed = await SeedChatAsync();
        var session = _factory.Services.GetRequiredService<SessionService>();
        var protector = _factory.Services.GetRequiredService<ChatDataProtector>();

        var newDek = await RotateAsAutoAcceptingPeerAsync();

        // The chat key row is sealed under the NEW master DEK only.
        using (var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            var row = await conn.QuerySingleAsync<(byte[] Wrapped, byte[] Iv)>(
                "SELECT wrapped_key, iv FROM tbl_node_data_key WHERE key_name = 'chat'");
            ChatDataKeyEnvelope.TryUnwrap(row.Wrapped, row.Iv, newDek).Should().NotBeNull();
        }
        Array.Clear(newDek);

        await AssertNoChatRowLeftUnderTheMasterDekAsync();
        await AssertChatReadableAsync(seed, session, protector, "after the peer applied the rotation");

        // A peer keeps its users' key slots under the OLD DEK and re-wraps them lazily at the next
        // unlock, walking the rotation chain — exactly what a restarted peer does.
        var (freshSession, freshProtector) = await SimulateRestartAsync();
        using (freshProtector)
            await AssertChatReadableAsync(seed, freshSession, freshProtector, "after the peer restarted");
        freshSession.Lock();
    }

    // ───── Legacy rows ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LegacyMasterDekRows_AreReadable_ThenMigrated_AndUnopenableOnesAreMarkedNotLooped()
    {
        var seed = await SeedChatAsync();
        var session = _factory.Services.GetRequiredService<SessionService>();
        var protector = _factory.Services.GetRequiredService<ChatDataProtector>();

        // A pre-change row that no key on this node opens (e.g. written before an earlier rotation).
        var orphan = Guid.NewGuid();
        var chatDb = _factory.Services.GetRequiredService<ChatDbConnectionFactory>();
        using (var conn = chatDb.CreateConnection())
        {
            var (c, cIv) = ArticleEncryptor.Encrypt("lost", RandomNumberGenerator.GetBytes(32), ContentAad);
            await conn.ExecuteAsync(
                @"INSERT INTO chat_message (id, conversation_id, role, content_ciphertext, content_iv, created_at)
                  VALUES (@id, @cid, 'assistant', @c, @cIv, @now)",
                new { id = orphan, cid = seed.ConversationId, c, cIv, now = DateTime.UtcNow.AddSeconds(5).ToString("o") });
        }

        var processor = new ChatHistoryBackfillProcessor(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<ILogger<ChatHistoryBackfillProcessor>>(),
            interval: TimeSpan.FromHours(1), batchSize: 2);
        (await processor.DrainAllPendingAsync(CancellationToken.None)).Should().BeGreaterThan(0);
        (await processor.DrainAllPendingAsync(CancellationToken.None)).Should().Be(0, "a drain must terminate and a second one find nothing");

        using (var conn = chatDb.CreateConnection())
        {
            (await conn.ExecuteScalarAsync<int?>("SELECT content_key_v FROM chat_message WHERE id = @id", new { id = orphan }))
                .Should().Be(ChatDataProtector.LegacyUnreadable, "an unopenable legacy row is marked so it stops being rescanned");
            (await conn.ExecuteScalarAsync<byte[]>("SELECT content_ciphertext FROM chat_message WHERE id = @id", new { id = orphan }))
                .Should().NotBeNullOrEmpty("it must be left intact, not destroyed");
            (await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM chat_message WHERE id <> @id AND (content_key_v IS NOT 1 OR (tool_calls_ciphertext IS NOT NULL AND tool_calls_key_v IS NOT 1))",
                new { id = orphan })).Should().Be(0);
        }

        var msgs = await new ChatMessageRepository(chatDb, protector).ListByConversationAsync(seed.ConversationId, session);
        msgs.Single(m => m.Id == orphan).ContentText.Should().StartWith("[unable to decrypt");
        foreach (var m in msgs.Where(m => m.Id != orphan))
            m.ContentText.Should().Be(seed.Messages[m.Id].Content);
    }

    [Fact]
    public async Task ChatKeyRowMissing_AsAfterRestoringAnOlderSnapshot_DegradesToPlaceholders_AndChatKeepsWorking()
    {
        var seed = await SeedChatAsync();
        var session = _factory.Services.GetRequiredService<SessionService>();
        var protector = _factory.Services.GetRequiredService<ChatDataProtector>();
        var processor = new ChatHistoryBackfillProcessor(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<ILogger<ChatHistoryBackfillProcessor>>(),
            interval: TimeSpan.FromHours(1));
        await processor.DrainAllPendingAsync(CancellationToken.None);

        // A restored main database from before the chat key existed: no row. Every restore path
        // locks the session, which is what drops the cached key.
        using (var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("DELETE FROM tbl_node_data_key");
        session.Lock();
        (await session.UnlockAsync(Password)).Should().BeTrue();

        var chatDb = _factory.Services.GetRequiredService<ChatDbConnectionFactory>();
        var msgRepo = new ChatMessageRepository(chatDb, protector);
        var msgs = await msgRepo.ListByConversationAsync(seed.ConversationId, session);
        msgs.Should().HaveCount(seed.Messages.Count, "a key mismatch must not fail the transcript load");
        // The drain above moved every row onto the (now lost) chat key, the plaintext one included.
        msgs.Should().OnlyContain(m => m.ContentText!.StartsWith("[unable to decrypt"));
        foreach (var m in msgs.Where(m => m.ToolCallsJson != null))
            JsonSerializer.Deserialize<List<ChatToolCall>>(m.ToolCallsJson!).Should().NotBeNull("the tool-calls placeholder must stay valid JSON");

        (await new ChatAttachmentRepository(chatDb, protector).ListByConversationAsync(seed.ConversationId, session))
            .Should().OnlyContain(a => a.Blob!.Length == 0, "unopenable attachments are blanked, not served as garbage");
        var keys = await new ChatSettingsRepository(chatDb, protector).ListAsync();
        (await new ChatSettingsRepository(chatDb, protector).OpenSecretsAsync(keys)).Should().OnlyContain(s => s == null);

        // New chat writes work under the replacement key.
        var fresh = Guid.NewGuid();
        await msgRepo.CreateAsync(new ChatMessage
        {
            Id = fresh, ConversationId = seed.ConversationId, Role = "user", ContentText = "after restore", CreatedAt = DateTime.UtcNow.AddMinutes(1)
        }, session);
        (await msgRepo.ListByConversationAsync(seed.ConversationId, session)).Single(m => m.Id == fresh)
            .ContentText.Should().Be("after restore");
    }

    [Fact]
    public async Task ChatKeyCache_IsWipedOnLock()
    {
        await SeedChatAsync();
        var session = _factory.Services.GetRequiredService<SessionService>();
        var protector = _factory.Services.GetRequiredService<ChatDataProtector>();
        using (await protector.AcquireAsync()) { }

        session.Lock();
        var afterLock = async () => { using var _ = await protector.AcquireAsync(); };
        await afterLock.Should().ThrowAsync<BeeMemoryBank.Core.Exceptions.SessionLockedException>(
            "no chat key may be served from memory once the vault is locked");
    }

    // ───── Agents ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InitiatorRotation_KeepsAgentsWithoutKeyMaterial_AndRemovesAutoUnlockAgents_OverMcp()
    {
        var createUser = await _client.PostAsJsonAsync("/api/users", new
        {
            username = "teammate", password = "teammatePwd123!", displayName = "Teammate", role = "user"
        });
        createUser.EnsureSuccessStatusCode();
        var teammateId = (await createUser.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var session = _factory.Services.GetRequiredService<SessionService>();
        var teammateKey = AgentKeyHelper.GenerateApiKey();
        var adminKey = AgentKeyHelper.GenerateApiKey();
        using (var scope = _factory.Services.CreateScope())
        {
            var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
            await agentRepo.CreateAsync(new Agent
            {
                Name = "teammate-agent", KeyPrefix = AgentKeyHelper.GetKeyPrefix(teammateKey),
                KeyHash = AgentKeyHelper.ComputeKeyHash(teammateKey), Status = "A", CreatedAt = DateTime.UtcNow,
                OwnerUserId = teammateId
            });

            var masterDek = session.GetMasterDek();
            var (enc, iv, salt) = AgentKeyHelper.EncryptDekV1(adminKey, masterDek);
            Array.Clear(masterDek);
            await agentRepo.CreateAsync(new Agent
            {
                Name = "admin-agent", KeyPrefix = AgentKeyHelper.GetKeyPrefix(adminKey),
                KeyHash = AgentKeyHelper.ComputeKeyHash(adminKey), EncryptedDek = enc, DekIV = iv, Salt = salt,
                KdfVersion = 1, Status = "A", CreatedAt = DateTime.UtcNow, OwnerUserId = AdminUserId
            });
        }

        await RotateAsInitiatorAsync();

        (await CallToolTextAsync(teammateKey, "bee_get_tree")).Should().NotContain("not recognized",
            "an ordinary user's agent holds no key material and must keep working after a rotation");
        (await CallToolTextAsync(adminKey, "bee_get_tree")).Should().Contain("not recognized",
            "an agent that carried the old master DEK is removed by the rotation and must be re-issued");
    }

    // ───── Minimal MCP client (same handshake as McpSessionGuardMiddlewareTests) ────────────

    private async Task<string> CallToolTextAsync(string bearer, string toolName)
    {
        var init = await McpPostAsync(new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "rotation-tests", version = "1.0" } }
        }, null, bearer);
        init.EnsureSuccessStatusCode();
        var sessionId = init.Headers.GetValues("Mcp-Session-Id").First();
        (await McpPostAsync(new { jsonrpc = "2.0", method = "notifications/initialized" }, sessionId, bearer)).EnsureSuccessStatusCode();

        var resp = await McpPostAsync(new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = toolName, arguments = new { } }
        }, sessionId, bearer);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        if (resp.Content.Headers.ContentType?.MediaType?.Contains("event-stream") == true)
            body = body.Split('\n').Select(l => l.TrimEnd('\r')).First(l => l.StartsWith("data:"))[5..].Trim();
        var result = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("result");
        return result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    private async Task<HttpResponseMessage> McpPostAsync(object payload, string? sessionId, string bearer)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (sessionId != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        return await _client.SendAsync(req);
    }

    // ───── Fix round 1 ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChatRotationHook_RefusesWhenLegacyRowsRemain_AndPassesOnceTheyAreMoved()
    {
        var seed = await SeedChatAsync(); // leaves 4 legacy records: 2 messages, 1 attachment, 1 provider key
        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var logger = _factory.Services.GetRequiredService<ILogger<ChatDekRotationHook>>();

        // Starved of batches, the drain cannot finish; the recount must catch that, not report success.
        var starved = new ChatDekRotationHook(scopeFactory, logger, maxBatches: 1, batchSize: 1);
        var act = () => starved.BeforeRewrapAsync(CancellationToken.None);
        (await act.Should().ThrowAsync<BeeMemoryBank.Core.Exceptions.DekRotationPreconditionException>())
            .Which.Message.Should().Contain("still sealed directly under the current master key");

        await new ChatDekRotationHook(scopeFactory, logger).BeforeRewrapAsync(CancellationToken.None);
        await AssertNoChatRowLeftUnderTheMasterDekAsync();
        await AssertChatReadableAsync(seed, _factory.Services.GetRequiredService<SessionService>(),
            _factory.Services.GetRequiredService<ChatDataProtector>(), "after the hook moved everything");
    }

    [Fact]
    public async Task ChatRotationHook_RefusesWhileLocked()
    {
        _factory.Services.GetRequiredService<SessionService>().Lock();
        var hook = new ChatDekRotationHook(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<ILogger<ChatDekRotationHook>>());
        var act = () => hook.BeforeRewrapAsync(CancellationToken.None);
        await act.Should().ThrowAsync<BeeMemoryBank.Core.Exceptions.DekRotationPreconditionException>();
    }

    [Fact]
    public async Task MalformedChatKeyRow_IsTreatedAsUnreadable_NotAServerError()
    {
        var seed = await SeedChatAsync();
        var session = _factory.Services.GetRequiredService<SessionService>();
        var protector = _factory.Services.GetRequiredService<ChatDataProtector>();

        // An IV of the wrong size makes AesGcm throw ArgumentException rather than a tag mismatch.
        using (var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_node_data_key SET iv = x'0102030405' WHERE key_name = 'chat'");
        session.Lock();
        (await session.UnlockAsync(Password)).Should().BeTrue();

        var chatDb = _factory.Services.GetRequiredService<ChatDbConnectionFactory>();
        var msgRepo = new ChatMessageRepository(chatDb, protector);
        var loaded = async () => await msgRepo.ListByConversationAsync(seed.ConversationId, session);
        (await loaded.Should().NotThrowAsync()).Subject.Should().HaveCount(seed.Messages.Count);

        var fresh = Guid.NewGuid();
        await msgRepo.CreateAsync(new ChatMessage
        {
            Id = fresh, ConversationId = seed.ConversationId, Role = "user", ContentText = "after repair", CreatedAt = DateTime.UtcNow.AddMinutes(1)
        }, session);
        (await msgRepo.ListByConversationAsync(seed.ConversationId, session)).Single(m => m.Id == fresh)
            .ContentText.Should().Be("after repair");
    }

    [Fact]
    public async Task CachedChatKey_IsNotHandedOut_InTheWindowBetweenLockAndTheLockedEvent()
    {
        await SeedChatAsync(); // creates the chat key row
        var session = new SessionService(
            _factory.Services.GetRequiredService<IKeySlotRepository>(),
            _factory.Services.GetRequiredService<IServiceScopeFactory>());
        (await session.UnlockAsync(Password)).Should().BeTrue();

        // Subscribed BEFORE the protector, so it runs after the master DEK is cleared but before the
        // protector's own cache wipe — exactly the window a concurrent request can hit.
        Exception? inWindow = null;
        ChatDataProtector? protector = null;
        session.Locked += () =>
        {
            try { using var _ = protector!.AcquireAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { inWindow = ex; }
        };
        protector = new ChatDataProtector(
            _factory.Services.GetRequiredService<IDbConnectionFactory>(), session, NullLogger<ChatDataProtector>.Instance);
        using (await protector.AcquireAsync()) { } // the key is now cached

        session.Lock();

        inWindow.Should().BeOfType<BeeMemoryBank.Core.Exceptions.SessionLockedException>(
            "no chat key lease may be issued once the vault has started locking");
        protector.Dispose();
    }

    [Fact]
    public async Task RemoteAccountToken_SurvivesRotation_AndRestart()
    {
        var session = _factory.Services.GetRequiredService<SessionService>();
        var dek = session.GetMasterDek();
        var (token, iv) = RemoteAccountService.SealToken("bmbrt_remote_secret", dek);
        Array.Clear(dek);
        await _factory.Services.GetRequiredService<IRemoteAccountRepository>().CreateAsync(new RemoteAccount
        {
            Id = Guid.NewGuid(), DisplayName = "Remote", BaseUrl = "https://remote.example", RemoteUsername = "u",
            EncryptedToken = token, TokenIv = iv, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        await RotateAsInitiatorAsync();

        var (freshSession, freshProtector) = await SimulateRestartAsync();
        freshProtector.Dispose();
        var account = (await _factory.Services.GetRequiredService<IRemoteAccountRepository>().ListAllAsync()).Single();
        var current = freshSession.GetMasterDek();
        try
        {
            RemoteAccountService.TryOpenToken(account.EncryptedToken, account.TokenIv, current)
                .Should().Be("bmbrt_remote_secret", "the rotation must carry remote-account tokens forward");
        }
        finally
        {
            Array.Clear(current);
            freshSession.Lock();
        }
    }
}
