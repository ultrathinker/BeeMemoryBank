using System.Net.Http.Json;

namespace BeeMemoryBank.Web.Services;

/// <summary>One way to reach a peer (the Api's <c>PeerChannelDto</c>).</summary>
public sealed record PeerChannelDto(
    string Address, string Source, string Scope, bool OnCurrentNetwork, bool Dialled, bool UsedFirst, bool ThisDeviceOnly,
    DateTime? LastOkAt, DateTime? LastFailAt, string? LastFailKind);

/// <summary>Every way this node knows to reach a peer (<c>GET /api/whitelist/{id}/channels</c>).</summary>
public sealed record PeerChannelsDto(Guid NodeId, List<PeerChannelDto> Channels, bool CaPinned, DateTime? AnnouncedAt);

public partial class ApiClient
{
    /// <summary>The ways to reach a peer (docs/design/lan-peer-sync.md 15.1), or null when the Api does not know the node.</summary>
    public async Task<PeerChannelsDto?> GetPeerChannelsAsync(Guid nodeId)
    {
        var resp = await http.GetAsync($"/api/whitelist/{nodeId}/channels");
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<PeerChannelsDto>(JsonOpts) : null;
    }

    /// <summary>
    /// Adds an address for a peer on this node only. <paramref name="saveWithoutAnswer"/>: keep it although nothing answers there now. NeedsConfirmation is
    /// true when the Api asks for that (422: nothing answered, or it is not a node).
    /// </summary>
    public async Task<(bool Ok, string? Error, bool NeedsConfirmation)> AddPeerChannelAsync(Guid nodeId, string address, bool saveWithoutAnswer)
    {
        var resp = await http.PostAsync($"/api/whitelist/{nodeId}/channels", Body(new { address, saveWithoutAnswer }));
        if (resp.IsSuccessStatusCode) return (true, null, false);
        return (false, await ReadErrorAsync(resp), (int)resp.StatusCode == 422);
    }

    /// <summary>Removes an address typed for a peer on this node.</summary>
    public async Task<(bool Ok, string? Error)> RemovePeerChannelAsync(Guid nodeId, string address)
    {
        var resp = await http.DeleteAsync($"/api/whitelist/{nodeId}/channels?address={Uri.EscapeDataString(address)}");
        return resp.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(resp));
    }
}
