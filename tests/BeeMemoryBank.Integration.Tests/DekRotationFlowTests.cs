using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

// TODO peer auto-accept integration test deferred — needs multi-node mock
[Collection(HeavyOperationCollection.Name)]
public class DekRotationFlowTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string Password = "rotationTestPwd1!";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);

        var loginResp = await _client.PostAsJsonAsync("/api/session/login",
            new { username = "admin", password = Password });
        loginResp.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ProposeAndAccept_ChangesEpochAndReWraps()
    {
        // Create 3 articles
        var articleIds = new List<Guid>();
        for (int i = 0; i < 3; i++)
        {
            var create = await _client.PostAsJsonAsync("/api/articles", new
            {
                title = $"Rotation Article {i}",
                treePath = "/RotationTests",
                content = $"Secret content {i}"
            });
            create.StatusCode.Should().Be(HttpStatusCode.Created);
            var article = await create.Content.ReadFromJsonAsync<ArticleResponse>();
            articleIds.Add(article!.Id);
        }

        // Record the original encrypted_dek of the first article
        byte[] originalEncryptedDek;
        var connFactory = _factory.Services.GetRequiredService<DbConnectionFactory>();
        using (var conn = connFactory.CreateConnection())
        {
            var row = await conn.QuerySingleAsync<dynamic>(
                "SELECT encrypted_dek FROM tbl_article_body WHERE article_id = @id COLLATE NOCASE",
                new { id = articleIds[0].ToString() });
            originalEncryptedDek = (byte[])row.encrypted_dek;
        }

        // Get original epoch
        int originalEpoch;
        using (var conn = connFactory.CreateConnection())
        {
            originalEpoch = await conn.ExecuteScalarAsync<int>("SELECT dek_epoch FROM tbl_node_identity");
        }

        // Propose rotation
        var proposeResp = await _client.PostAsJsonAsync("/api/dek-rotation/propose",
            new { masterPassword = Password });
        proposeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var proposeBody = await proposeResp.Content.ReadFromJsonAsync<JsonElement>();
        var commitEventId = proposeBody.GetProperty("commitEventId").GetGuid().ToString();

        // ADR 0006: the rotation is confidential. Read the commit event's payload straight from
        // tbl_event (before accept — the initiator compacts right after) and confirm it ships the
        // per-peer envelopes and OMITS the wrap-under-old-DEK field entirely.
        using (var conn = connFactory.CreateConnection())
        {
            var payloadJson = await conn.ExecuteScalarAsync<string>(
                "SELECT payload FROM tbl_event WHERE event_id = @id COLLATE NOCASE AND event_type = 'dek_rotation_commit'",
                new { id = commitEventId });
            payloadJson.Should().NotBeNullOrEmpty();
            using var payloadDoc = JsonDocument.Parse(payloadJson!);
            payloadDoc.RootElement.TryGetProperty("encrypted_new_dek", out _)
                .Should().BeFalse("a confidential rotation must not ship the DEK wrapped under the old DEK");
            payloadDoc.RootElement.TryGetProperty("iv", out _).Should().BeFalse();
            payloadDoc.RootElement.TryGetProperty("dek_envelopes", out var env).Should().BeTrue();
            env.GetProperty("peers").EnumerateObject().Should().NotBeEmpty("the initiator gets its own envelope");
        }

        // Accept rotation
        var acceptResp = await _client.PostAsJsonAsync("/api/dek-rotation/accept",
            new { commitEventId, masterPassword = Password });
        acceptResp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Poll progress until Completed or timeout
        var completed = await PollProgressAsync(
            step => step == DekRotationFlowStep.Completed,
            timeout: TimeSpan.FromSeconds(60));
        completed.Should().BeTrue("rotation should complete within timeout");

        // Assert: epoch incremented
        int newEpoch;
        using (var conn = connFactory.CreateConnection())
        {
            newEpoch = await conn.ExecuteScalarAsync<int>("SELECT dek_epoch FROM tbl_node_identity");
        }
        newEpoch.Should().Be(originalEpoch + 1);

        // Assert: encrypted_dek changed
        byte[] newEncryptedDek;
        using (var conn = connFactory.CreateConnection())
        {
            var row = await conn.QuerySingleAsync<dynamic>(
                "SELECT encrypted_dek FROM tbl_article_body WHERE article_id = @id COLLATE NOCASE",
                new { id = articleIds[0].ToString() });
            newEncryptedDek = (byte[])row.encrypted_dek;
        }
        newEncryptedDek.Should().NotEqual(originalEncryptedDek);

        // Assert: article still readable with same content
        var contentResp = await _client.GetAsync($"/api/articles/{articleIds[0]}/content");
        contentResp.EnsureSuccessStatusCode();
        var contentBody = await contentResp.Content.ReadFromJsonAsync<ArticleContentResponse>();
        contentBody!.Content.Should().Be("Secret content 0");

        // Assert: state == APPLIED, applied_at != null
        using (var conn = connFactory.CreateConnection())
        {
            var stateRow = await conn.QuerySingleAsync<dynamic>(
                "SELECT state, applied_at FROM tbl_dek_rotation_state WHERE event_id = @eventId",
                new { eventId = commitEventId });
            ((string)stateRow.state).Should().Be("APPLIED");
            ((string?)stateRow.applied_at).Should().NotBeNull();
        }

        // Assert: only 1 key slot remains (initiator)
        using (var conn = connFactory.CreateConnection())
        {
            var slotCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_key_slot");
            slotCount.Should().Be(1);
        }
    }

    /// <summary>
    /// ADR 0006 security property: a rotation is confidential against a party that holds only the
    /// OLD DEK. With node B enumerated in the active whitelist, rotating on A seals the new DEK once
    /// per active peer as an X25519 envelope and omits the wrap-under-old-DEK field. B, holding only
    /// its own identity key, opens its envelope and recovers the exact new master DEK — while the
    /// event carries no <c>encrypted_new_dek</c> for an old-DEK holder to unwrap at all.
    /// </summary>
    [Fact]
    public async Task ConfidentialRotation_PeerOpensItsEnvelope_ButOldDekAloneCannot()
    {
        // A stand-in for a second cluster node: a real Ed25519 identity in the active whitelist.
        var (peerPub, peerSeed) = Ed25519Signer.GenerateKeyPair();
        var peerNodeId = Guid.NewGuid();

        var whitelistRepo = _factory.Services.GetRequiredService<IWhitelistRepository>();
        await whitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = peerNodeId,
            DisplayName = "NodeB",
            Ed25519PublicKey = peerPub,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var connFactory = _factory.Services.GetRequiredService<DbConnectionFactory>();

        // Seed an article so the "still readable after rotation" invariant is exercised too.
        var create = await _client.PostAsJsonAsync("/api/articles", new
        {
            title = "Confidential",
            treePath = "/RotationTests",
            content = "top secret"
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var articleId = (await create.Content.ReadFromJsonAsync<ArticleResponse>())!.Id;

        int originalEpoch;
        using (var conn = connFactory.CreateConnection())
            originalEpoch = await conn.ExecuteScalarAsync<int>("SELECT dek_epoch FROM tbl_node_identity");

        // Propose, then capture the commit envelope BEFORE accept (the initiator compacts after).
        var proposeResp = await _client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password });
        proposeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var commitEventId = (await proposeResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("commitEventId").GetGuid();

        string ephemeralPub, wrappedB64, nonceB64;
        using (var conn = connFactory.CreateConnection())
        {
            var payloadJson = await conn.ExecuteScalarAsync<string>(
                "SELECT payload FROM tbl_event WHERE event_id = @id COLLATE NOCASE AND event_type = 'dek_rotation_commit'",
                new { id = commitEventId.ToString() });
            using var doc = JsonDocument.Parse(payloadJson!);
            var root = doc.RootElement;

            root.TryGetProperty("encrypted_new_dek", out _).Should().BeFalse(
                "there must be nothing for a holder of only the old DEK to unwrap");

            var env = root.GetProperty("dek_envelopes");
            ephemeralPub = env.GetProperty("ephemeral_pub").GetString()!;
            var peers = env.GetProperty("peers");

            // Both the initiator's own node and the enumerated peer B received an envelope.
            var localNodeId = await conn.ExecuteScalarAsync<string>("SELECT node_id FROM tbl_node_identity");
            peers.TryGetProperty(localNodeId!.ToUpperInvariant(), out _).Should().BeTrue("the initiator gets its own envelope");
            peers.TryGetProperty(peerNodeId.ToString().ToUpperInvariant(), out var boxB).Should().BeTrue(
                "the active peer must be enumerated and sealed for");

            wrappedB64 = boxB.GetProperty("wrapped").GetString()!;
            nonceB64 = boxB.GetProperty("nonce").GetString()!;
        }

        // Accept and let the rotation complete.
        var acceptResp = await _client.PostAsJsonAsync("/api/dek-rotation/accept",
            new { commitEventId = commitEventId.ToString(), masterPassword = Password });
        acceptResp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await PollProgressAsync(step => step == DekRotationFlowStep.Completed, TimeSpan.FromSeconds(60)))
            .Should().BeTrue("rotation should complete");

        using (var conn = connFactory.CreateConnection())
            (await conn.ExecuteScalarAsync<int>("SELECT dek_epoch FROM tbl_node_identity"))
                .Should().Be(originalEpoch + 1);

        // B, holding ONLY its identity seed, opens its envelope and recovers the exact new master DEK.
        var session = _factory.Services.GetRequiredService<SessionService>();
        var newMasterDek = session.GetMasterDek();
        try
        {
            var opened = DekEnvelope.Open(ephemeralPub, wrappedB64, nonceB64, commitEventId, peerNodeId, peerSeed);
            opened.Should().Equal(newMasterDek, "peer B derives the same new DEK the initiator rotated to");
        }
        finally
        {
            Array.Clear(newMasterDek);
        }

        // A stranger — a valid identity that was NOT in the active set — cannot open B's envelope.
        var (_, strangerSeed) = Ed25519Signer.GenerateKeyPair();
        var strangerAttempt = () => DekEnvelope.Open(ephemeralPub, wrappedB64, nonceB64, commitEventId, peerNodeId, strangerSeed);
        strangerAttempt.Should().Throw<System.Security.Cryptography.CryptographicException>(
            "only B's identity key opens B's envelope");

        // The article is still readable under the new DEK.
        var contentResp = await _client.GetAsync($"/api/articles/{articleId}/content");
        contentResp.EnsureSuccessStatusCode();
        (await contentResp.Content.ReadFromJsonAsync<ArticleContentResponse>())!.Content.Should().Be("top secret");
    }

    /// <summary>
    /// Confidentiality guardrail (adversarial): a REVOKED peer must receive no envelope it could
    /// open. Recipients come from GetAllActiveAsync(), so a status='R' peer is enumerated out — this
    /// pins that property so a future change to the recipient set cannot silently start sealing the
    /// new DEK for a revoked node.
    /// </summary>
    [Fact]
    public async Task ConfidentialRotation_RevokedPeer_GetsNoEnvelope()
    {
        var (revokedPub, _) = Ed25519Signer.GenerateKeyPair();
        var revokedNodeId = Guid.NewGuid();
        var whitelistRepo = _factory.Services.GetRequiredService<IWhitelistRepository>();
        await whitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = revokedNodeId,
            DisplayName = "RevokedNode",
            Ed25519PublicKey = revokedPub,
            Status = "R", // revoked — must not be a rotation recipient
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var connFactory = _factory.Services.GetRequiredService<DbConnectionFactory>();
        var proposeResp = await _client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password });
        proposeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var commitEventId = (await proposeResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("commitEventId").GetGuid();

        using var conn = connFactory.CreateConnection();
        var payloadJson = await conn.ExecuteScalarAsync<string>(
            "SELECT payload FROM tbl_event WHERE event_id = @id COLLATE NOCASE AND event_type = 'dek_rotation_commit'",
            new { id = commitEventId.ToString() });
        using var doc = JsonDocument.Parse(payloadJson!);
        var peers = doc.RootElement.GetProperty("dek_envelopes").GetProperty("peers");

        peers.TryGetProperty(revokedNodeId.ToString().ToUpperInvariant(), out _)
            .Should().BeFalse("a revoked peer must not receive an openable envelope");
        var localNodeId = await conn.ExecuteScalarAsync<string>("SELECT node_id FROM tbl_node_identity");
        peers.TryGetProperty(localNodeId!.ToUpperInvariant(), out _)
            .Should().BeTrue("the initiator still gets its own envelope");
    }

    /// <summary>
    /// Availability guardrail (adversarial, P1): an active peer whose stored Ed25519 key is not a
    /// valid curve point (corrupt row, or a maliciously planted key) is EXCLUDED from the rotation
    /// with a log — it must not (a) abort the whole rotation for every healthy peer, nor (b) be
    /// sealed an envelope from an off-curve/small-order key. The rotation still completes.
    /// </summary>
    [Fact]
    public async Task ConfidentialRotation_BrokenPeerKey_IsExcluded_AndRotationCompletes()
    {
        var brokenNodeId = Guid.NewGuid();
        var whitelistRepo = _factory.Services.GetRequiredService<IWhitelistRepository>();
        await whitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = brokenNodeId,
            DisplayName = "BrokenKeyNode",
            Ed25519PublicKey = new byte[32], // small-order / non-subgroup point — rejected by validation
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var connFactory = _factory.Services.GetRequiredService<DbConnectionFactory>();
        var proposeResp = await _client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password });
        proposeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var commitEventId = (await proposeResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("commitEventId").GetGuid();

        using (var conn = connFactory.CreateConnection())
        {
            var payloadJson = await conn.ExecuteScalarAsync<string>(
                "SELECT payload FROM tbl_event WHERE event_id = @id COLLATE NOCASE AND event_type = 'dek_rotation_commit'",
                new { id = commitEventId.ToString() });
            using var doc = JsonDocument.Parse(payloadJson!);
            var peers = doc.RootElement.GetProperty("dek_envelopes").GetProperty("peers");
            peers.TryGetProperty(brokenNodeId.ToString().ToUpperInvariant(), out _)
                .Should().BeFalse("a peer with an invalid Ed25519 key must be excluded, not sealed for");
        }

        var acceptResp = await _client.PostAsJsonAsync("/api/dek-rotation/accept",
            new { commitEventId = commitEventId.ToString(), masterPassword = Password });
        acceptResp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await PollProgressAsync(step => step == DekRotationFlowStep.Completed, TimeSpan.FromSeconds(60)))
            .Should().BeTrue("one broken peer key must not abort the rotation for everyone else");
    }

    /// <summary>
    /// Regression: rotation used to build the unwrap AAD for tbl_article_version /
    /// tbl_conflict_version from the ROW's GUID primary key instead of the parent article_id.
    /// Those rows carry a byte-copy of the article body's DEK, wrapped under the article's AAD,
    /// so every version row made the rewrap throw AuthenticationTagMismatch and roll the whole
    /// rotation back — i.e. rotation was impossible on any vault whose articles had ever been
    /// edited, which is every real vault. The pre-existing rotation tests all rotated over
    /// freshly-created articles and so had no version rows at all.
    /// </summary>
    [Fact]
    public async Task RotationAfterAnArticleWasEdited_CompletesAndKeepsHistoryReadable()
    {
        var create = await _client.PostAsJsonAsync("/api/articles", new
        {
            title = "Edited Before Rotation",
            treePath = "/RotationTests",
            content = "original body"
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var articleId = (await create.Content.ReadFromJsonAsync<ArticleResponse>())!.Id;

        // Two edits → two version rows, the exact state that used to break rotation.
        foreach (var body in new[] { "second body", "third body" })
        {
            var edit = await _client.PutAsJsonAsync($"/api/articles/{articleId}", new { content = body });
            edit.EnsureSuccessStatusCode();
        }

        var connFactory = _factory.Services.GetRequiredService<DbConnectionFactory>();
        using (var conn = connFactory.CreateConnection())
        {
            var versionCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM tbl_article_version WHERE article_id = @id COLLATE NOCASE",
                new { id = articleId.ToString() });
            versionCount.Should().Be(2, "the test premise is that version rows exist before rotating");
        }

        (await RotateAsync()).Should().BeTrue("rotation must survive a vault that has version rows");

        // Current body still readable under the new DEK...
        var contentResp = await _client.GetAsync($"/api/articles/{articleId}/content");
        contentResp.EnsureSuccessStatusCode();
        (await contentResp.Content.ReadFromJsonAsync<ArticleContentResponse>())!
            .Content.Should().Be("third body");

        // ...and so is history, which is what the broken AAD corrupted the path to. Version 1 is
        // the snapshot of the ORIGINAL body taken by the first edit.
        var versionResp = await _client.GetAsync($"/api/articles/{articleId}/versions/1");
        versionResp.EnsureSuccessStatusCode();
        var versionBody = await versionResp.Content.ReadFromJsonAsync<JsonElement>();
        versionBody.GetProperty("content").GetString().Should().Be("original body");
    }

    /// <summary>
    /// Regression: the semantic-search projection matrix is sealed directly under the master DEK,
    /// but rotation only re-wrapped the four per-row-DEK tables. A completed rotation therefore
    /// left the matrix under the retired DEK, and every semantic query plus every background
    /// re-embed threw CryptographicException from then on, permanently.
    /// </summary>
    [Fact]
    public async Task Rotation_ReWrapsTheProjectionMatrix()
    {
        var connFactory = _factory.Services.GetRequiredService<DbConnectionFactory>();
        var session = _factory.Services.GetRequiredService<SessionService>();

        // Seed a matrix-shaped payload sealed under the CURRENT master DEK. Wrapped directly here
        // rather than via EmbeddingProjectionService so the test doesn't need the ONNX model file
        // (gitignored, ~87MB) — the rewrap path only cares about the wrap format, not the contents.
        var plaintextMatrix = new byte[4096];
        Random.Shared.NextBytes(plaintextMatrix);

        var dekBefore = session.GetMasterDek();
        byte[] encBefore, ivBefore;
        try
        {
            (encBefore, ivBefore) = DekManager.WrapDek(plaintextMatrix, dekBefore);
        }
        finally
        {
            Array.Clear(dekBefore);
        }

        using (var conn = connFactory.CreateConnection())
        {
            await conn.ExecuteAsync("DELETE FROM tbl_projection_matrix");
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_projection_matrix (encrypted_matrix, iv, created_at)
                  VALUES (@enc, @iv, @createdAt)",
                new { enc = encBefore, iv = ivBefore, createdAt = DateTime.UtcNow.ToString("O") });
        }

        (await RotateAsync()).Should().BeTrue("rotation should complete");

        // The stored blob must now open under the NEW master DEK and still hold the same matrix.
        byte[] encAfter, ivAfter;
        using (var conn = connFactory.CreateConnection())
        {
            var row = await conn.QuerySingleAsync<dynamic>(
                "SELECT encrypted_matrix, iv FROM tbl_projection_matrix");
            encAfter = (byte[])row.encrypted_matrix;
            ivAfter = (byte[])row.iv;
        }
        encAfter.Should().NotEqual(encBefore, "the matrix must actually be re-wrapped, not left alone");

        var dekAfter = session.GetMasterDek();
        try
        {
            var recovered = DekManager.UnwrapVersioned(encAfter, ivAfter, dekAfter);
            recovered.Should().Equal(plaintextMatrix);
        }
        finally
        {
            Array.Clear(dekAfter);
        }
    }

    /// <summary>
    /// Regression for C1: a confidential rotation must be self-perpetuating. The node's own Ed25519
    /// identity seed is wrapped under the master DEK (v1); ResolveNewDek decrypts it to open this
    /// node's envelope, and every event signed afterwards decrypts it under the CURRENT DEK. If the
    /// rotation does not re-wrap the seed to the new DEK, the FIRST rotation still works (the seed is
    /// under the key it was sealed with) but the SECOND wedges — the seed is under the pre-rotation-1
    /// DEK while accept now tries to open it under the rotation-1 DEK. This rotates twice and proves
    /// the seed opens under the current DEK after each.
    /// </summary>
    [Fact]
    public async Task TwoRotationsInARow_KeepTheIdentitySeedUsable()
    {
        var create = await _client.PostAsJsonAsync("/api/articles", new
        {
            title = "Survives Two Rotations",
            treePath = "/RotationTests",
            content = "body that must stay readable"
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var articleId = (await create.Content.ReadFromJsonAsync<ArticleResponse>())!.Id;

        var connFactory = _factory.Services.GetRequiredService<DbConnectionFactory>();
        var session = _factory.Services.GetRequiredService<SessionService>();

        // Premise: the node identity must be v1 (DEK-wrapped seed), or this test exercises nothing —
        // v0 plaintext seeds do not depend on the DEK and were never at risk.
        (Guid nodeId, byte[] pk, byte[]? iv, int v) ReadIdentity()
        {
            using var conn = connFactory.CreateConnection();
            var row = conn.QuerySingle<dynamic>(
                @"SELECT node_id AS NodeId, ed25519_private_key AS Pk,
                         ed25519_private_key_iv AS Iv, ed25519_private_key_v AS V
                    FROM tbl_node_identity LIMIT 1");
            return (Guid.Parse((string)row.NodeId), (byte[])row.Pk, row.Iv as byte[], (int)(long)row.V);
        }

        void AssertSeedOpensUnderCurrentDek(string when)
        {
            var (nodeId, pk, iv, v) = ReadIdentity();
            v.Should().Be(1, $"identity should stay v1 {when}");
            var dek = session.GetMasterDek();
            try
            {
                // Throws CryptographicException if the seed is not sealed under the current DEK —
                // that is exactly the C1 brick, surfaced as a test failure instead of a wedged node.
                var seed = NodeIdentityCrypto.GetDecryptedPrivateKey(pk, iv, v, nodeId, dek);
                seed.Length.Should().Be(32, $"the identity seed must open under the current DEK {when}");
                Array.Clear(seed);
            }
            finally
            {
                Array.Clear(dek);
            }
        }

        ReadIdentity().v.Should().Be(1, "test premise: a freshly initialized node stores a v1 identity seed");
        AssertSeedOpensUnderCurrentDek("before any rotation");

        (await RotateAsync()).Should().BeTrue("the first rotation must complete");
        AssertSeedOpensUnderCurrentDek("after the first rotation");

        (await RotateAsync()).Should().BeTrue(
            "the SECOND rotation must complete — this is the C1 regression; without re-wrapping the "
            + "identity seed it fails to open this node's envelope under the rotation-1 DEK");
        AssertSeedOpensUnderCurrentDek("after the second rotation");

        // The article body must still open after two rotations.
        var contentResp = await _client.GetAsync($"/api/articles/{articleId}/content");
        contentResp.EnsureSuccessStatusCode();
        (await contentResp.Content.ReadFromJsonAsync<ArticleContentResponse>())!
            .Content.Should().Be("body that must stay readable");
    }

    /// <summary>Proposes and accepts a rotation; returns whether it reached Completed.</summary>
    private async Task<bool> RotateAsync()
    {
        var proposeResp = await _client.PostAsJsonAsync("/api/dek-rotation/propose",
            new { masterPassword = Password });
        proposeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var commitEventId = (await proposeResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("commitEventId").GetGuid().ToString();

        var acceptResp = await _client.PostAsJsonAsync("/api/dek-rotation/accept",
            new { commitEventId, masterPassword = Password });
        acceptResp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // 60s, not the 15s the older tests use: these two rotate over a vault that also has
        // version rows and a projection matrix, and they run while the rest of the suite saturates
        // the machine. A rotation that actually works finishes in about a second, so the wider
        // bound only buys tolerance for a loaded runner — it never masks a real failure, which
        // surfaces as the Failed step rather than as a timeout.
        return await PollProgressAsync(
            step => step == DekRotationFlowStep.Completed,
            timeout: TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task LazyRewrap_StillWalksTheChainAfterTheCommitEventIsCompactedAway()
    {
        // The lockout this closes. A peer that auto-applies a rotation keeps its users' key slots
        // wrapped under the OLD DEK and re-wraps each one lazily at that user's next login, by
        // walking the Applied rotations and unwrapping the next DEK with the previous one. Each
        // link was read out of the dek_rotation_commit event in tbl_event — and those rows do not
        // last: compaction deletes everything at or below the checkpoint, and the initiator
        // compacts automatically right after rotating. Once the row was gone the walk could not
        // start, reachedTarget stayed false, and that user could never unlock that node again.
        //
        // Deleting the commit event outright is exactly what compaction does to it.
        var connFactory = _factory.Services.GetRequiredService<DbConnectionFactory>();
        var slotRepo = _factory.Services.GetRequiredService<IKeySlotRepository>();
        var session = _factory.Services.GetRequiredService<SessionService>();

        // Capture the PRE-rotation DEK: it is what a peer's untouched slot still unwraps to, and
        // therefore where the walk has to start from.
        var slotBefore = (await slotRepo.GetAllAsync()).Single(x => x.SlotType == "user");
        var kek = KeyDerivation.DeriveKek(
            Password, slotBefore.Salt!,
            slotBefore.ArgonMemory!.Value, slotBefore.ArgonIterations!.Value, slotBefore.ArgonParallelism!.Value);
        var preRotationDek = MasterKeyManager.UnwrapMasterDek(slotBefore.EncryptedMasterDek, slotBefore.IV, kek);

        var proposeResp = await _client.PostAsJsonAsync("/api/dek-rotation/propose",
            new { masterPassword = Password });
        proposeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var proposeBody = await proposeResp.Content.ReadFromJsonAsync<JsonElement>();
        var commitEventId = proposeBody.GetProperty("commitEventId").GetGuid().ToString();

        var acceptResp = await _client.PostAsJsonAsync("/api/dek-rotation/accept",
            new { commitEventId, masterPassword = Password });
        acceptResp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        (await PollProgressAsync(step => step == DekRotationFlowStep.Completed,
            timeout: TimeSpan.FromSeconds(60)))
            .Should().BeTrue("rotation should complete within timeout");

        // The link is on the state row, written in the same statement that marked the rotation
        // Applied — so a row claiming Applied can never be one the walk cannot get past.
        byte[] currentSentinel;
        using (var conn = connFactory.CreateConnection())
        {
            var row = await conn.QuerySingleAsync<ChainRow>(
                @"SELECT chain_encrypted_new_dek AS Enc, chain_iv AS Iv, state AS State
                  FROM tbl_dek_rotation_state WHERE event_id = @id COLLATE NOCASE",
                new { id = commitEventId });

            row.State.Should().Be("APPLIED");
            row.Enc.Should().NotBeNullOrEmpty("the chain link must be stored locally, not only in the event log");
            row.Iv.Should().NotBeNullOrEmpty();

            // BLOB, not text — MasterKeyManager.ComputeSentinel writes raw bytes.
            currentSentinel = await conn.ExecuteScalarAsync<byte[]>(
                "SELECT sentinel_value FROM tbl_node_identity") ?? [];

            // Now do what compaction does to it.
            var deleted = await conn.ExecuteAsync(
                "DELETE FROM tbl_event WHERE event_id = @id COLLATE NOCASE", new { id = commitEventId });
            deleted.Should().Be(1, "the commit event must actually have been there to delete");
        }

        currentSentinel.Should().NotBeEmpty();

        // A slot still holding the pre-rotation DEK must reach the current sentinel with the event
        // log gone. Before migration 020 this returned Success=false and the user was locked out.
        var rewrap = _factory.Services.GetRequiredService<ILazySlotRewrapService>();
        var slotAfter = (await slotRepo.GetAllAsync()).Single(x => x.SlotType == "user");

        LazyRewrapResult result;
        try
        {
            result = await rewrap.TryRewrapAsync(slotAfter, kek, preRotationDek, currentSentinel);
        }
        finally
        {
            Array.Clear(preRotationDek);
            Array.Clear(kek);
        }

        result.Success.Should().BeTrue(
            "the rotation chain must stay walkable from the local state row after compaction has " +
            "removed the commit event it originally came from");
    }

    private sealed class ChainRow
    {
        public string? Enc { get; set; }
        public string? Iv { get; set; }
        public string State { get; set; } = "";
    }

    [Fact]
    public async Task AcceptWithWrongPassword_DoesNotBrickNode()
    {
        // Propose with correct password
        var proposeResp = await _client.PostAsJsonAsync("/api/dek-rotation/propose",
            new { masterPassword = Password });
        proposeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var proposeBody = await proposeResp.Content.ReadFromJsonAsync<JsonElement>();
        var commitEventId = proposeBody.GetProperty("commitEventId").GetGuid().ToString();

        // Record original epoch
        var connFactory = _factory.Services.GetRequiredService<DbConnectionFactory>();
        int originalEpoch;
        using (var conn = connFactory.CreateConnection())
        {
            originalEpoch = await conn.ExecuteScalarAsync<int>("SELECT dek_epoch FROM tbl_node_identity");
        }

        // Accept with WRONG password — returns 202 (fire-and-forget)
        var acceptResp = await _client.PostAsJsonAsync("/api/dek-rotation/accept",
            new { commitEventId, masterPassword = "WRONG_PASSWORD_123!" });
        acceptResp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Poll until Failed
        var failed = await PollProgressAsync(
            step => step == DekRotationFlowStep.Failed,
            timeout: TimeSpan.FromSeconds(60));
        failed.Should().BeTrue("rotation should fail with wrong password");

        // Assert: epoch unchanged
        using (var conn = connFactory.CreateConnection())
        {
            var epoch = await conn.ExecuteScalarAsync<int>("SELECT dek_epoch FROM tbl_node_identity");
            epoch.Should().Be(originalEpoch);
        }

        // Assert: login with original password still works.
        //
        // Polled rather than asserted outright: the Failed progress step is published from inside
        // AcceptCommitCoreAsync, while maintenance mode is released by the finally one frame out
        // in AcceptCommitAsync. Between those two the node correctly answers 503, and a CI runner
        // is slow enough to land in that window — the point of this test is that the node comes
        // back, not that it comes back within zero milliseconds.
        var loginResp = await PollLoginAsync(TimeSpan.FromSeconds(60));
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DoubleProposeFailsWithConflict()
    {
        // First propose succeeds
        var propose1 = await _client.PostAsJsonAsync("/api/dek-rotation/propose",
            new { masterPassword = Password });
        propose1.StatusCode.Should().Be(HttpStatusCode.OK);
        var body1 = await propose1.Content.ReadFromJsonAsync<JsonElement>();
        var commitEventId = body1.GetProperty("commitEventId").GetGuid().ToString();

        // Second propose while first is in Committing state → 409. This is the end-to-end half of
        // ExceptionStatusMapTests: the endpoint reaches 409 because the service threw a
        // ConflictException, not because its message happened to contain "in progress".
        var propose2 = await _client.PostAsJsonAsync("/api/dek-rotation/propose",
            new { masterPassword = Password });
        propose2.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Accept the first one to clean up
        var acceptResp = await _client.PostAsJsonAsync("/api/dek-rotation/accept",
            new { commitEventId, masterPassword = Password });
        acceptResp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await PollProgressAsync(
            step => step == DekRotationFlowStep.Completed || step == DekRotationFlowStep.Failed,
            timeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Logs in, retrying while the node still reports maintenance mode (503). Any other status —
    /// including a genuine failure — is returned immediately so the caller's assertion sees it.
    /// </summary>
    private async Task<HttpResponseMessage> PollLoginAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        HttpResponseMessage resp;
        do
        {
            resp = await _client.PostAsJsonAsync("/api/session/login",
                new { username = "admin", password = Password });
            if (resp.StatusCode != HttpStatusCode.ServiceUnavailable)
                return resp;
            await Task.Delay(200);
        } while (DateTime.UtcNow < deadline);

        return resp;
    }

    private async Task<bool> PollProgressAsync(
        Func<DekRotationFlowStep, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var progressResp = await _client.GetAsync("/api/dek-rotation/progress");
            progressResp.EnsureSuccessStatusCode();
            var progress = await progressResp.Content.ReadFromJsonAsync<JsonElement>();
            var stepStr = progress.GetProperty("currentStep").GetString()!;
            var step = Enum.Parse<DekRotationFlowStep>(stepStr);
            if (predicate(step))
                return true;
            await Task.Delay(200);
        }
        return false;
    }
}
