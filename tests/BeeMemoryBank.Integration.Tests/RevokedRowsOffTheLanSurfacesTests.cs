using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A revoked row, and since the arrival-order fix a tombstone (a revoked row for a node this node only heard of through a revoke: no name, no
/// address, maybe no key), is never a peer. The surfaces of LAN sync and of Admin &gt; Devices read active rows only: the device list and the
/// delivery status, the network map, the channels of a device ("Ways to reach it", "Add an address"), and the full-peer alarm.
/// </summary>
public sealed class RevokedRowsOffTheLanSurfacesTests : IAsyncLifetime
{
    private const string Password = "testPassword";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BmbWebApplicationFactory _node = new();
    private HttpClient _admin = null!;
    private readonly Guid _kept = Guid.NewGuid();
    private readonly Guid _revoked = Guid.NewGuid();
    private readonly Guid _tombstone = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _node.InitializeNodeAsync("Home", Password);
        _admin = _node.CreateClient();
        (await _admin.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();

        using var scope = _node.Services.CreateScope();
        var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        foreach (var (id, name) in new[] { (_kept, "Kept laptop"), (_revoked, "Revoked laptop") })
            await whitelist.CreateAsync(new WhitelistEntry
            {
                NodeId = id, DisplayName = name, Ed25519PublicKey = new byte[32], ApiAddress = "https://192.168.1.50:5311",
                Status = WhitelistStatuses.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        await whitelist.RevokeAsync(_revoked, new RowVersion(1_000_000, Guid.NewGuid()));
        (await whitelist.CreateTombstoneAsync(_tombstone, new RowVersion(1_000_001, Guid.NewGuid()))).Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _node.Dispose();
        return Task.CompletedTask;
    }

    private async Task<List<Guid>> IdsAsync(string url, Func<JsonElement, IEnumerable<JsonElement>> rows)
    {
        var body = await _admin.GetFromJsonAsync<JsonElement>(url, Json);
        return rows(body).Select(r => r.GetProperty("nodeId").GetGuid()).ToList();
    }

    [Fact]
    public async Task TheDeviceList_TheDeliveryStatus_AndTheNetworkMap_ShowTheActivePeerOnly()
    {
        var lists = new Dictionary<string, List<Guid>>
        {
            ["/api/whitelist"] = await IdsAsync("/api/whitelist", b => b.EnumerateArray()),
            ["/api/whitelist/sync-status"] = await IdsAsync("/api/whitelist/sync-status", b => b.EnumerateArray()),
            ["/api/sync/network"] = await IdsAsync("/api/sync/network", b => b.GetProperty("peers").EnumerateArray()),
        };

        foreach (var (url, ids) in lists)
        {
            ids.Should().Contain(_kept, url);
            ids.Should().NotContain([_revoked, _tombstone], $"{url} lists peers, and a revoked row or a tombstone is none");
        }
    }

    [Fact]
    public async Task TheChannelsOfARevokedRowOrATombstone_AreNotThere_AndNoAddressCanBeAddedForIt()
    {
        (await _admin.GetAsync($"/api/whitelist/{_kept}/channels")).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var id in new[] { _revoked, _tombstone })
        {
            (await _admin.GetAsync($"/api/whitelist/{id}/channels")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await _admin.PostAsJsonAsync($"/api/whitelist/{id}/channels", new { address = "192.168.1.60", saveWithoutAnswer = true }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task NoAlarm_NamesARevokedRowOrATombstone()
    {
        var alarms = await _admin.GetFromJsonAsync<JsonElement>("/api/blind-nodes/alarms", Json);

        alarms.ToString().Should().NotContain(_revoked.ToString()).And.NotContain(_tombstone.ToString());
    }
}
