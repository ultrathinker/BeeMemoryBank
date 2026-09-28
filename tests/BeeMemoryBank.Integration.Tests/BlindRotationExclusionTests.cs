using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Plan 3.3: a DEK rotation never seals the new key for a blind node — decided by the mark in its
/// id, so it holds even for a row that arrived through an event from a build that knew nothing of
/// blind nodes.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class BlindRotationExclusionTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string Password = "blindRotationPwd1!";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        (await _client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password }))
            .EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Rotation_SealsNoEnvelopeForABlindId_WhateverItsRowSays()
    {
        // The row comes from a whitelist_add in the pre-blind payload shape, relayed from a
        // superadmin hub: active, superadmin, valid key — nothing in it says "blind".
        var (hubPub, hubKey) = Ed25519Signer.GenerateKeyPair();
        var hubId = Guid.NewGuid();
        var whitelist = _factory.Services.GetRequiredService<IWhitelistRepository>();
        await whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = hubId, DisplayName = "Hub", Ed25519PublicKey = hubPub, Status = "A",
            IsSuperadmin = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var (blindPub, _) = Ed25519Signer.GenerateKeyPair();
        var blindId = BlindNodeId.NewId();
        var legacyAdd = $$"""
            {"node_id":"{{blindId}}","display_name":"Blind","public_key":"{{Convert.ToBase64String(blindPub)}}",
             "api_address":"https://blind.lan:5610","can_generate_embeddings":false,"is_superadmin":true}
            """;
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = hubId, LamportTs = 3, EventType = EventTypes.WhitelistAdd,
            Payload = legacyAdd, ProtocolVersion = 2, CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(hubKey, EventSignature.BuildPayload(evt));
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(evt);
        (await whitelist.GetByNodeIdAsync(blindId)).Should().NotBeNull("the add itself must apply");

        var proposeResp = await _client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password });
        proposeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var commitEventId = (await proposeResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("commitEventId").GetGuid();

        using var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        var payloadJson = await conn.ExecuteScalarAsync<string>(
            "SELECT payload FROM tbl_event WHERE event_id = @id COLLATE NOCASE AND event_type = 'dek_rotation_commit'",
            new { id = commitEventId.ToString() });
        using var doc = JsonDocument.Parse(payloadJson!);
        var peers = doc.RootElement.GetProperty("dek_envelopes").GetProperty("peers");

        peers.TryGetProperty(blindId.ToString().ToUpperInvariant(), out _)
            .Should().BeFalse("a blind node must never receive the master DEK");
        peers.TryGetProperty(hubId.ToString().ToUpperInvariant(), out _)
            .Should().BeTrue("an ordinary peer with an equally valid row still gets its envelope");
    }
}
