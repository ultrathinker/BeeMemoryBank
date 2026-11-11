using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Providers;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Dapper;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The provider registry (docs/ai-providers.md): the upgrade of a 2.9.0 chat.db with nobody doing anything, the rollback rule (what
/// 2.9.0's reader of chat_api_key would see), the sealed approval and what withdraws it, and who may touch providers and keys at all.
/// </summary>
public class ProviderRegistryTests : IAsyncLifetime
{
    private const string Password = "providerRegistryPassword";
    private readonly BmbWebApplicationFactory _factory = new();
    private readonly ScriptedProviderHandler _provider = new((req, body) => body.Contains("\"stream\":true")
        ? ProviderTestKit.Sse("vendor/answer-model", "Hello", " there")
        : ProviderTestKit.Ok("{}"));
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory.RouteOutboundHttpThrough(_provider);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-User-Id", "1");
        await _factory.InitializeNodeAsync(password: Password);
        (await _factory.Services.GetRequiredService<SessionService>().UnlockAsync(Password)).Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, text);
        return JsonSerializer.Deserialize<T>(text, Web)!;
    }

    private async Task<ProviderResponse> ProviderAsync(Guid id) =>
        (await ReadAsync<List<ProviderResponse>>(await _client.GetAsync("/api/chat/providers"))).Single(p => p.Id == id);

    private System.Data.IDbConnection ChatDb(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ChatDbConnectionFactory>().CreateConnection();

    private byte[] ChatKey()
    {
        using var scope = _factory.Services.CreateScope();
        var dek = _factory.Services.GetRequiredService<SessionService>().GetMasterDek();
        using var main = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var row = main.QuerySingle<(byte[] Wrapped, byte[] Iv)>("SELECT wrapped_key, iv FROM tbl_node_data_key WHERE key_name = 'chat'");
        return ChatDataKeyEnvelope.TryUnwrap(row.Wrapped, row.Iv, dek)!;
    }

    /// <summary>What 2.9.0 does with chat_api_key before every chat turn: every enabled, not cooling-down row, opened with its constant AAD
    /// under the chat key, and sent to openrouter.ai.</summary>
    private List<string> WhatTwoNineZeroWouldSendToOpenRouter()
    {
        var chatKey = ChatKey();
        using var scope = _factory.Services.CreateScope();
        using var conn = ChatDb(scope);
        var rows = conn.Query<(byte[] Cipher, byte[] Iv)>(
            "SELECT ciphertext, iv FROM chat_api_key WHERE enabled = 1 AND (disabled_until IS NULL OR disabled_until <= @now) ORDER BY priority ASC, created_at ASC",
            new { now = DateTime.UtcNow.ToString("o") });
        return rows.Select(r => ArticleEncryptor.Decrypt(r.Cipher, r.Iv, chatKey, "bmb-openrouter-key-v1"u8.ToArray())).ToList();
    }

    // ── Upgrade from 2.9.0 ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The 2.9.0 shape of chat.db, made from this one: no provider table, no column of this version, keys and models in the
    /// legacy tables only. Then the schema step of this version runs over it, as it does at the first start after the update.</summary>
    [Fact]
    public async Task AnUpgradedNode_HasItsKeysAndModelsUnderTheBuiltInOpenRouter_WithItsDefaults_AndTheChatWorks()
    {
        Guid keyId, textModel, visionModel;
        using (var scope = _factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            keyId = await ProviderTestKit.AddKeyAsync(sp, BuiltInOpenRouter.Id, "sk-or-v1-upgraded-key-0001");
            textModel = (await ProviderTestKit.AddModelAsync(sp, BuiltInOpenRouter.Id, "vendor/text-model")).Id;
            visionModel = (await ProviderTestKit.AddModelAsync(sp, BuiltInOpenRouter.Id, "vendor/vision-model", isText: false, isVision: true)).Id;
            await sp.GetRequiredService<ChatSettingsRepository>().SetDefaultModelIdsAsync(textModel.ToString(), visionModel.ToString(), null);
            using var conn = ChatDb(scope);
            conn.Execute("DROP TABLE chat_provider; DROP TABLE chat_provider_key; DROP TABLE chat_provider_model;");
            conn.Execute("ALTER TABLE chat_settings DROP COLUMN fallback_text_model_ids; ALTER TABLE chat_message DROP COLUMN provider;");
        }

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ChatDbInitializer>().InitializeAsync();

        var builtIn = await ProviderAsync(BuiltInOpenRouter.Id);
        builtIn.Name.Should().Be("OpenRouter");
        builtIn.BaseUrl.Should().Be("https://openrouter.ai/api/v1");
        builtIn.Approved.Should().BeTrue("2.9.0 trusted that host; nobody has to do anything after the update");
        builtIn.BuiltIn.Should().BeTrue();
        builtIn.KeyCount.Should().Be(1);
        builtIn.ModelCount.Should().Be(2);
        var keys = await ReadAsync<List<JsonElement>>(await _client.GetAsync("/api/chat/keys"));
        keys.Should().ContainSingle().Which.GetProperty("providerId").GetGuid().Should().Be(BuiltInOpenRouter.Id);
        keys[0].GetProperty("id").GetGuid().Should().Be(keyId);
        var models = await ReadAsync<List<JsonElement>>(await _client.GetAsync("/api/chat/models/all"));
        models.Should().OnlyContain(m => m.GetProperty("providerName").GetString() == "OpenRouter");
        var defaults = await ReadAsync<JsonElement>(await _client.GetAsync("/api/chat/settings/defaults"));
        defaults.GetProperty("defaultTextModelId").GetString().Should().Be(textModel.ToString());
        defaults.GetProperty("defaultVisionModelId").GetString().Should().Be(visionModel.ToString());

        var turn = await _client.PostAsJsonAsync("/api/chat/stream", new { message = "hi there" });
        var sse = await turn.Content.ReadAsStringAsync();
        turn.StatusCode.Should().Be(HttpStatusCode.OK, sse);
        sse.Should().Contain("event: done").And.Contain("\"provider\":\"OpenRouter\"").And.Contain("Hello");
        var sent = _provider.Seen.Should().ContainSingle().Subject;
        sent.Url.Should().Be("https://openrouter.ai/api/v1/chat/completions");
        sent.Key.Should().Be("sk-or-v1-upgraded-key-0001");
        JsonDocument.Parse(sent.Body).RootElement.GetProperty("model").GetString().Should().Be("vendor/text-model");
        JsonDocument.Parse(sent.Body).RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean().Should().BeTrue(
            "the built-in provider keeps 2.9.0's wire options");
    }

    [Fact]
    public async Task ANodeThatNeverUsedTheChat_GetsNoProviderAtTheUpgrade()
    {
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ChatDbInitializer>().InitializeAsync();
        (await ReadAsync<List<ProviderResponse>>(await _client.GetAsync("/api/chat/providers"))).Should().BeEmpty();
    }

    // ── Rollback to 2.9.0 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoNineZero_SeesOnlyOpenRouterKeys_AndNoKeyThatWasDeletedHere()
    {
        (await _client.PostAsJsonAsync("/api/chat/keys", new { label = "or", apiKey = "sk-or-v1-openrouter-0001" })).EnsureSuccessStatusCode();
        var zai = await ReadAsync<ProviderResponse>(await _client.PostAsJsonAsync("/api/chat/providers", new { presetId = "zai-coding" }), HttpStatusCode.Created);
        (await _client.PostAsJsonAsync("/api/chat/keys", new { label = "zai", apiKey = "zai-coding-secret-0002", providerId = zai.Id })).EnsureSuccessStatusCode();
        var other = await ReadAsync<ProviderResponse>(await _client.PostAsJsonAsync("/api/chat/providers",
            new { presetId = "openrouter", name = "OpenRouter work account" }), HttpStatusCode.Created);
        (await _client.PostAsJsonAsync("/api/chat/keys", new { label = "work", apiKey = "sk-or-v1-work-0003", providerId = other.Id })).EnsureSuccessStatusCode();

        WhatTwoNineZeroWouldSendToOpenRouter().Should().Equal(["sk-or-v1-openrouter-0001"],
            "a key of any other provider - even another OpenRouter account - is never in the table 2.9.0 sends to openrouter.ai");

        var keys = await ReadAsync<List<JsonElement>>(await _client.GetAsync("/api/chat/keys"));
        var builtInKey = keys.Single(k => k.GetProperty("providerId").GetGuid() == BuiltInOpenRouter.Id).GetProperty("id").GetGuid();
        (await _client.DeleteAsync($"/api/chat/keys/{builtInKey}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        WhatTwoNineZeroWouldSendToOpenRouter().Should().BeEmpty("a key deleted here must not survive in the table 2.9.0 reads");

        (await _client.PostAsJsonAsync("/api/chat/keys", new { label = "or2", apiKey = "sk-or-v1-openrouter-0004" })).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync("/api/chat/models", new { modelId = "vendor/m", label = "M" })).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/chat/providers/{BuiltInOpenRouter.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        WhatTwoNineZeroWouldSendToOpenRouter().Should().BeEmpty("deleting the built-in provider deletes its keys from chat_api_key");
        using var scope = _factory.Services.CreateScope();
        using var conn = ChatDb(scope);
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_model").Should().Be(0);
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_provider_key").Should().Be(2, "the other providers keep their keys");
    }

    /// <summary>2.9.0's own schema step and its writes, run over a database of this version: it must still open it and work in it.</summary>
    [Fact]
    public async Task TwoNineZero_StillOpensTheDatabase_AndWritesItsRows()
    {
        var zai = await ReadAsync<ProviderResponse>(await _client.PostAsJsonAsync("/api/chat/providers", new { presetId = "zai" }), HttpStatusCode.Created);
        (await _client.PostAsJsonAsync("/api/chat/keys", new { label = "zai", apiKey = "zai-secret-0005", providerId = zai.Id })).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync("/api/chat/models", new { modelId = "glm-4.6", label = "GLM", providerId = zai.Id })).EnsureSuccessStatusCode();
        using var scope = _factory.Services.CreateScope();
        using var conn = ChatDb(scope);
        // 2.9.0's statements, verbatim in what matters: CREATE TABLE IF NOT EXISTS of its tables, its single-row seed, and its INSERTs.
        conn.Execute("CREATE TABLE IF NOT EXISTS chat_api_key (id TEXT PRIMARY KEY, label TEXT NOT NULL, key_prefix TEXT NOT NULL, ciphertext BLOB NOT NULL, iv BLOB NOT NULL, key_v INTEGER, enabled INTEGER NOT NULL DEFAULT 1, priority INTEGER NOT NULL DEFAULT 0, disabled_until TEXT, last_error TEXT, last_used_at TEXT, created_at TEXT NOT NULL);");
        conn.Execute("INSERT OR IGNORE INTO chat_settings (id, auto_approve_writes) VALUES (1, 0);");
        conn.Execute(@"INSERT INTO chat_model (id, model_id, label, category, is_text, is_vision, is_image_gen, context_window, created_at)
                       VALUES (@id, 'vendor/x', 'X', 'text', 1, 0, 0, NULL, @now)", new { id = Guid.NewGuid().ToString(), now = DateTime.UtcNow.ToString("o") });
        conn.Execute(@"INSERT INTO chat_message (id, conversation_id, role, content_text, content_ciphertext, content_iv, content_key_v, tool_calls_json,
                       tool_calls_ciphertext, tool_calls_iv, tool_calls_key_v, tool_call_id, model, tokens_in, tokens_out, tool_calls_count, duration_ms, created_at)
                       VALUES (@id, @c, 'assistant', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 'vendor/x', 1, 1, 0, 1, @now)",
            new { id = Guid.NewGuid().ToString(), c = Guid.NewGuid().ToString(), now = DateTime.UtcNow.ToString("o") });
        conn.Query<string>("SELECT model_id FROM chat_model").Should().Equal(["vendor/x"], "2.9.0 sees its own models only");
    }

    // ── The approval ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANewProvider_IsNotApproved_UntilItsExactHostIsConfirmed_AndANewAddressWithdrawsIt()
    {
        var created = await ReadAsync<ProviderResponse>(await _client.PostAsJsonAsync("/api/chat/providers", new { presetId = "zai-coding" }), HttpStatusCode.Created);
        created.Approved.Should().BeFalse();
        created.Host.Should().Be("api.z.ai");
        created.BaseUrl.Should().Be("https://api.z.ai/api/coding/paas/v4");
        created.Note.Should().Contain("Coding Plan quota only in supported coding tools");

        (await _client.PostAsJsonAsync($"/api/chat/providers/{created.Id}/approve", new { host = "api.evil.example" })).StatusCode
            .Should().Be(HttpStatusCode.Conflict, "the approval names the host the person was shown");
        (await ProviderAsync(created.Id)).Approved.Should().BeFalse();

        var approved = await ReadAsync<ProviderResponse>(await _client.PostAsJsonAsync($"/api/chat/providers/{created.Id}/approve", new { host = "api.z.ai" }));
        approved.Approved.Should().BeTrue();
        approved.ApprovedHost.Should().Be("api.z.ai");

        var moved = await ReadAsync<ProviderResponse>(await _client.PatchAsJsonAsync($"/api/chat/providers/{created.Id}", new { baseUrl = "https://api.z.ai/api/paas/v4" }));
        moved.Approved.Should().BeFalse("a new base URL withdraws the approval");
        (await ReadAsync<ProviderResponse>(await _client.PostAsJsonAsync($"/api/chat/providers/{created.Id}/approve", new { host = "api.z.ai" }))).Approved.Should().BeTrue();

        var renamed = await ReadAsync<ProviderResponse>(await _client.PatchAsJsonAsync($"/api/chat/providers/{created.Id}", new { name = "Z.ai pay as you go" }));
        renamed.Approved.Should().BeTrue("a name is not an address");

        var local = await ReadAsync<ProviderResponse>(await _client.PatchAsJsonAsync($"/api/chat/providers/{created.Id}", new { isLocal = true }));
        local.Approved.Should().BeFalse("the local flag decides which addresses may be used, so it withdraws the approval too");

        using var scope = _factory.Services.CreateScope();
        using var main = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        main.Query<string>("SELECT action FROM tbl_audit_log WHERE entity_type = 'chat_provider' ORDER BY rowid").Should()
            .Contain(["chat_provider_added", "chat_provider_approved", "chat_provider_readdressed"]);
    }

    [Fact]
    public async Task AnApproval_CannotBeWrittenIntoTheDatabase_ByHand()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var a = await ProviderTestKit.AddProviderAsync(sp, "A", "https://a.example/v1");
        var b = await ProviderTestKit.AddProviderAsync(sp, "B", "https://b.example/v1", approve: false);
        using (var conn = ChatDb(scope))
        {
            // A stamp copied from another provider, display columns filled in: still not approved.
            conn.Execute(@"UPDATE chat_provider SET approval_ciphertext = (SELECT approval_ciphertext FROM chat_provider WHERE id = @a),
                           approval_iv = (SELECT approval_iv FROM chat_provider WHERE id = @a), approval_key_v = 1, approved_host = 'b.example',
                           approved_at = @now WHERE id = @b", new { a = a.Id, b = b.Id, now = DateTime.UtcNow.ToString("o") });
        }
        using (var conn = ChatDb(scope))
            conn.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_provider WHERE id = @b AND approval_ciphertext IS NOT NULL", new { b = b.Id }).Should().Be(1);
        (await ProviderAsync(b.Id)).Approved.Should().BeFalse("the stamp is bound to the provider it was sealed for");
        (await ProviderAsync(a.Id)).Approved.Should().BeTrue();

        using (var conn = ChatDb(scope))
            conn.Execute("UPDATE chat_provider SET base_url = 'https://evil.example/v1' WHERE id = @a", new { a = a.Id });
        (await ProviderAsync(a.Id)).Approved.Should().BeFalse("the stamp names the address that was approved");

        await ProviderTestKit.AddKeyAsync(sp, BuiltInOpenRouter.Id, "sk-or-v1-builtin-0006");
        (await ProviderAsync(BuiltInOpenRouter.Id)).Approved.Should().BeTrue();
        using (var conn = ChatDb(scope))
            conn.Execute("UPDATE chat_provider SET base_url = 'https://evil.example/api/v1' WHERE id = @id", new { id = BuiltInOpenRouter.Id });
        (await ProviderAsync(BuiltInOpenRouter.Id)).Approved.Should().BeFalse("the built-in provider is approved only at the address 2.9.0 had");
    }

    [Fact]
    public async Task ANodeReset_DeletesEveryProvidersKeys_AndKeepsTheProvidersAndModels()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            await ProviderTestKit.AddKeyAsync(sp, BuiltInOpenRouter.Id, "sk-or-v1-reset-0009");
            var other = await ProviderTestKit.AddProviderAsync(sp, "Other", "https://other.example/v1");
            await ProviderTestKit.AddKeyAsync(sp, other.Id, "sk-other-reset-0010");
            await ProviderTestKit.AddModelAsync(sp, other.Id, "other-model");
            await ConversationWithProviderStateAsync(sp);
            using (var before = ChatDb(scope))
                before.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_message WHERE provider_state_ciphertext IS NOT NULL").Should().Be(1);
            var hook = sp.GetServices<INodeResetHook>().OfType<ApiStateResetHook>().Single();
            await hook.AfterVaultWipedAsync(CancellationToken.None);
        }
        using var check = _factory.Services.CreateScope();
        using var conn = ChatDb(check);
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_api_key").Should().Be(0);
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_provider_key").Should().Be(0, "a key of the old vault must not outlive it");
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_provider").Should().Be(2, "the providers are the node's settings");
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_provider_model").Should().Be(1);
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_message").Should().Be(0, "the conversations of the old vault go with it");
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_message WHERE provider_state_ciphertext IS NOT NULL")
            .Should().Be(0, "a model's stored thinking goes with its conversation (review prov-r2, finding 3)");
    }

    /// <summary>A conversation of user 1 whose assistant tool-call turn carries a provider state (sealed in provider_state_ciphertext), as the
    /// tool loop stores it, and a later message of the same conversation.</summary>
    private static async Task<Guid> ConversationWithProviderStateAsync(IServiceProvider sp)
    {
        var conversation = Guid.NewGuid();
        await sp.GetRequiredService<ChatConversationRepository>().CreateAsync(new ChatConversation
            { Id = conversation, UserId = 1, Title = "with thinking", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        var messages = sp.GetRequiredService<ChatMessageRepository>();
        var session = sp.GetRequiredService<SessionService>();
        await messages.CreateAsync(new ChatMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation, Role = "assistant",
            ToolCallsJson = "[{\"id\":\"toolu_1\",\"type\":\"function\",\"function\":{\"name\":\"bee_get_tree\",\"arguments\":\"{}\"}}]",
            ProviderStateJson = new ProviderTurnState(ProviderProtocols.AnthropicMessages, Guid.NewGuid(), "claude-opus-5-5",
                JsonDocument.Parse("[{\"type\":\"thinking\",\"thinking\":\"LIFECYCLE-THOUGHT\",\"signature\":\"sig\"}]").RootElement.Clone()).ToJson(),
            CreatedAt = DateTime.UtcNow,
        }, session);
        await messages.CreateAsync(new ChatMessage
            { Id = Guid.NewGuid(), ConversationId = conversation, Role = "tool", ToolCallId = "toolu_1", ContentText = "{}", CreatedAt = DateTime.UtcNow }, session);
        return conversation;
    }

    [Fact]
    public async Task DeletingAConversation_DeletesItsStoredProviderState()
    {
        Guid conversation, kept;
        using (var scope = _factory.Services.CreateScope())
        {
            conversation = await ConversationWithProviderStateAsync(scope.ServiceProvider);
            kept = await ConversationWithProviderStateAsync(scope.ServiceProvider);
        }
        using (var scope = _factory.Services.CreateScope())
        using (var conn = ChatDb(scope))
            conn.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_message WHERE provider_state_ciphertext IS NOT NULL").Should().Be(2);

        (await _client.DeleteAsync($"/api/chat/conversations/{conversation}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var check = _factory.Services.CreateScope();
        using var db = ChatDb(check);
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_message WHERE conversation_id = @c", new { c = conversation }).Should().Be(0);
        db.Query<string>("SELECT conversation_id FROM chat_message WHERE provider_state_ciphertext IS NOT NULL").Select(Guid.Parse)
            .Should().Equal([kept], "only the state of the conversation that was deleted is gone");
    }

    [Fact]
    public async Task TheBuiltInProvidersAddress_IsFixed()
    {
        (await _client.PostAsJsonAsync("/api/chat/keys", new { label = "or", apiKey = "sk-or-v1-fixed-0007" })).EnsureSuccessStatusCode();
        var response = await _client.PatchAsJsonAsync($"/api/chat/providers/{BuiltInOpenRouter.Id}", new { baseUrl = "https://openrouter.example/api/v1" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("fixed");
        (await ProviderAsync(BuiltInOpenRouter.Id)).BaseUrl.Should().Be(BuiltInOpenRouter.BaseUrl);
    }

    [Theory]
    [InlineData(1, "something-else")]
    [InlineData(2, "")]
    public async Task AnUnknownProtocol_CannotBeCreated(int row, string protocol)
    {
        var response = await _client.PostAsJsonAsync("/api/chat/providers", new { name = $"P{row}", protocol, baseUrl = "https://api.example.com/v1" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.GetFromJsonAsync<List<ProviderResponse>>("/api/chat/providers", Web)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, "anthropic-messages")]
    [InlineData(2, "gemini")]
    public async Task TheNativeProtocols_HaveTheirClientsOnAFullNode(int row, string protocol)
    {
        var created = await ReadAsync<ProviderResponse>(await _client.PostAsJsonAsync("/api/chat/providers",
            new { name = $"P{row}", protocol, baseUrl = "https://api.example.com/v1" }), HttpStatusCode.Created);
        created.Protocol.Should().Be(protocol);
        created.ClientAvailable.Should().BeTrue();
        created.Approved.Should().BeFalse();
    }

    [Fact]
    public void AProtocolWithoutARegisteredClient_IsNotAvailable_AndFailsClosed()
    {
        var registry = new ModelClientRegistry([ProviderTestKit.Client(new ScriptedProviderHandler((_, _) => ProviderTestKit.Ok("x")))]);
        registry.IsAvailable(ProviderProtocols.OpenAiChat).Should().BeTrue();
        registry.IsAvailable(ProviderProtocols.Gemini).Should().BeFalse();
        ((Action)(() => registry.Resolve(ProviderProtocols.AnthropicMessages))).Should().Throw<ProviderUnavailableException>();
    }

    [Fact]
    public async Task ACustomProvider_KeepsWhatWasTyped_WhenTheAddressIsRefused()
    {
        var response = await _client.PostAsJsonAsync("/api/chat/providers", new { name = "Mine", protocol = "openai-chat", baseUrl = "http://api.example.com/v1" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("local");

        var local = await ReadAsync<ProviderResponse>(await _client.PostAsJsonAsync("/api/chat/providers",
            new { name = "Mine", protocol = "openai-chat", baseUrl = "http://192.168.1.50:11434/v1", isLocal = true }), HttpStatusCode.Created);
        local.IsLocal.Should().BeTrue();
        local.Host.Should().Be("192.168.1.50:11434");
    }

    [Fact]
    public async Task AddingOrApprovingAProvider_NeedsAnUnlockedVault()
    {
        var created = await ReadAsync<ProviderResponse>(await _client.PostAsJsonAsync("/api/chat/providers", new { presetId = "deepseek" }), HttpStatusCode.Created);
        _factory.Services.GetRequiredService<SessionService>().Lock();
        (await _client.PostAsJsonAsync("/api/chat/providers", new { presetId = "openai" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.PostAsJsonAsync($"/api/chat/providers/{created.Id}/approve", new { host = "api.deepseek.com" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.PatchAsJsonAsync($"/api/chat/providers/{created.Id}", new { baseUrl = "https://api.example.com/v1" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ThePresets_IncludeBothZaiEndpoints_AndTheLocalServers()
    {
        var presets = await ReadAsync<List<ProviderPreset>>(await _client.GetAsync("/api/chat/providers/presets"));
        presets.Single(p => p.Id == "zai").BaseUrl.Should().Be("https://api.z.ai/api/paas/v4");
        presets.Single(p => p.Id == "zai-coding").BaseUrl.Should().Be("https://api.z.ai/api/coding/paas/v4");
        presets.Single(p => p.Id == "zai-coding").Note.Should().NotBeNullOrWhiteSpace();
        presets.Where(p => p.Local).Select(p => p.Id).Should().BeEquivalentTo(["ollama", "lmstudio", "llamacpp"]);
        presets.Select(p => p.Protocol).Distinct().Should().BeEquivalentTo(
            [ProviderProtocols.OpenAiChat, ProviderProtocols.AnthropicMessages, ProviderProtocols.Gemini],
            "every protocol of the list has a client on a full node, so every preset is offered");
        ProviderPresets.All.Should().HaveCount(presets.Count);
    }

    // ── Who may touch providers and keys ─────────────────────────────────────────────────────────────────────

    private static readonly string[] ProviderRoutePrefixes = ["/api/chat/providers", "/api/chat/keys", "/api/chat/settings/fallbacks"];

    private List<(string Method, string Path)> ManagementRoutes() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => ProviderRoutePrefixes.Any(p => e.RoutePattern.RawText?.StartsWith(p, StringComparison.Ordinal) == true))
            .SelectMany(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()!.HttpMethods
                .Select(m => (m, e.RoutePattern.RawText!.Replace("{id:guid}", Guid.NewGuid().ToString()))))
            .ToList();

    [Fact]
    public async Task AnAgent_EvenOneOwnedByTheSuperadmin_CanNeitherReadNorChangeProvidersOrKeys()
    {
        string apiKey = AgentKeyHelper.GenerateApiKey();
        using (var scope = _factory.Services.CreateScope())
        {
            var owner = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin");
            await scope.ServiceProvider.GetRequiredService<IAgentRepository>().CreateAsync(new Agent
            {
                Name = "provider-attacker", KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey), KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
                Status = "A", CreatedAt = DateTime.UtcNow, OwnerUserId = owner!.Id,
            });
        }
        using var agent = _factory.Server.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        agent.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var routes = ManagementRoutes();
        routes.Should().HaveCountGreaterThan(10);
        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method is "POST" or "PATCH") request.Content = JsonContent.Create(new { label = "x", apiKey = "x", host = "x", name = "x" });
            var response = await agent.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {path}: {body}");
            body.Should().Contain("Agents cannot perform this operation", $"{method} {path} must stop at the agent gate");
        }
    }

    [Fact]
    public async Task AUserWhoIsNotSuperadmin_CanNeitherReadNorChangeProvidersOrKeys()
    {
        using var user = _factory.Server.CreateClient();
        user.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        user.DefaultRequestHeaders.Add("X-User-Role", "user");
        user.DefaultRequestHeaders.Add("X-User-Id", "1");
        foreach (var (method, path) in ManagementRoutes())
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method is "POST" or "PATCH") request.Content = JsonContent.Create(new { label = "x", apiKey = "x", host = "x", name = "x" });
            var response = await user.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {path}");
        }
    }

    [Fact]
    public async Task NoKey_EverComesBackToTheBrowser_AfterItWasSaved()
    {
        var zai = await ReadAsync<ProviderResponse>(await _client.PostAsJsonAsync("/api/chat/providers", new { presetId = "zai" }), HttpStatusCode.Created);
        const string secret = "zai-very-secret-key-value-0008";
        (await _client.PostAsJsonAsync("/api/chat/keys", new { label = "zai", apiKey = secret, providerId = zai.Id })).EnsureSuccessStatusCode();
        foreach (var path in new[] { "/api/chat/keys", "/api/chat/providers" })
        {
            var text = await (await _client.GetAsync(path)).Content.ReadAsStringAsync();
            text.Should().NotContain(secret);
        }
        var keys = await ReadAsync<List<JsonElement>>(await _client.GetAsync("/api/chat/keys"));
        keys.Single().GetProperty("keyPrefix").GetString().Should().Be(secret[..12] + "…");
        using var scope = _factory.Services.CreateScope();
        using var conn = ChatDb(scope);
        var raw = conn.Query<byte[]>("SELECT ciphertext FROM chat_provider_key").Single();
        Encoding.UTF8.GetString(raw).Should().NotContain("zai-very");
    }
}
