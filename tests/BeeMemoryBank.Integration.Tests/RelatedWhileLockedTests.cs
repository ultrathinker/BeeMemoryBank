using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The related list while the vault is locked, through the real REST endpoint. The article vectors are derived from
/// article bodies and sit in the database in the clear, so a locked vault must not hand out relations that only the
/// vectors know about (the same rule as ranked search): the list falls back to shared tags alone, and comes back
/// in full once the vault is unlocked again. The MCP tool has its own twin of this test in
/// <see cref="RelatedMcpToolsTests"/>.
/// </summary>
public class RelatedWhileLockedTests : IAsyncLifetime
{
    private const string Password = "relatedLockedPassword";
    private const int Dim = 64;

    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private readonly Random _random = new(20261112);

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> CreateAsync(string title, params string[] tags)
    {
        var create = await _client.PostAsJsonAsync("/api/articles", new { title, treePath = "/Dev", conceptTags = tags, content = "text" });
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<ArticleResponse>())!.Id;
    }

    private float[] RandomVector()
    {
        var v = new float[Dim];
        for (int i = 0; i < Dim; i++) v[i] = (float)(_random.NextDouble() * 2 - 1);
        return v;
    }

    private async Task SetVectorAsync(Guid id, float[] vector)
    {
        using (var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_article SET embedding_projection = @bytes, embedding_pending = 0 WHERE id = @id",
                new { id, bytes = MemoryMarshal.AsBytes(vector.AsSpan()).ToArray() });
        _factory.Services.GetRequiredService<EmbeddingVectorCache>().Invalidate();
    }

    private async Task<List<JsonElement>> RelatedAsync(Guid id)
    {
        var resp = await _client.GetAsync($"/api/articles/{id}/related");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private async Task LockAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/session/lock");
        req.Headers.Add("X-User-Role", "superadmin");
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task WhileTheVaultIsLocked_TheEndpointListsSharedTagsAloneAndNoMeaningNeighbours()
    {
        var basis = RandomVector();
        var subject = await CreateAsync("Subject", "rust");
        var tagged = await CreateAsync("Tagged partner", "rust");
        var copy = await CreateAsync("Copy by meaning only");
        var fillers = new List<Guid>();
        for (int i = 0; i < 40; i++) fillers.Add(await CreateAsync($"Filler {i}"));
        await SetVectorAsync(subject, basis);
        await SetVectorAsync(copy, basis.Select(v => v + (float)(_random.NextDouble() * 0.06 - 0.03)).ToArray());
        foreach (var id in fillers) await SetVectorAsync(id, RandomVector());

        // Unlocked: the tag partner and the near copy are both there.
        var open = await RelatedAsync(subject);
        open.Select(r => r.GetProperty("id").GetGuid()).Should().Contain([tagged, copy]);
        open.Single(r => r.GetProperty("id").GetGuid() == copy).GetProperty("possibleDuplicate").GetBoolean().Should().BeTrue();

        await LockAsync();

        // Locked: only what the tags say.
        var locked = await RelatedAsync(subject);
        locked.Select(r => r.GetProperty("id").GetGuid()).Should().Equal(tagged);
        locked[0].GetProperty("semanticPercent").ValueKind.Should().Be(JsonValueKind.Null);
        locked[0].GetProperty("possibleDuplicate").GetBoolean().Should().BeFalse();
        locked[0].GetProperty("reasons").EnumerateArray().Select(e => e.GetString()).Should().Equal("shared tag: rust");

        // Unlocked again: the meaning part is back.
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        (await RelatedAsync(subject)).Select(r => r.GetProperty("id").GetGuid()).Should().Contain([tagged, copy]);
    }
}
