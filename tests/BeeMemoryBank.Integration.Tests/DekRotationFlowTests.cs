using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

// TODO peer auto-accept integration test deferred — needs multi-node mock
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
            timeout: TimeSpan.FromSeconds(15));
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
            timeout: TimeSpan.FromSeconds(15));
        failed.Should().BeTrue("rotation should fail with wrong password");

        // Assert: epoch unchanged
        using (var conn = connFactory.CreateConnection())
        {
            var epoch = await conn.ExecuteScalarAsync<int>("SELECT dek_epoch FROM tbl_node_identity");
            epoch.Should().Be(originalEpoch);
        }

        // Assert: login with original password still works
        var loginResp = await _client.PostAsJsonAsync("/api/session/login",
            new { username = "admin", password = Password });
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

        // Second propose while first is in Committing state → 409
        var propose2 = await _client.PostAsJsonAsync("/api/dek-rotation/propose",
            new { masterPassword = Password });
        propose2.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Accept the first one to clean up
        var acceptResp = await _client.PostAsJsonAsync("/api/dek-rotation/accept",
            new { commitEventId, masterPassword = Password });
        acceptResp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await PollProgressAsync(
            step => step == DekRotationFlowStep.Completed || step == DekRotationFlowStep.Failed,
            timeout: TimeSpan.FromSeconds(15));
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
