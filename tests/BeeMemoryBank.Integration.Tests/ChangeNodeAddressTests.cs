using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-307 and its fix round: changing the address of a trusted node. The route is a thin gate (superadmin, NOT an agent, the master password) in front of
/// <see cref="PeerAddressService"/>, the one writer of a peer's address - so what is tested here is the whole door, with a real node, a real event log and a
/// fake network: an address that is not a sync server is refused in the shared words, the policy comes before any request, the peer's key must be the one on
/// record, the same address is a no-op, what the loop learned about the old address is forgotten, the audit line carries fingerprints, and no answer ever
/// quotes the address that was typed.
/// </summary>
public sealed class ChangeNodeAddressTests : IAsyncLifetime
{
    private const string Password = "addrChange1!";
    private const string Secret = "s3cret-token";
    private readonly BmbWebApplicationFactory _node = new();
    private readonly Guid _peerId = Guid.NewGuid();
    private readonly Peer _peer;
    private HttpClient _api = null!;

    public ChangeNodeAddressTests() => _peer = new Peer(_peerId);

    public enum Answer { Good, NotFound, MethodNotAllowed, Gone, Redirect, WebPage, EmptyIdentity, OtherNode, OtherKey, NoConnection, ServerError, BadGateway }

    private sealed class Peer(Guid id) : HttpMessageHandler
    {
        public Answer Mode { get; set; } = Answer.Good;
        private readonly List<string> _hosts = [];

        /// <summary>The hosts this node has asked, in order (the live loop of the node also asks old.local).</summary>
        public IReadOnlyList<string> Hosts { get { lock (_hosts) return [.. _hosts]; } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_hosts) _hosts.Add(request.RequestUri!.Host);
            HttpResponseMessage Identity(Guid who, byte key) => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    nodeId = who, displayName = "peer", ed25519PublicKeyB64 = Convert.ToBase64String(Enumerable.Repeat(key, 32).ToArray()), protocolVersion = 3,
                }), Encoding.UTF8, "application/json")
            };
            return Task.FromResult(Mode switch
            {
                Answer.NotFound => new HttpResponseMessage(HttpStatusCode.NotFound),
                Answer.MethodNotAllowed => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed),
                Answer.Gone => new HttpResponseMessage(HttpStatusCode.Gone),
                Answer.Redirect => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://login.local/portal?token=" + Secret) } },
                Answer.WebPage => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>router</html>", Encoding.UTF8, "text/html") },
                Answer.EmptyIdentity => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { nodeId = Guid.Empty, displayName = "", ed25519PublicKeyB64 = "" }), Encoding.UTF8, "application/json")
                },
                Answer.OtherNode => Identity(Guid.NewGuid(), 0),
                Answer.OtherKey => Identity(id, 9),
                Answer.NoConnection => throw new HttpRequestException("refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)),
                Answer.ServerError => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                Answer.BadGateway => new HttpResponseMessage(HttpStatusCode.BadGateway),
                _ => Identity(id, 0),
            });
        }
    }

    public async Task InitializeAsync()
    {
        _node.RouteOutboundHttpThrough(_peer);
        _api = _node.CreateClient();
        await _node.InitializeNodeAsync("Node", Password);
        (await _api.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        using var scope = _node.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = _peerId, DisplayName = "Laptop", Ed25519PublicKey = new byte[32], ApiAddress = "http://old.local",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }

    public Task DisposeAsync()
    {
        _api.Dispose();
        _node.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<(HttpStatusCode Status, string Error)> ReadAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        var error = "";
        try { error = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString() ?? ""; } catch (Exception ex) when (ex is JsonException or KeyNotFoundException) { }
        return (resp.StatusCode, error);
    }

    private async Task<(HttpStatusCode Status, string Error)> ChangeAsync(string address = "http://peer.local/", string? password = null, HttpClient? client = null) =>
        await ReadAsync(await (client ?? _api).PutAsJsonAsync($"/api/whitelist/{_peerId}/address", new { newApiAddress = address, password = password ?? Password }));

    private async Task<WhitelistEntry> RowAsync()
    {
        using var scope = _node.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(_peerId))!;
    }

    private async Task<string?> AddressAsync() => (await RowAsync()).ApiAddress;

    private async Task<int> UpdateEventsAsync()
    {
        using var scope = _node.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetRecentAsync(500, 0, EventTypes.WhitelistUpdate)).Count;
    }

    // ---------------------------------------------------------------- what answers at the new address

    [Theory]
    [InlineData(Answer.NotFound, "it answered 404")]
    [InlineData(Answer.MethodNotAllowed, "it answered 405")]
    [InlineData(Answer.Gone, "it answered 410")]
    [InlineData(Answer.Redirect, "it answered 302")]
    [InlineData(Answer.WebPage, "not with a sync identity")]
    [InlineData(Answer.EmptyIdentity, "not with a sync identity")]
    public async Task An_address_with_no_sync_server_is_refused_in_the_shared_words_and_nothing_is_saved(Answer answer, string detail)
    {
        _peer.Mode = answer;
        var events = await UpdateEventsAsync();

        var (status, error) = await ChangeAsync();

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.StartsWith("No sync server at this address: ", error);
        Assert.Contains(detail, error);
        Assert.DoesNotContain("peer.local", error);
        Assert.DoesNotContain(Secret, error);
        Assert.DoesNotContain("login.local", error);
        Assert.Equal("http://old.local", await AddressAsync());
        Assert.Equal(events, await UpdateEventsAsync());
    }

    [Theory]
    [InlineData(Answer.NoConnection, "connection refused")]
    [InlineData(Answer.ServerError, "it answered 500")]
    [InlineData(Answer.BadGateway, "it answered 502")]
    public async Task An_address_that_does_not_answer_or_is_in_trouble_is_not_a_missing_sync_server_and_is_not_saved_here(Answer answer, string detail)
    {
        _peer.Mode = answer;

        var (status, error) = await ChangeAsync();

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.StartsWith("Nothing answered at the new address:", error);
        Assert.Contains(detail, error);
        Assert.DoesNotContain("peer.local", error);
        Assert.DoesNotContain("No sync server", error);
        Assert.Equal("http://old.local", await AddressAsync());
    }

    [Fact]
    public async Task A_different_node_at_the_address_is_refused()
    {
        _peer.Mode = Answer.OtherNode;

        var (status, error) = await ChangeAsync();

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("This is a different node", error);
        Assert.DoesNotContain("peer.local", error);
        Assert.Equal("http://old.local", await AddressAsync());
    }

    [Fact]
    public async Task The_right_node_id_with_another_key_is_refused_and_the_key_on_record_is_untouched()
    {
        _peer.Mode = Answer.OtherKey;
        var events = await UpdateEventsAsync();

        var (status, error) = await ChangeAsync();

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("another key", error);
        Assert.DoesNotContain("peer.local", error);
        var row = await RowAsync();
        Assert.Equal("http://old.local", row.ApiAddress);
        Assert.Equal(new byte[32], row.Ed25519PublicKey);
        Assert.Equal(events, await UpdateEventsAsync());
    }

    // ---------------------------------------------------------------- the policy comes first

    [Theory]
    [InlineData("http://user:" + Secret + "@peer.local")]
    [InlineData("https://peer.local/?token=" + Secret)]
    [InlineData("ftp://peer.local/" + Secret)]
    [InlineData("http://169.254.169.254/latest/meta-data/" + Secret)]
    [InlineData("http://[fe80::1]:5300")]
    [InlineData("http://0.0.0.0:5300")]
    [InlineData("http://metadata.google.internal/" + Secret)]
    [InlineData("http://2852039166")]
    public async Task An_address_the_policy_refuses_sends_no_request_anywhere_and_the_answer_does_not_quote_it(string typed)
    {
        var (status, error) = await ChangeAsync(typed);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain(Secret, error);
        Assert.DoesNotContain("peer.local", error);
        Assert.DoesNotContain("169.254.169.254", error);
        Assert.DoesNotContain("metadata.google.internal", error);
        Assert.False(_peer.Hosts.Any(h => h is "peer.local" or "169.254.169.254" or "metadata.google.internal" or "0.0.0.0" or "fe80::1" or "[fe80::1]"),
            "the address is judged before any request is made");
        Assert.Equal("http://old.local", await AddressAsync());
    }

    [Theory]
    [InlineData("http://127.0.0.1:5300", "http://127.0.0.1:5300")]
    [InlineData("http://192.168.1.20:5300/", "http://192.168.1.20:5300")]
    [InlineData("HTTP://Peer.Local:80/", "http://peer.local")]
    public async Task Loopback_and_private_addresses_are_saved_in_their_canonical_form_when_the_node_answers_there(string typed, string stored)
    {
        var (status, _) = await ChangeAsync(typed);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(stored, await AddressAsync());
    }

    // ---------------------------------------------------------------- the change itself

    [Fact]
    public async Task The_right_node_at_the_address_is_saved_published_and_audited_without_the_address()
    {
        var events = await UpdateEventsAsync();

        var (status, _) = await ChangeAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("http://peer.local", await AddressAsync());
        Assert.Equal(events + 1, await UpdateEventsAsync());

        using var scope = _node.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Storage.Sqlite.DbConnectionFactory>().CreateConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT details FROM tbl_audit_log WHERE entity_id = @id AND action = 'peer_address_changed'";
        var id = cmd.CreateParameter();
        id.ParameterName = "@id";
        id.Value = _peerId.ToString();
        cmd.Parameters.Add(id);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "the change is in the audit trail");
        var details = reader.GetString(0);
        Assert.Contains(PeerAddressPolicy.Fingerprint("http://old.local"), details);
        Assert.Contains(PeerAddressPolicy.Fingerprint("http://peer.local"), details);
        Assert.DoesNotContain("peer.local", details);
        Assert.DoesNotContain("old.local", details);
        Assert.False(reader.Read(), "one line");
    }

    [Fact]
    public async Task The_same_address_again_is_a_no_op_no_second_event_no_second_audit_line()
    {
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync("peer.local")).Status);
        var events = await UpdateEventsAsync();

        var again = await ChangeAsync("HTTPS://PEER.LOCAL:443/");

        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(events, await UpdateEventsAsync());
        Assert.Equal("https://peer.local", await AddressAsync());
    }

    [Fact]
    public async Task A_change_forgets_what_the_loop_learned_about_the_old_address_and_asks_for_a_sync()
    {
        var contacts = _node.Services.GetRequiredService<PeerContactLog>();
        contacts.NoteOutboundFailure(_peerId, DateTime.UtcNow, "no answer: connection refused", "http://old.local");
        Assert.NotNull(contacts.Get(_peerId, "http://old.local").OutboundFailure);

        var (status, _) = await ChangeAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(contacts.Get(_peerId, "http://peer.local").OutboundFailure);
        Assert.Null(contacts.Get(_peerId, "http://old.local").OutboundFailure);
    }

    // ---------------------------------------------------------------- who may ask

    [Fact]
    public async Task A_wrong_master_password_changes_nothing_and_sends_nothing()
    {
        var (status, error) = await ChangeAsync(password: "not the password");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("Invalid master password", error);
        Assert.Equal("http://old.local", await AddressAsync());
        Assert.DoesNotContain("peer.local", _peer.Hosts);
    }

    [Fact]
    public async Task An_agent_even_one_owned_by_a_superadmin_cannot_change_an_address()
    {
        var users = await _api.GetFromJsonAsync<JsonElement>("/api/users");
        var admin = users.EnumerateArray().First(u => u.GetProperty("role").GetString() == "superadmin").GetProperty("id").GetInt32();
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var masterDek = _node.Services.GetRequiredService<SessionService>().GetMasterDek();
        var (ciphertext, iv) = AgentKeyHelper.EncryptDek(apiKey, masterDek);
        Array.Clear(masterDek);
        using (var scope = _node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAgentRepository>().CreateAsync(new Agent
            {
                Name = "owner-agent", KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey), KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
                EncryptedDek = ciphertext, DekIV = iv, Status = "A", CreatedAt = DateTime.UtcNow, OwnerUserId = admin,
            });
        using var agent = _node.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var (status, error) = await ChangeAsync(client: agent);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("Agents cannot perform this operation", error);
        Assert.Equal("http://old.local", await AddressAsync());
        Assert.DoesNotContain("peer.local", _peer.Hosts);
    }

    [Fact]
    public async Task The_generic_update_no_longer_takes_an_address_but_still_renames()
    {
        var withAddress = await ReadAsync(await _api.PutAsJsonAsync($"/api/whitelist/{_peerId}", new { apiAddress = "http://evil.local" }));

        Assert.Equal(HttpStatusCode.BadRequest, withAddress.Status);
        Assert.Contains("/address", withAddress.Error);
        Assert.Equal("http://old.local", await AddressAsync());

        var events = await UpdateEventsAsync();
        var rename = await _api.PutAsJsonAsync($"/api/whitelist/{_peerId}", new { displayName = "Office laptop" });

        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var row = await RowAsync();
        Assert.Equal("Office laptop", row.DisplayName);
        Assert.Equal("http://old.local", row.ApiAddress);
        Assert.Equal(events + 1, await UpdateEventsAsync());
    }

    [Fact]
    public async Task An_unknown_node_is_not_found()
    {
        var resp = await _api.PutAsJsonAsync($"/api/whitelist/{Guid.NewGuid()}/address", new { newApiAddress = "http://peer.local", password = Password });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
