using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
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
