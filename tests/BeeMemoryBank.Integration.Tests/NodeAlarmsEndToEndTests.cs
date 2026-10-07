using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Hosting;
using BeeMemoryBank.Node;
using BeeMemoryBank.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-77 end to end in one process: the desktop node's real front (<c>GET /node/alarms</c>, on a real loopback socket) in front of the
/// REAL Api. The identity the front speaks with (the node's internal key, the superadmin role) is the one the Api's
/// <c>/api/blind-nodes/alarms</c> group accepts, and the report the shell reads is the Api's own.
/// </summary>
public class NodeAlarmsEndToEndTests : IAsyncLifetime
{
    private const string Password = "nodeAlarmsEndToEnd-Password-1";

    private readonly BmbWebApplicationFactory _factory = new();
    private WebApplication _front = null!;
    private HttpClient _frontClient = null!;
    private HttpClient _apiClient = null!;

    public async Task InitializeAsync()
    {
        _apiClient = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        (await _apiClient.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var front = new NodeFront("http://127.0.0.1:1", "http://127.0.0.1:2", new Dictionary<string, ReadyFileInfo>())
        {
            // The address is a placeholder: the handler routes every request into the in-process Api.
            Alarms = new NodeAlarmsControl("http://api.invalid", BmbWebApplicationFactory.InternalKeyForTests, _factory.Server.CreateHandler())
        };
        front.RegisterServices(builder.Services);
        _front = builder.Build();
        front.MapEndpoints(_front);
        await _front.StartAsync();
        _frontClient = new HttpClient { BaseAddress = new Uri(_front.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _frontClient.Dispose();
        _apiClient.Dispose();
        await _front.StopAsync();
        await _front.DisposeAsync();
        ((IDisposable)_factory).Dispose();
    }

    private static HttpRequestMessage Alarms(string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/node/alarms");
        request.Headers.Add("X-Internal-Key", key);
        return request;
    }

    [Fact]
    public async Task TheFrontsAlarms_WithTheNodesKey_AreTheRealApisReport()
    {
        // A blind copy that declared a newer protocol: the Api raises pc_too_old for it as soon as it judges.
        var id = BlindNodeId.NewId();
        var whitelist = _factory.Services.GetRequiredService<IWhitelistRepository>();
        await whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "Blind copy", Ed25519PublicKey = new byte[32], Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await whitelist.RecordProtocolVersionAsync(id, SyncProtocolVersion.Current + 1, DateTime.UtcNow);
        await BlindNodeAlarmsTests.JudgedAlarmsAsync(_apiClient);

        using var response = await _frontClient.SendAsync(Alarms(BmbWebApplicationFactory.InternalKeyForTests));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        report.GetProperty("state").GetString().Should().Be("ok");
        var alarm = report.GetProperty("alarms").EnumerateArray().Should().ContainSingle().Subject;
        alarm.GetProperty("kind").GetString().Should().Be("pc_too_old");
        alarm.GetProperty("name").GetString().Should().Be("Blind copy", "the vault is unlocked");
    }

    [Fact]
    public async Task AWrongKey_IsRefusedByTheFront()
    {
        using var response = await _frontClient.SendAsync(Alarms("a-key-the-node-does-not-have"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
