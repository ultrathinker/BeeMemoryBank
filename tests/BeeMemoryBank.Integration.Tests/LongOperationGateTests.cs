using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-311, server side: a second identical request for a long operation is refused at once with 409 "already running" (code
/// <c>already_running</c>), instead of starting a second copy of something that takes minutes. The refusal comes from the endpoint filter,
/// before the handler: that is what these tests hold the lease for.
/// </summary>
public sealed class LongOperationGateTests : IAsyncLifetime
{
    private const string Password = "longOpsPw1!";
    private readonly BmbWebApplicationFactory _node = new();

    public async Task InitializeAsync()
    {
        _node.RouteOutboundHttpThrough(new NoNetwork());
        await _node.InitializeNodeAsync("Node", Password);
    }

    public Task DisposeAsync()
    {
        _node.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void TheGate_GivesOneLeasePerKey_AndTakesItBackOnDispose()
    {
        var gate = new LongOperationGate();
        var first = gate.TryEnter("k");
        Assert.NotNull(first);
        Assert.True(gate.IsRunning("k"));
        Assert.Null(gate.TryEnter("k"));
        Assert.NotNull(gate.TryEnter("other"));   // another key runs next to it

        first!.Dispose();
        first.Dispose();                           // twice is harmless
        Assert.False(gate.IsRunning("k"));
        Assert.NotNull(gate.TryEnter("k"));
    }

    private LongOperationGate Gate => _node.Services.GetRequiredService<LongOperationGate>();

    [Fact]
    public void TheGateIsPerNode_NotPerProcess()
    {
        using var other = new BmbWebApplicationFactory();
        other.RouteOutboundHttpThrough(new NoNetwork());
        _ = other.CreateClient();   // starts the second host in this process

        using var held = Gate.TryEnter("blind-add");

        Assert.NotNull(held);
        Assert.NotSame(Gate, other.Services.GetRequiredService<LongOperationGate>());
        Assert.False(other.Services.GetRequiredService<LongOperationGate>().IsRunning("blind-add"));
    }

    [Fact]
    public async Task AddingABlindNode_WhileAnotherAddRuns_Answers409AlreadyRunning()
    {
        using var held = Gate.TryEnter("blind-add");
        Assert.NotNull(held);
        using var api = _node.CreateClient();

        var second = await api.PostAsJsonAsync("/api/blind-nodes/", new { code = "BMBBLIND1.not-a-real-code" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already_running", body.GetProperty("code").GetString());
        Assert.Contains("already running", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AddingABlindNode_AfterTheFirstEnded_RunsTheHandlerAgain()
    {
        using var api = _node.CreateClient();

        // The handler runs (and refuses the code as malformed): 400, not 409.
        var first = await api.PostAsJsonAsync("/api/blind-nodes/", new { code = "not-a-pair-code" });
        Assert.NotEqual(HttpStatusCode.Conflict, first.StatusCode);
        // The lease was given back although the handler refused.
        var second = await api.PostAsJsonAsync("/api/blind-nodes/", new { code = "not-a-pair-code" });
        Assert.NotEqual(HttpStatusCode.Conflict, second.StatusCode);
        Assert.False(Gate.IsRunning("blind-add"));
    }

    [Fact]
    public async Task ReseedingABlindNode_IsOneAtATimePerNode()
    {
        var node = Guid.NewGuid();
        using var held = Gate.TryEnter($"blind-reseed:{node}");
        Assert.NotNull(held);
        using var api = _node.CreateClient();

        var same = await api.PostAsync($"/api/blind-nodes/{node}/reseed", null);
        var other = await api.PostAsync($"/api/blind-nodes/{Guid.NewGuid()}/reseed", null);

        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);   // another node: the handler ran (it knows no such node)
    }

    [Fact]
    public async Task ASnapshot_WhileAnotherIsBeingMade_Answers409()
    {
        using var held = Gate.TryEnter("snapshot-create");
        Assert.NotNull(held);
        using var api = _node.CreateClient();

        var second = await api.PostAsync("/api/snapshots/", null);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("already running", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheEmbeddingsBackfill_RefusesASecondOneWhileTheDrainRuns()
    {
        using var api = _node.CreateClient();
        (await api.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        using var held = Gate.TryEnter("embeddings-backfill");
        Assert.NotNull(held);

        var second = await api.PostAsync("/api/admin/search/embeddings/backfill", null);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    /// <summary>The routes that take a lease. A new long endpoint is added here on purpose; a route that loses its lease fails here.</summary>
    [Fact]
    public void TheLongRoutes_AreMarkedSingleFlight()
    {
        var expected = new Dictionary<string, string>
        {
            ["POST /api/blind-nodes"] = "blind-add",
            ["POST /api/blind-nodes/{nodeId:guid}/reseed"] = "blind-reseed:{nodeId}",
            ["POST /api/blind-nodes/android"] = "blind-phone-pair",
            ["POST /api/snapshots"] = "snapshot-create",
            ["POST /api/snapshots/restore"] = "snapshot-restore",
            ["POST /api/snapshots/restore-network"] = "snapshot-restore",
            ["POST /api/snapshots/upload"] = "snapshot-upload",
            ["POST /api/import/bee"] = "import",
            ["POST /api/import/obsidian"] = "import",
            ["POST /api/init/standalone"] = "node-init",
            ["POST /api/init/join"] = "node-init",
            ["POST /api/init/reset"] = "node-init",
            ["POST /api/keys/change-password"] = "master-password-change",
            ["POST /api/admin/concept-tag-edge/rebuild"] = "graph-rebuild",
            // BMB-311 fix round: the long operations that had no gate.
            ["POST /api/sync/activation"] = "metadata-activation",
            ["POST /api/downloads/prepare"] = "export-folder:{path}|export-all",
            ["POST /api/folders/{id:guid}/copy"] = "copy-folder:{id}",
            ["POST /api/hard-delete/article/{id:guid}"] = "hard-delete-article:{id}",
            ["POST /api/hard-delete/folder"] = "hard-delete-folder:{path}",
            ["POST /api/hard-delete/restore/article/{id:guid}"] = "restore-article:{id}",
            ["POST /api/hard-delete/restore/folder/{id:guid}"] = "restore-folder:{id}",
            ["POST /api/users/me/change-password"] = "user-password:{userId}",
            ["POST /api/users/{id:int}/change-password"] = "user-password:{id}",
        };

        var actual = new Dictionary<string, string>();
        foreach (var endpoint in _node.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var marker = endpoint.Metadata.GetMetadata<SingleFlightMetadata>();
            if (marker == null) continue;
            var method = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "?";
            actual[$"{method} /{endpoint.RoutePattern.RawText?.Trim('/')}"] = marker.Key;
        }

        foreach (var (route, key) in expected)
            Assert.True(actual.TryGetValue(route, out var found) && found == key, $"{route} should take the lease '{key}' (found: {(actual.TryGetValue(route, out var f) ? f : "none")})");
        Assert.Empty(actual.Keys.Except(expected.Keys));   // nothing is marked without being listed here
    }

    /// <summary>
    /// The routes added by the fix round take their lease from the right thing: the route value, the body, or the caller. Held lease -> 409 with the code; another
    /// resource, or a request that is not a long one, is not refused.
    /// </summary>
    [Theory]
    [InlineData("/api/sync/activation", "{\"password\":\"x\"}", "metadata-activation")]
    [InlineData("/api/downloads/prepare", "{\"kind\":\"folder\",\"path\":\"/Docs/\",\"withImages\":false}", "export-folder:docs")]
    [InlineData("/api/downloads/prepare", "{\"kind\":\"all\",\"withImages\":false}", "export-all")]
    [InlineData("/api/folders/5d7e4cc2-1c1e-4a6b-8a38-0c5bd5b0f001/copy", "{\"targetParentPath\":\"/x\"}", "copy-folder:5d7e4cc2-1c1e-4a6b-8a38-0c5bd5b0f001")]
    [InlineData("/api/hard-delete/article/5d7e4cc2-1c1e-4a6b-8a38-0c5bd5b0f001", "{}", "hard-delete-article:5d7e4cc2-1c1e-4a6b-8a38-0c5bd5b0f001")]
    [InlineData("/api/hard-delete/folder", "{\"path\":\"/Docs\"}", "hard-delete-folder:docs")]
    [InlineData("/api/hard-delete/restore/article/5d7e4cc2-1c1e-4a6b-8a38-0c5bd5b0f001", "{}", "restore-article:5d7e4cc2-1c1e-4a6b-8a38-0c5bd5b0f001")]
    [InlineData("/api/hard-delete/restore/folder/5d7e4cc2-1c1e-4a6b-8a38-0c5bd5b0f001", "{}", "restore-folder:5d7e4cc2-1c1e-4a6b-8a38-0c5bd5b0f001")]
    [InlineData("/api/users/7/change-password", "{\"newPassword\":\"x\"}", "user-password:7")]
    public async Task TheRoutesOfTheFixRound_RefuseASecondRequestForTheSameResource(string url, string json, string key)
    {
        using var held = Gate.TryEnter(key);
        Assert.NotNull(held);
        using var api = _node.CreateClient();

        var second = await api.PostAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("already_running", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheSelfServicePasswordChange_IsGatedPerCaller_AndAnotherUserIsNotRefused()
    {
        using var held = Gate.TryEnter("user-password:7");
        Assert.NotNull(held);
        using var api = _node.CreateClient();
        HttpRequestMessage Change(string userId) => new(HttpMethod.Post, "/api/users/me/change-password")
        {
            Headers = { { "X-User-Id", userId } },
            Content = new StringContent("{\"oldPassword\":\"a\",\"newPassword\":\"b\"}", System.Text.Encoding.UTF8, "application/json"),
        };

        var same = await api.SendAsync(Change("7"));
        var other = await api.SendAsync(Change("8"));
        var noCaller = await api.PostAsJsonAsync("/api/users/me/change-password", new { oldPassword = "a", newPassword = "b" });

        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        Assert.NotEqual(HttpStatusCode.Conflict, other.StatusCode);       // another user: the handler ran
        Assert.Equal(HttpStatusCode.Unauthorized, noCaller.StatusCode);   // nobody identified: no lease, and the handler's own answer
    }

    [Fact]
    public async Task AnArticleExport_IsAShortOne_AndTakesNoLease_WhileAFolderExportDoes()
    {
        using var heldAll = Gate.TryEnter("export-all");
        Assert.NotNull(heldAll);
        using var api = _node.CreateClient();

        var article = await api.PostAsJsonAsync("/api/downloads/prepare", new { kind = "article", id = Guid.NewGuid(), withImages = false });
        var folder = await api.PostAsJsonAsync("/api/downloads/prepare", new { kind = "folder", path = "/Elsewhere", withImages = false });

        Assert.NotEqual(HttpStatusCode.Conflict, article.StatusCode);
        Assert.NotEqual(HttpStatusCode.Conflict, folder.StatusCode);   // another resource than the whole vault
    }

    [Fact]
    public async Task TheStatusOfTheGate_ListsWhatRuns_ToASuperadminOnly()
    {
        using var held = Gate.TryEnter("snapshot-create", "Creating a snapshot");
        Assert.NotNull(held);
        using var api = _node.CreateClient();

        var ok = await api.GetFromJsonAsync<JsonElement>("/api/admin/operations");
        using var user = _node.CreateClient();
        user.DefaultRequestHeaders.Remove("X-User-Role");
        user.DefaultRequestHeaders.Add("X-User-Role", "user");
        var refused = await user.GetAsync("/api/admin/operations");

        var row = Assert.Single(ok.EnumerateArray());
        Assert.Equal("snapshot-create", row.GetProperty("key").GetString());
        Assert.Equal("Creating a snapshot", row.GetProperty("what").GetString());
        Assert.True(row.TryGetProperty("since", out _));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("no network in this test");
    }
}
