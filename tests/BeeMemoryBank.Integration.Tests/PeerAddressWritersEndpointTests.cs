using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-306/307 fix round 2 (T1, Q2): <c>POST /api/join</c> takes the joiner's address through the ONE policy for a peer's address before anything is written,
/// stores and announces its canonical form, and a re-join that moves it has the same effects as the "Change node address" screen: the loop forgets the old address
/// and an audit line says so without the address.
/// </summary>
public class JoinAddressEndpointTests : IAsyncLifetime
{
    private const string Password = "joinAddressEndpointPw1";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed class RecordingResetter : IPeerStateResetter
    {
        public List<Guid> Changed { get; } = [];
        public void PeerChanged(Guid nodeId) { lock (Changed) Changed.Add(nodeId); }
    }

    private readonly RecordingResetter _resetter = new();
    private RecoveryTestFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new RecoveryTestFactory(allowOpenCopies: false, services: s => s.Replace(ServiceDescriptor.Singleton<IPeerStateResetter>(_resetter)));
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync("JoinAddressNode", Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private Task<HttpResponseMessage> JoinAsync(Guid nodeId, byte[] publicKey, string? address) =>
        _client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = Password, nodeId, displayName = "Joiner", ed25519PublicKeyB64 = Convert.ToBase64String(publicKey), apiAddress = address,
        });

    private async Task<WhitelistEntry?> RowAsync(Guid nodeId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(nodeId, includeDeleted: true);
    }

    private async Task<List<(string Action, string Details)>> AuditAsync(Guid id)
    {
        using var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return (await conn.QueryAsync<(string, string)>("SELECT action, details FROM tbl_audit_log WHERE entity_id = @id COLLATE NOCASE", new { id = id.ToString() })).ToList();
    }

    [Fact]
    public async Task A_joiners_address_is_stored_and_announced_in_canonical_form()
    {
        var nodeId = Guid.NewGuid();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();

        (await JoinAsync(nodeId, publicKey, "HTTPS://Joiner.LOCAL:443/")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await RowAsync(nodeId))!.ApiAddress.Should().Be("https://joiner.local");
        using var scope = _factory.Services.CreateScope();
        var add = (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0, 100))
            .Single(e => e.EventType == EventTypes.WhitelistAdd && e.Payload.Contains(nodeId.ToString()));
        JsonSerializer.Deserialize<WhitelistAddPayload>(add.Payload)!.ApiAddress.Should().Be("https://joiner.local", "the mesh is told the canonical form too");
    }

    [Theory]
    [InlineData("https://user:secret@joiner.local")]
    [InlineData("https://joiner.local/?token=abc123")]
    [InlineData("http://169.254.169.254/latest")]
    [InlineData("http://[fe80::1]:5300")]
    public async Task A_joiners_address_the_policy_refuses_is_a_400_before_any_row_and_is_never_quoted(string address)
    {
        var nodeId = Guid.NewGuid();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();

        var response = await JoinAsync(nodeId, publicKey, address);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("secret").And.NotContain("abc123").And.NotContain("169.254.169.254").And.NotContain("fe80::1");
        (await RowAsync(nodeId)).Should().BeNull("nothing was written");
    }

    [Fact]
    public async Task A_rejoin_that_moves_the_address_resets_the_loop_and_audits_it_and_one_that_does_not_does_neither()
    {
        var nodeId = Guid.NewGuid();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        (await JoinAsync(nodeId, publicKey, "https://old.local")).StatusCode.Should().Be(HttpStatusCode.OK);
        _resetter.Changed.Clear();

        // The same place written another way: nothing moved, nothing is reset or said.
        (await JoinAsync(nodeId, publicKey, "HTTPS://OLD.local:443/")).StatusCode.Should().Be(HttpStatusCode.OK);
        // No address in the request means "not supplied": the known one stays.
        (await JoinAsync(nodeId, publicKey, null)).StatusCode.Should().Be(HttpStatusCode.OK);
        _resetter.Changed.Should().BeEmpty();
        (await AuditAsync(nodeId)).Should().BeEmpty();

        (await JoinAsync(nodeId, publicKey, "https://new.local:8443")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await RowAsync(nodeId))!.ApiAddress.Should().Be("https://new.local:8443");
        _resetter.Changed.Should().Equal(new[] { nodeId }, "the peer moved: what the loop remembered of the old address is forgotten");
        var line = (await AuditAsync(nodeId)).Should().ContainSingle(l => l.Action == "peer_address_changed").Which;
        line.Details.Should().NotContain("new.local").And.NotContain("old.local");
    }

    [Fact]
    public async Task A_rejoin_with_a_refused_address_changes_nothing()
    {
        var nodeId = Guid.NewGuid();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        (await JoinAsync(nodeId, publicKey, "https://old.local")).StatusCode.Should().Be(HttpStatusCode.OK);
        _resetter.Changed.Clear();

        (await JoinAsync(nodeId, publicKey, "http://169.254.169.254")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await RowAsync(nodeId))!.ApiAddress.Should().Be("https://old.local");
        _resetter.Changed.Should().BeEmpty();
    }
}

/// <summary>
/// The hub pairing is the other local writer of a peer's address: it takes the address through the same policy, and a pairing that moves the address or changes the
/// pin has the same effects.
/// </summary>
[Collection(BlindHubCollection.Name)]
public sealed class HubAddressEffectsTests(BlindHubWorld world) : IAsyncLifetime
{
    public Task InitializeAsync() => world.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<List<(string Action, string Details)>> AuditAsync()
    {
        using var conn = world.Pc.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return (await conn.QueryAsync<(string, string)>("SELECT action, details FROM tbl_audit_log WHERE entity_id = @id COLLATE NOCASE", new { id = world.HubId.ToString() })).ToList();
    }

    [Fact]
    public async Task A_hub_pairing_that_moves_the_address_forgets_what_the_loop_learned_and_audits_it_without_the_address()
    {
        var contacts = world.Pc.Services.GetRequiredService<PeerContactLog>();
        contacts.NoteOutboundFailure(world.HubId, DateTime.UtcNow, "no answer: learned before", "https://elsewhere.example");

        var response = await world.SetHubAsync(BlindTrust.PublicCa);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        contacts.Get(world.HubId).OutboundFailure.Should().BeNull("the address (and the trust behind it) is new: nothing learned before counts");
        var line = (await AuditAsync()).Should().ContainSingle(l => l.Action == "peer_address_changed").Which;
        line.Details.Should().NotContain(world.Hub.Origin).And.NotContain("localhost");
    }

    [Theory]
    [InlineData("https://user:secret@localhost:5301")]
    [InlineData("https://localhost:5301/?token=abc")]
    [InlineData("https://169.254.169.254")]
    public async Task A_hub_address_the_policy_refuses_is_a_400_that_never_quotes_it_and_changes_nothing(string address)
    {
        var before = await world.HubRowOnPcAsync();

        var response = await world.SetHubAsync(BlindTrust.PublicCa, address);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("secret").And.NotContain("abc").And.NotContain("169.254.169.254");
        (await world.HubRowOnPcAsync()).ApiAddress.Should().Be(before.ApiAddress);
    }
}

/// <summary>
/// BMB-306/307 fix round 2 (Q1): the address check says what kind of thing answered, never what the answer said. A peer that answers 404 with a reason phrase that
/// carries a URL and a token is "no sync server", and the sentence the endpoint returns holds none of it.
/// </summary>
public class AddressChangeReasonTextTests : IAsyncLifetime
{
    private const string Password = "addressReasonTextPw1";
    private const string Secret = "tok_9f8e7d6c5b4a";
    private RecoveryTestFactory _factory = null!;
    private HttpClient _client = null!;

    private sealed class ReasonPhraseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = $"Not Found at https://internal.example/secret?token={Secret}" });
    }

    public async Task InitializeAsync()
    {
        _factory = new RecoveryTestFactory(allowOpenCopies: false);
        _factory.RouteOutboundHttpThrough(new ReasonPhraseHandler());
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync("ReasonTextNode", Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_reason_phrase_that_carries_a_url_and_a_token_never_reaches_the_answer_of_the_address_check()
    {
        var peer = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = peer, DisplayName = "Peer", Ed25519PublicKey = new byte[32], ApiAddress = "https://old.example",
                Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });

        var response = await _client.PutAsJsonAsync($"/api/whitelist/{peer}/address", new { newApiAddress = "https://new.example", password = Password });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("404", "the numeric status is said").And.NotContain(Secret).And.NotContain("internal.example").And.NotContain("token=");
    }
}

/// <summary>
/// BMB-306/307 fix round 2 (T3): what the other computer answers to a join - its status line, its reason phrase, an HTML page, an exception raised by the network
/// stack with an address and a token in it - never reaches the answer of this node's own join endpoint. The status and the kind of failure do.
/// </summary>
public class JoinRemoteTextTests : IAsyncLifetime
{
    private const string Secret = "tok_5c4b3a29";
    private readonly BmbWebApplicationFactory _joiner = new();
    private HttpClient? _client;

    private sealed class HostileHost(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(answer(request));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _joiner.Dispose();
        return Task.CompletedTask;
    }

    private async Task<(HttpStatusCode Status, string Body)> JoinAsync()
    {
        // The host is built by the first client, and the routing of its outbound calls is read when it is built: a test routes first, then joins.
        _client ??= _joiner.CreateClient();
        var response = await _client.PostAsJsonAsync("/api/init/join", new
        {
            adminUsername = "admin", displayName = "Joiner", remoteUrl = "http://host.example", password = "Joiner-password-1",
        });
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_refusal_with_a_crafted_reason_phrase_and_an_html_page_is_told_by_its_status_only()
    {
        _joiner.RouteOutboundHttpThrough(new HostileHost(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            ReasonPhrase = $"Server error at https://evil.example/pay?token={Secret}",
            Content = new StringContent($"<html><body>Pay at https://evil.example/pay?token={Secret}</body></html>", System.Text.Encoding.UTF8, "text/html"),
        }));

        var (status, body) = await JoinAsync();

        status.Should().Be(HttpStatusCode.BadGateway);
        body.Should().Contain("500").And.NotContain(Secret).And.NotContain("evil.example").And.NotContain("<html");
    }

    [Fact]
    public async Task A_network_failure_whose_message_carries_an_address_and_a_token_is_told_as_a_kind_of_failure()
    {
        _joiner.RouteOutboundHttpThrough(new HostileHost(_ =>
            throw new HttpRequestException($"Could not reach https://evil.example/?token={Secret} (192.0.2.7:443)", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))));

        var (status, body) = await JoinAsync();

        status.Should().Be(HttpStatusCode.BadGateway);
        body.Should().Contain("Cannot reach remote node").And.Contain("connection refused").And.NotContain(Secret).And.NotContain("evil.example").And.NotContain("192.0.2.7");
    }
}
