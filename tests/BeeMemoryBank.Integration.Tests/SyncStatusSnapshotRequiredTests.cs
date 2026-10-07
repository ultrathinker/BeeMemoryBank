using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-81: the scheduler's "a full peer compacted past us" state reaches the Web UI through /api/sync/status (the layout
/// shows its sentence); nothing used to read it.
/// </summary>
public class SyncStatusSnapshotRequiredTests
{
    [Fact]
    public async Task SyncStatus_SaysTheNodeCannotCatchUp_UntilASyncSucceeds()
    {
        using var node = new BmbWebApplicationFactory();
        await node.InitializeNodeAsync("Node", "syncStatusPassword1");
        using var client = node.CreateClient();
        var state = node.Services.GetRequiredService<SnapshotRequiredState>();

        (await StatusAsync(client)).GetProperty("snapshotRequired").ValueKind.Should().Be(JsonValueKind.Null);

        state.Set(new SnapshotRequiredException("https://host.example", 8, 14, "410"));
        var required = (await StatusAsync(client)).GetProperty("snapshotRequired");
        required.GetProperty("lastCompactionCp").GetInt64().Should().Be(8);
        required.GetProperty("message").GetString().Should().Contain("cannot catch up").And.Contain("Wipe this node and join again");

        state.Clear();
        (await StatusAsync(client)).GetProperty("snapshotRequired").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static async Task<JsonElement> StatusAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/sync/status");
}
