using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Concept tags set through the REST surface must reach other nodes.
///
/// They did not. Every REST route attached tags with a bare
/// <c>ConceptTagService.SetForArticleAsync</c> AFTER the article write that emits the sync event,
/// so the event carried an empty tag set and a peer received the article untagged — permanently.
/// Worse, the next <c>article_update</c> arriving from that peer carried its own tag-less view,
/// which <c>ApplyArticleUpdateCore</c> applied, wiping the tags on the originating node too.
///
/// The MCP tool and the chat dispatcher were both moved to the atomic
/// <c>ArticleService.CreateAsync/UpdateAsync</c> form; ChatToolDispatcher's own comment said "MCP
/// and REST should move TO this atomic form — the divergence is theirs to fix". REST never did.
/// Found on the live two-node test mesh: test1 held 201 concept tags, test2 held zero.
/// </summary>
public class ConceptTagSyncTests : IAsyncLifetime
{
    private BmbWebApplicationFactory _nodeA = null!;
    private BmbWebApplicationFactory _nodeB = null!;
    private HttpClient _clientA = null!;
    private HttpClient _clientB = null!;

    private const string MasterPassword = "sharedPassword123";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _nodeA = new BmbWebApplicationFactory();
        _nodeB = new BmbWebApplicationFactory();
        await _nodeA.InitializeNodeAsync("NodeA", MasterPassword);
        _clientA = _nodeA.CreateClient();
        (await _clientA.PostAsJsonAsync("/api/session/unlock", new { Password = MasterPassword })).EnsureSuccessStatusCode();
        await _nodeB.JoinNodeAsync(_clientA, "NodeB", MasterPassword);
        _clientB = _nodeB.CreateClient();
        (await _clientB.PostAsJsonAsync("/api/session/unlock", new { Password = MasterPassword })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _clientA.Dispose(); _clientB.Dispose();
        _nodeA.Dispose(); _nodeB.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TagsFromPostArticles_ReachThePeer()
    {
        var id = await CreateAsync(_clientA, "Tagged on create", "/T", "body", ["alpha", "beta"]);

        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);

        (await TagsOnAsync(_clientB, id)).Should().BeEquivalentTo("alpha", "beta");
    }

    [Fact]
    public async Task TagsFromPutArticles_ReachThePeer()
    {
        var id = await CreateAsync(_clientA, "Tagged on update", "/T", "body", []);
        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);

        (await _clientA.PutAsJsonAsync($"/api/articles/{id}", new { conceptTags = new[] { "gamma" } }))
            .EnsureSuccessStatusCode();
        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);

        (await TagsOnAsync(_clientB, id)).Should().BeEquivalentTo("gamma");
    }

    [Fact]
    public async Task TagsFromTheDedicatedConceptTagsRoute_ReachThePeer()
    {
        var id = await CreateAsync(_clientA, "Tagged via route", "/T", "body", []);
        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);

        (await _clientA.PutAsJsonAsync($"/api/articles/{id}/concept-tags", new { conceptTags = new[] { "delta" } }))
            .EnsureSuccessStatusCode();
        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);

        (await TagsOnAsync(_clientB, id)).Should().BeEquivalentTo("delta");
    }

    /// <summary>
    /// The half that turned "tags don't propagate" into "tags disappear": once the peer holds a
    /// tag-less copy, its own update of that article pushes that empty set back and erases the
    /// tags where they were set.
    /// </summary>
    [Fact]
    public async Task APeerEditingTheArticle_DoesNotWipeTagsBackHome()
    {
        var id = await CreateAsync(_clientA, "Round trip", "/T", "body", ["keepme"]);
        await SyncNodeWithAsync(_nodeB, _clientA, _nodeA);

        (await _clientB.PutAsJsonAsync($"/api/articles/{id}", new { title = "Round trip, edited on B" }))
            .EnsureSuccessStatusCode();
        await SyncNodeWithAsync(_nodeA, _clientB, _nodeB);

        (await TagsOnAsync(_clientA, id)).Should().BeEquivalentTo("keepme");
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static async Task<Guid> CreateAsync(
        HttpClient client, string title, string treePath, string content, string[] conceptTags)
    {
        var resp = await client.PostAsJsonAsync("/api/articles", new { title, treePath, content, conceptTags });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    private static async Task<List<string>> TagsOnAsync(HttpClient client, Guid id)
    {
        var resp = await client.GetAsync($"/api/articles/{id}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return [.. body.GetProperty("conceptTags").EnumerateArray().Select(e => e.GetString()!)];
    }

    private static async Task SyncNodeWithAsync(
        BmbWebApplicationFactory node, HttpClient serverClient, BmbWebApplicationFactory server)
    {
        using var serverScope = server.Services.CreateScope();
        var serverIdentity = (await serverScope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        using var scope = node.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Sync.SyncClient>()
            .SyncWithAsync(serverClient, "", serverIdentity.NodeId);
    }
}
