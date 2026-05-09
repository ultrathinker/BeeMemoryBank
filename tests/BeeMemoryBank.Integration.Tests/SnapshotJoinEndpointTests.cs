using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

public class SnapshotJoinEndpointTests : IAsyncLifetime
{
    private BmbWebApplicationFactory _nodeA = null!;
    private BmbWebApplicationFactory _nodeB = null!;
    private HttpClient _clientA = null!;
    private HttpClient _clientB = null!;

    private const string MasterPassword = "snapshotJoinPassword";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _nodeA = new BmbWebApplicationFactory();
        _nodeB = new BmbWebApplicationFactory();

        await _nodeA.InitializeNodeAsync("NodeA", MasterPassword);
        _clientA = _nodeA.CreateClient();

        await UnlockAsync(_clientA, MasterPassword);

        await _nodeB.JoinNodeAsync(_clientA, "NodeB", MasterPassword);
        _clientB = _nodeB.CreateClient();

        await UnlockAsync(_clientB, MasterPassword);
    }

    public Task DisposeAsync()
    {
        _clientA.Dispose();
        _clientB.Dispose();
        _nodeA.Dispose();
        _nodeB.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Unauthorized_Returns401()
    {
        var resp = await _clientA.GetAsync("/api/sync/snapshot/for-join");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ValidToken_FreshNode_ReturnsSnapshot()
    {
        var token = await AuthNodeOnServerAsync(_nodeB, _clientA);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/sync/snapshot/for-join");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _clientA.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/gzip");

        var cpSeq = resp.Headers.Contains("X-BMB-Snapshot-CP-Seq")
            ? resp.Headers.GetValues("X-BMB-Snapshot-CP-Seq").First()
            : null;
        cpSeq.Should().NotBeNull();
        long.Parse(cpSeq!).Should().BeGreaterThan(0);

        var signature = resp.Headers.Contains("X-BMB-Snapshot-Signature")
            ? resp.Headers.GetValues("X-BMB-Snapshot-Signature").First()
            : null;
        signature.Should().NotBeNullOrEmpty();

        var producer = resp.Headers.Contains("X-BMB-Snapshot-Producer")
            ? resp.Headers.GetValues("X-BMB-Snapshot-Producer").First()
            : null;
        producer.Should().NotBeNullOrEmpty();

        var content = await resp.Content.ReadAsByteArrayAsync();
        content.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AlreadySynced_Returns409()
    {
        var token = await AuthNodeOnServerAsync(_nodeB, _clientA);

        using var scope = _nodeA.Services.CreateScope();
        var syncPosRepo = scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>();
        using var nodeScope = _nodeB.Services.CreateScope();
        var nodeRepo = nodeScope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var nodeBIdentity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException();

        await syncPosRepo.UpsertAsync(new BeeMemoryBank.Core.Models.SyncPosition
        {
            RemoteNodeId = nodeBIdentity.NodeId,
            LastSequenceNum = 1,
            UpdatedAt = DateTime.UtcNow
        });

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/sync/snapshot/for-join");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _clientA.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("error").GetString().Should().Be("ALREADY_SYNCED");
    }

    [Fact]
    public async Task ReturnsSignedFilteredSnapshot()
    {
        var token = await AuthNodeOnServerAsync(_nodeB, _clientA);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/sync/snapshot/for-join");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _clientA.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var content = await resp.Content.ReadAsByteArrayAsync();
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"bmb-join-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                await ExtractTarGzAsync(content, tempDir);

                var dbPath = Path.Combine(tempDir, "beememorybank.db");
                File.Exists(dbPath).Should().BeTrue();

                var cs = $"Data Source={dbPath}";
                using var conn = new SqliteConnection(cs);
                conn.Open();

                foreach (var secretTable in new[] { "tbl_node_identity", "tbl_session", "tbl_user", "tbl_key_slot" })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{secretTable}'";
                    var result = cmd.ExecuteScalar();
                    result.Should().BeNull($"secret table {secretTable} should not exist in filtered snapshot");
                }
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }
        finally
        {
            // tempDir cleaned up inside nested try/finally
        }
    }

    [Fact]
    public async Task CacheUsed_OnSecondRequest()
    {
        var token1 = await AuthNodeOnServerAsync(_nodeB, _clientA);

        using var req1 = new HttpRequestMessage(HttpMethod.Get, "/api/sync/snapshot/for-join");
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token1);
        var resp1 = await _clientA.SendAsync(req1);
        resp1.EnsureSuccessStatusCode();
        var filename1 = resp1.Content.Headers.ContentDisposition?.FileName;

        var token2 = await AuthNodeOnServerAsync(_nodeB, _clientA);

        using var req2 = new HttpRequestMessage(HttpMethod.Get, "/api/sync/snapshot/for-join");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token2);
        var resp2 = await _clientA.SendAsync(req2);
        resp2.EnsureSuccessStatusCode();
        var filename2 = resp2.Content.Headers.ContentDisposition?.FileName;

        filename1.Should().Be(filename2, "second request should serve cached snapshot");
    }

    private static async Task UnlockAsync(HttpClient client, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/session/unlock", new { Password = password });
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<string> AuthNodeOnServerAsync(
        BmbWebApplicationFactory clientNode, HttpClient server)
    {
        var challengeResp = await server.PostAsync("/api/sync/challenge", null);
        challengeResp.EnsureSuccessStatusCode();
        var challengeData = await challengeResp.Content.ReadFromJsonAsync<ChallengeDto>(JsonOpts)
            ?? throw new InvalidDataException();

        using var scope = clientNode.Services.CreateScope();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException();

        var challengeBytes = Convert.FromBase64String(challengeData.Challenge);
        var domainTag = "BMB-CHALLENGE-V1\0"u8.ToArray();
        var challengePayload = domainTag.Concat(challengeBytes).ToArray();
        var session = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>();
        var masterDek = session.GetMasterDek();
        byte[] signature;
        try
        {
            signature = BeeMemoryBank.Crypto.NodeIdentityCrypto.SignWithIdentity(
                identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                identity.NodeId, masterDek, challengePayload);
        }
        finally { Array.Clear(masterDek); }

        var authResp = await server.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = identity.NodeId,
            ChallengeB64 = challengeData.Challenge,
            SignatureB64 = Convert.ToBase64String(signature)
        });
        authResp.EnsureSuccessStatusCode();
        var authData = await authResp.Content.ReadFromJsonAsync<AuthTokenDto>(JsonOpts)
            ?? throw new InvalidDataException();

        return authData.Token;
    }

    private static async Task ExtractTarGzAsync(byte[] tarGzBytes, string destDir)
    {
        using var ms = new MemoryStream(tarGzBytes);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var tar = new System.Formats.Tar.TarReader(gz);

        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (entry.EntryType != System.Formats.Tar.TarEntryType.RegularFile) continue;
            var destPath = Path.GetFullPath(Path.Combine(destDir, entry.Name));
            if (!destPath.StartsWith(Path.GetFullPath(destDir) + Path.DirectorySeparatorChar))
                throw new InvalidOperationException($"Path traversal: {entry.Name}");
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    private sealed record ChallengeDto(string Challenge, Guid ServerNodeId);
    private sealed record AuthTokenDto(string Token);
}
