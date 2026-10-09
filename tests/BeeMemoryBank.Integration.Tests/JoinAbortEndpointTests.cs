using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// <c>POST /api/join/abort</c>: a joiner whose join failed after the host wrote its row takes that row back. The host
/// must remove only the never-synced row of that attempt, only for a caller that proves the master password, and answer
/// the same way when asked twice.
/// </summary>
[Collection(ProcessWideRateLimiterCollection.Name)]
public class JoinAbortEndpointTests : IAsyncLifetime
{
    private const string Password = "joinAbortPassword123";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private BmbWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new BmbWebApplicationFactory();
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync("AbortHost", Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private sealed record Attempt(Guid NodeId, byte[] PublicKey)
    {
        public static Attempt New() => new(Guid.NewGuid(), Ed25519Signer.GenerateKeyPair().publicKey);
    }

    private Task<HttpResponseMessage> JoinAsync(Attempt a, string? password = null, HttpClient? client = null) =>
        (client ?? _client).PostAsJsonAsync("/api/join", new
        {
            masterPassword = password ?? Password,
            nodeId = a.NodeId,
            displayName = "Joiner-" + a.NodeId.ToString("N")[..6],
            ed25519PublicKeyB64 = Convert.ToBase64String(a.PublicKey),
            apiAddress = (string?)null
        }, JsonOpts);

    private Task<HttpResponseMessage> AbortAsync(Attempt a, string? password = null, byte[]? publicKey = null, Guid? nodeId = null, HttpClient? client = null) =>
        (client ?? _client).PostAsJsonAsync("/api/join/abort", new
        {
            masterPassword = password ?? Password,
            nodeId = nodeId ?? a.NodeId,
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey ?? a.PublicKey)
        }, JsonOpts);

    private async Task<WhitelistEntry?> RowAsync(Guid nodeId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(nodeId, includeDeleted: true);
    }

    private async Task<string> StatusOfAsync(Guid nodeId) => (await RowAsync(nodeId))?.Status ?? "none";

    [Fact]
    public async Task Abort_RemovesTheRowTheJoinMade_AndTheMeshHearsARevoke()
    {
        var a = Attempt.New();
        (await JoinAsync(a)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(a.NodeId)).Should().Be("A");

        var resp = await AbortAsync(a);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("aborted").GetBoolean().Should().BeTrue();
        body.GetProperty("removed").GetBoolean().Should().BeTrue();
        (await StatusOfAsync(a.NodeId)).Should().Be("R");

        using var scope = _factory.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0, 100);
        events.Should().Contain(e => e.EventType == EventTypes.WhitelistRevoke && e.Payload.Contains(a.NodeId.ToString()),
            "the add went to the mesh, so the removal has to as well");

        var active = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
        active.Should().NotContain(e => e.NodeId == a.NodeId, "compaction no longer waits for a peer nobody holds the key of");
    }

    [Fact]
    public async Task Abort_Twice_IsASuccessBothTimes()
    {
        var a = Attempt.New();
        (await JoinAsync(a)).EnsureSuccessStatusCode();

        (await AbortAsync(a)).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await AbortAsync(a);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("removed").GetBoolean()
            .Should().BeFalse("there was nothing left to remove");
    }

    [Fact]
    public async Task Abort_ForAJoinThatNeverWroteARow_IsASuccess_AndRemovesNothing()
    {
        var a = Attempt.New();

        var resp = await AbortAsync(a);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("removed").GetBoolean().Should().BeFalse();
        (await StatusOfAsync(a.NodeId)).Should().Be("none");
    }

    [Fact]
    public async Task Abort_WithAWrongPassword_Is401_AndTheRowStays()
    {
        var a = Attempt.New();
        (await JoinAsync(a)).EnsureSuccessStatusCode();

        var resp = await AbortAsync(a, password: "not-the-password");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain(Password);
        (await StatusOfAsync(a.NodeId)).Should().Be("A");
    }

    [Fact]
    public async Task Abort_ForAPeerThatHasSynced_IsRefused_AndTheRowStays()
    {
        var a = Attempt.New();
        (await JoinAsync(a)).EnsureSuccessStatusCode();
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISyncPushPositionRepository>().UpsertAsync(
                new SyncPushPosition { RemoteNodeId = a.NodeId, LastPushedSeq = 3, PushedAt = DateTime.UtcNow });

        var resp = await AbortAsync(a);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StatusOfAsync(a.NodeId)).Should().Be("A", "a device that has synced is a member; only an admin revokes it");
    }

    [Fact]
    public async Task Abort_ForAPeerTheHostHasPulledFrom_IsRefused_AndTheRowStays()
    {
        var a = Attempt.New();
        (await JoinAsync(a)).EnsureSuccessStatusCode();
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().UpsertAsync(
                new SyncPosition { RemoteNodeId = a.NodeId, LastSequenceNum = 3, UpdatedAt = DateTime.UtcNow });

        (await AbortAsync(a)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StatusOfAsync(a.NodeId)).Should().Be("A", "events came from it: it is a member that did something");
    }

    [Fact]
    public async Task Abort_WithAnotherKeyThanTheJoinUsed_IsRefused_AndTheRowStays()
    {
        var a = Attempt.New();
        (await JoinAsync(a)).EnsureSuccessStatusCode();

        var resp = await AbortAsync(a, publicKey: Ed25519Signer.GenerateKeyPair().publicKey);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StatusOfAsync(a.NodeId)).Should().Be("A");
    }

    [Fact]
    public async Task Abort_ForARowThatIsNotFromAnAttemptInProgress_IsRefused()
    {
        // An old never-synced row (an admin may be looking at it) is not the row of a join that just failed.
        var old = Attempt.New();
        using (var scope = _factory.Services.CreateScope())
        {
            var created = DateTime.UtcNow.AddHours(-3);
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = old.NodeId, DisplayName = "Old", Ed25519PublicKey = old.PublicKey, Status = "A",
                CreatedAt = created, UpdatedAt = created
            });
        }

        (await AbortAsync(old)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StatusOfAsync(old.NodeId)).Should().Be("A");
    }

    [Fact]
    public async Task Abort_NeverTouchesTheHostItself_OrABlindNode_OrAnotherJoiner()
    {
        var host = await _client.GetFromJsonAsync<JsonElement>("/api/sync/identity", JsonOpts);
        var hostId = host.GetProperty("nodeId").GetGuid();
        var other = Attempt.New();
        (await JoinAsync(other)).EnsureSuccessStatusCode();
        var mine = Attempt.New();
        (await JoinAsync(mine)).EnsureSuccessStatusCode();

        (await AbortAsync(mine, nodeId: hostId)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AbortAsync(mine, nodeId: BlindNodeId.NewId())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AbortAsync(mine)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StatusOfAsync(other.NodeId)).Should().Be("A", "an abort names one node and removes that one");
        (await StatusOfAsync(mine.NodeId)).Should().Be("R");
        (await StatusOfAsync(hostId)).Should().NotBe("R");
    }

    [Fact]
    public async Task Abort_WithAMalformedBody_Is400()
    {
        var resp = await _client.PostAsJsonAsync("/api/join/abort", new
        {
            masterPassword = Password,
            nodeId = Guid.NewGuid(),
            ed25519PublicKeyB64 = "%%not base64%%"
        }, JsonOpts);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AJoinThatArrivesAfterItsAbort_IsRefused_AndWritesNoRow()
    {
        // The client gave up on a slow answer and aborted while the host was still working on the join.
        var a = Attempt.New();
        (await AbortAsync(a)).StatusCode.Should().Be(HttpStatusCode.OK);

        var late = await JoinAsync(a);

        late.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StatusOfAsync(a.NodeId)).Should().Be("none");
    }

    [Fact]
    public async Task Abort_OfOneJoin_LeavesTheOtherJoinsAlone_WhenTheyRace()
    {
        var attempts = Enumerable.Range(0, 4).Select(_ => Attempt.New()).ToList();

        var joins = await Task.WhenAll(attempts.Select(a => JoinAsync(a)));
        joins.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        var results = await Task.WhenAll(attempts.Take(2).Select(a => AbortAsync(a)));

        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        (await StatusOfAsync(attempts[0].NodeId)).Should().Be("R");
        (await StatusOfAsync(attempts[1].NodeId)).Should().Be("R");
        (await StatusOfAsync(attempts[2].NodeId)).Should().Be("A");
        (await StatusOfAsync(attempts[3].NodeId)).Should().Be("A");
    }

    [Fact]
    public async Task TwoJoinsOfTheSameNewId_AtOnce_BothSucceed_AndLeaveOneRow()
    {
        var a = Attempt.New();

        var both = await Task.WhenAll(JoinAsync(a), JoinAsync(a));

        both.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK, "the second one is a plain re-join of the same key");
        (await StatusOfAsync(a.NodeId)).Should().Be("A");
    }

    [Fact]
    public async Task AJoinAndItsAbort_AtOnce_LeaveEitherNoActiveRow_OrNoRowAtAll_NeverACrash()
    {
        for (var i = 0; i < 4; i++)
        {
            var a = Attempt.New();
            var join = JoinAsync(a);
            var abort = AbortAsync(a);
            var results = await Task.WhenAll(join, abort);

            results[1].StatusCode.Should().Be(HttpStatusCode.OK);
            results[0].StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
            (await StatusOfAsync(a.NodeId)).Should().NotBe("A", "whichever came first, no active row is left for an aborted join");
        }
    }

    [Fact]
    public async Task Abort_IsWrittenToTheAuditLog_WithoutAnySecret()
    {
        var a = Attempt.New();
        (await JoinAsync(a)).EnsureSuccessStatusCode();
        (await AbortAsync(a)).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>().CreateConnection();
        var rows = (await conn.QueryAsync<(string EntityId, string Action, string Details)>(
            "SELECT entity_id AS EntityId, action AS Action, details AS Details FROM tbl_audit_log WHERE action = 'join_aborted'")).ToList();

        rows.Should().ContainSingle();
        rows[0].EntityId.Should().Be(a.NodeId.ToString());
        rows[0].Details.Should().NotContain(Password).And.NotContain(Convert.ToBase64String(a.PublicKey));
    }

    [Fact]
    public async Task Abort_FromAKeylessCaller_IsReachable_ButNeedsThePassword_AndIsRateLimited()
    {
        RateLimitMiddleware.ResetForTests();
        using var keyless = _factory.Server.CreateClient();
        var a = Attempt.New();

        for (var i = 0; i < 5; i++)
            (await AbortAsync(a, password: "wrong-" + i, client: keyless)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await AbortAsync(a, password: "wrong-6", client: keyless)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        RateLimitMiddleware.ResetForTests();
    }
}
