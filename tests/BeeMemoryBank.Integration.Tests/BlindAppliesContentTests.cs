using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Plan 3.6 guard (review L-stage0 #10): a blind node takes in what a full node writes — article
/// create and update with concept tags, media — without the DEK and without the embedding model.
/// The bodies stay ciphertext, tags land without vectors, and nothing is refused: a blind node that
/// quarantined content events would be useless as a replica.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class BlindAppliesContentTests : IAsyncLifetime
{
    private const string Password = "blindAppliesContentPw1!";
    private readonly BlindNodeFactory _blind = new();
    private readonly BmbWebApplicationFactory _pc = new();
    private HttpClient _pcClient = null!;

    public async Task InitializeAsync()
    {
        _ = _blind.Services;
        await _pc.InitializeNodeAsync("PC", Password);
        _pcClient = _pc.CreateClient();
        (await _pcClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password }))
            .EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _pcClient.Dispose();
        _pc.Dispose();
        _blind.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateUpdateAndMedia_FromAFullNode_LandOnTheBlindNode_WithoutDekOrModel()
    {
        var created = await _pcClient.PostAsJsonAsync("/api/articles",
            new { title = "Replica", treePath = "/Blind", content = "first body" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var articleId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        (await _pcClient.PutAsJsonAsync($"/api/articles/{articleId}",
            new { content = "second body", conceptTags = new[] { "blind-tag-a", "blind-tag-b" } }))
            .EnsureSuccessStatusCode();
        using (var form = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent("attached bytes"u8.ToArray());
            file.Headers.ContentType = new("text/plain");
            form.Add(file, "file", "notes.txt");
            form.Add(new StringContent(articleId), "articleId");
            form.Add(new StringContent("true"), "attachment");
            (await _pcClient.PostAsync("/api/media", form)).StatusCode.Should().Be(HttpStatusCode.Created);
        }
        await TrustThePcOnTheBlindNodeAsync();
        await CopyBlobsAsync();

        var events = await _pc.Services.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0);
        var results = new List<string>();
        var failures = new List<string>();
        using (var scope = _blind.Services.CreateScope())
        {
            var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
            foreach (var evt in events)
            {
                try { results.Add($"{evt.EventType}: {await applier.ApplyAsync(evt)}"); }
                catch (Exception ex) { failures.Add($"{evt.EventType}: {ex.GetType().Name} {ex.Message}"); }
            }
        }

        failures.Should().BeEmpty("a blind node would quarantine these; none may fail for want of a DEK or a model");

        results.Should().Contain(new[]
        {
            $"{EventTypes.ArticleCreate}: {EventApplyResult.Applied}",
            $"{EventTypes.ArticleUpdate}: {EventApplyResult.Applied}",
            $"{EventTypes.MediaCreate}: {EventApplyResult.Applied}"
        });
        (await ScalarAsync("SELECT COUNT(*) FROM tbl_article")).Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM tbl_article_concept_tag")).Should().Be(2);
        (await ScalarAsync("SELECT COUNT(*) FROM tbl_concept_tag WHERE name LIKE 'blind-tag-%' AND embedding IS NULL"))
            .Should().Be(2, "the blind node has no model, so tags arrive without vectors");
        (await ScalarAsync("SELECT COUNT(*) FROM tbl_media")).Should().Be(1);
        _blind.Services.GetRequiredService<SessionService>().IsUnlocked.Should().BeFalse();
        _blind.Services.GetRequiredService<IEmbeddingGenerator>().Should().BeOfType<BlindEmbeddingGenerator>();
    }

    private async Task TrustThePcOnTheBlindNodeAsync()
    {
        var pc = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        await _blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = pc.NodeId, DisplayName = "PC", Ed25519PublicKey = pc.Ed25519PublicKey, Status = "A",
            IsSuperadmin = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
    }

    /// <summary>What BlobTransport would carry: the ciphertext the events reference by hash.</summary>
    private async Task CopyBlobsAsync()
    {
        using var conn = _pc.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        using var scope = _blind.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobRepository>();
        foreach (var data in await conn.QueryAsync<byte[]>("SELECT data FROM tbl_blob"))
            await blobs.StoreAsync(data);
    }

    private async Task<long> ScalarAsync(string sql)
    {
        using var conn = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<long>(sql);
    }
}
