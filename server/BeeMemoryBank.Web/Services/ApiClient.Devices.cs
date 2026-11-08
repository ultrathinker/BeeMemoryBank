using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Web.Models;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    /// <summary>
    /// The network as this node knows it: this node and each peer with its kind, platform, version and reachability. Null when the node did not answer; the Devices
    /// pages then say so instead of showing an empty list.
    /// </summary>
    public async Task<NetworkDto?> GetNetworkAsync()
    {
        try
        {
            var resp = await http.GetAsync("/api/sync/network");
            return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<NetworkDto>(JsonOpts) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    /// <summary>The delivery status of each peer (the header's list): the page of a device reads from it the peer that last saw the device. Null when the node did not answer.</summary>
    public async Task<DeliveryStatusDto?> GetDeliveryStatusAsync()
    {
        try
        {
            var resp = await http.GetAsync("/api/sync/delivery-status");
            return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<DeliveryStatusDto>(JsonOpts) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }
}
