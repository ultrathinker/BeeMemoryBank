using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Setup page's join (<c>POST /api/init/join</c>) against a real host in process: when the join fails after the host wrote the
/// new node's row, the joiner takes that row back, so the host does not keep an active never-synced peer nobody holds the key of.
/// </summary>
[Collection(ProcessWideRateLimiterCollection.Name)]
public class InitJoinAbortTests : IAsyncLifetime
{
    private const string MasterPassword = "initJoinAbortPassword123";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private BmbWebApplicationFactory _host = null!;
    private HttpClient _hostClient = null!;

    public async Task InitializeAsync()
    {
        // The host's join and abort share one 5-per-5-minutes budget per caller in this process; every test here joins at least once.
        RateLimitMiddleware.ResetForTests();
        _host = new BmbWebApplicationFactory();
        await _host.InitializeNodeAsync("AbortHost", MasterPassword);
        _hostClient = _host.CreateClient();
        (await _hostClient.PostAsJsonAsync("/api/session/unlock", new { Password = MasterPassword })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _hostClient.Dispose();
        _host.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Passes everything to the real host, except what <paramref name="refuse"/> picks, which it answers with the status it names.</summary>
    private sealed class SabotagingHandler(HttpMessageHandler inner, Func<HttpRequestMessage, HttpStatusCode?> refuse) : DelegatingHandler(inner)
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            return refuse(request) is { } status
                ? Task.FromResult(new HttpResponseMessage(status))
                : base.SendAsync(request, ct);
        }
    }

    /// <summary>Passes everything to the real host, but hands the joiner what <paramref name="rewrite"/> makes of the host's answer to <c>/api/join</c>.</summary>
    private sealed class RewritingJoinHandler(HttpMessageHandler inner, Func<string, string> rewrite) : DelegatingHandler(inner)
    {
        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri!.AbsolutePath != "/api/join" || !response.IsSuccessStatusCode) return response;
            var body = rewrite(await response.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private async Task<(HttpResponseMessage Response, List<string> Paths, Guid JoinerId)> JoinWithRewrittenAnswerAsync(Func<string, string> rewrite)
    {
        using var joiner = new BmbWebApplicationFactory();
        var handler = new RewritingJoinHandler(_host.Server.CreateHandler(), rewrite);
        joiner.RouteOutboundHttpThrough(handler);
        var client = joiner.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/init/join", new
        {
            adminUsername = "admin",
            displayName = "Failing joiner",
            remoteUrl = "http://host",
            password = MasterPassword
        }, JsonOpts);

        using var scope = _host.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
        var joinerRow = rows.SingleOrDefault(r => r.DisplayName == "Failing joiner");
        return (resp, handler.Paths, joinerRow?.NodeId ?? Guid.Empty);
    }

    private static string WithField(string json, string section, string field, string value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node[section]![field] = value;
        return node.ToJsonString();
    }

    private async Task AssertTakenBackAsync(HttpResponseMessage resp, List<string> paths, Guid activeRowId, string cause)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var error = (await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("error").GetString();
        error.Should().Contain("The join could not be completed").And.Contain(cause)
            .And.Contain("no longer lists").And.NotContain("revoke it").And.NotContain(MasterPassword);
        paths.Count(p => p == "/api/join/abort").Should().Be(1);
        activeRowId.Should().Be(Guid.Empty, "the host took the failed joiner's row back; compaction no longer waits for it");
    }

    [Theory]
    [InlineData("{\"remoteNode\":{\"nodeId\":")]
    [InlineData("this is not json")]
    public async Task AnAnswerOf200ThatIsNotReadable_LeavesNoActiveRowOnTheHost(string body)
    {
        var (resp, paths, activeRowId) = await JoinWithRewrittenAnswerAsync(_ => body);

        await AssertTakenBackAsync(resp, paths, activeRowId, "JSON");
    }

    [Theory]
    [InlineData("keySlot", "encryptedMasterDekB64")]
    [InlineData("keySlot", "ivB64")]
    [InlineData("keySlot", "saltB64")]
    public async Task AKeySlotThatIsNotBase64_LeavesNoActiveRowOnTheHost(string section, string field)
    {
        var (resp, paths, activeRowId) = await JoinWithRewrittenAnswerAsync(json => WithField(json, section, field, "***not base64***"));

        await AssertTakenBackAsync(resp, paths, activeRowId, "Base-64");
    }

    [Fact]
    public async Task AHostKeyThatIsNotBase64_FailsTheLocalWrites_AndLeavesNoActiveRowOnTheHost()
    {
        var (resp, paths, activeRowId) = await JoinWithRewrittenAnswerAsync(
            json => WithField(json, "remoteNode", "ed25519PublicKeyB64", "***not base64***"));

        await AssertTakenBackAsync(resp, paths, activeRowId, "Base-64");
    }

    private async Task<(HttpResponseMessage Response, SabotagingHandler Handler, Guid JoinerId)> JoinThroughAsync(Func<HttpRequestMessage, HttpStatusCode?> refuse)
    {
        using var joiner = new BmbWebApplicationFactory();
        var handler = new SabotagingHandler(_host.Server.CreateHandler(), refuse);
        joiner.RouteOutboundHttpThrough(handler);
        var client = joiner.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/init/join", new
        {
            adminUsername = "admin",
            displayName = "Failing joiner",
            remoteUrl = "http://host",
            password = MasterPassword
        }, JsonOpts);

        using var scope = _host.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
        var joinerRow = rows.SingleOrDefault(r => r.DisplayName == "Failing joiner");
        return (resp, handler, joinerRow?.NodeId ?? Guid.Empty);
    }

    [Fact]
    public async Task AJoinThatFailsAtTheSnapshot_LeavesNoActiveRowOnTheHost_AndSaysThatTheHostWasTold()
    {
        var (resp, handler, activeRowId) = await JoinThroughAsync(r =>
            r.RequestUri!.AbsolutePath == "/api/sync/snapshot/for-join" ? HttpStatusCode.InternalServerError : null);

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var error = (await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("error").GetString();
        error.Should().Contain("snapshot import failed").And.Contain("no longer lists").And.NotContain("revoke it").And.NotContain(MasterPassword);
        handler.Paths.Should().Contain("/api/join/abort");
        activeRowId.Should().Be(Guid.Empty, "the host took the failed joiner's row back; compaction no longer waits for it");
    }

    [Fact]
    public async Task AJoinThatFailsAtTheSnapshot_AgainstAHostWithoutTheRoute_KeepsTheAccurateSentence()
    {
        // A host older than 2.5.1, or a proxy in front of it that does not forward the route, answers 404.
        var (resp, handler, activeRowId) = await JoinThroughAsync(r => r.RequestUri!.AbsolutePath switch
        {
            "/api/sync/snapshot/for-join" => HttpStatusCode.InternalServerError,
            "/api/join/abort" => HttpStatusCode.NotFound,
            _ => null
        });

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var error = (await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("error").GetString();
        error.Should().Contain("snapshot import failed").And.Contain("still lists this node ('Failing joiner')").And.Contain("revoke it");
        handler.Paths.Should().Contain("/api/join/abort");
        activeRowId.Should().NotBe(Guid.Empty, "nothing took the row back");
    }
}
