using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// <c>/Admin/Device/{nodeId}</c> (docs/design/lan-peer-sync.md 15.1): one page for one peer. It replaces the row actions of Trusted Nodes and the card of the Blind nodes
/// page: the node id and key, Revoke, the status line, the ways to reach the device (its channels, "Add an address", "Change internet address"), the devices that
/// relay for it, what it may do on its own, how its certificate is trusted, whether blind copies may call it, and for a blind node its reseed and alarms.
/// </summary>
public class DeviceModel(ApiClient api) : AdminPageModel(api)
{
    /// <summary>The device this page is about (the route).</summary>
    [BindProperty(Name = "nodeId", SupportsGet = true)]
    public Guid NodeId { get; set; }

    /// <summary>The row of the whitelist; null when the device is not in the list (revoked, or never was).</summary>
    public WhitelistEntryDto? Entry { get; private set; }

    /// <summary>What this node knows about the device: kind, platform, version, reachability. Null when the node did not answer.</summary>
    public NetworkNodeDto? Peer { get; private set; }

    /// <summary>The status line of the device, as the list says it.</summary>
    public DeviceRow? Row { get; private set; }

    /// <summary>The peer that last saw this device, when this node cannot reach it itself.</summary>
    public string? ReachableVia { get; private set; }

    /// <summary>The ways to reach a peer: its own address, what it announced, what mDNS showed, what was typed here. Null for a blind node and when the Api has none.</summary>
    public PeerChannelsDto? Channels { get; private set; }

    /// <summary>What the Api says about a blind node: protocol, contact and the alarms it judged.</summary>
    public BlindNodeDto? Blind { get; private set; }

    public bool IsBlind => BlindNodeId.IsBlind(NodeId);

    public bool NetworkUnavailable => Peer is null;

    public async Task<IActionResult> OnGetAsync()
    {
        var whitelist = Api.GetWhitelistAsync();
        var network = Api.GetNetworkAsync();
        var delivery = Api.GetDeliveryStatusAsync();
        var blind = IsBlind ? Api.ListBlindNodesAsync() : Task.FromResult<List<BlindNodeDto>?>(null);
        try { Entry = (await whitelist)?.FirstOrDefault(e => e.NodeId == NodeId); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Entry = null; }
        var net = await network;
        if (net is not null && net.Self.NodeId == NodeId) return Redirect(AdminRoutes.Devices + "#this-device");
        Peer = net?.Peers.FirstOrDefault(p => p.NodeId == NodeId);
        Blind = (await blind)?.FirstOrDefault(b => b.NodeId == NodeId);
        ReachableVia = (await delivery)?.Nodes.FirstOrDefault(n => n.NodeId == NodeId)?.ReachableVia;
        if (Peer is not null) Row = DeviceRows.For(Peer, Blind?.Alarms, DateTime.UtcNow);

        if (Entry is not null && !IsBlind)
        {
            try { Channels = await Api.GetPeerChannelsAsync(NodeId); }
            catch (HttpRequestException) { /* an older Api: the page shows the address on record only */ }
        }
        return Page();
    }

    /// <summary>Ends a handler: this device again, with the message of the action.</summary>
    protected override IActionResult Back(string? msg = null, string? err = null, string? detail = null) =>
        RedirectToPage(null, null, new { nodeId = NodeId, msg, err, detail });

    // ---- the ways to reach it ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>"Add an address" for a peer, on this node only (docs/design/lan-peer-sync.md 15.3): checked by the policy, then asked who it is.</summary>
    public async Task<IActionResult> OnPostAddPeerChannelAsync(string address, bool saveWithoutAnswer)
    {
        var (ok, error, needsConfirmation) = await Api.AddPeerChannelAsync(NodeId, address, saveWithoutAnswer);
        if (ok) return Back(msg: "The address is saved for this device on this node only. It is tried whenever this computer is on its network.");
        return Back(err: needsConfirmation ? error + " Tick \"Save it even if nothing answers now\" to keep it." : error ?? "The address could not be added.");
    }

    /// <summary>Removes an address typed for a peer.</summary>
    public async Task<IActionResult> OnPostRemovePeerChannelAsync(string address)
    {
        var (ok, error) = await Api.RemovePeerChannelAsync(NodeId, address);
        return ok ? Back(msg: "The address is removed.") : Back(err: error ?? "The address could not be removed.");
    }

    /// <summary>"Change internet address": the address on record for every node of the network, announced when this node is a superadmin. Needs the master password.</summary>
    public async Task<IActionResult> OnPostChangeNodeAddressAsync(string newApiAddress, string password)
    {
        var (ok, error, code) = await Api.ChangeNodeAddressAsync(NodeId, newApiAddress, password);
        return ok
            ? Back(msg: "Node address updated")
            : Back(err: error ?? "Failed to update node address", detail: code);
    }

    // ---- what it may do ----------------------------------------------------------------------------------------------------------------------------------

    public async Task<IActionResult> OnPostSetPeerSuperadminAsync(bool isSuperadmin)
    {
        var (ok, error) = await Api.SetPeerSuperadminAsync(NodeId, isSuperadmin);
        return ok
            ? Back(msg: isSuperadmin
                ? "Peer promoted to superadmin."
                : "Peer demoted. It can no longer revoke peers, hard-delete content or restore the network.")
            : Back(err: error ?? "Failed to change peer authority");
    }

    public async Task<IActionResult> OnPostSetAutoAcceptRestoreAsync(bool autoAccept)
    {
        var (ok, error) = await Api.SetAutoAcceptRestoreAsync(NodeId, autoAccept);
        if (!ok)
            return Back(err: "Failed to update auto-accept setting: " + (error ?? "unknown"));
        return Back(msg: autoAccept
            ? "Auto-accept restore enabled — restores from this peer will apply automatically."
            : "Auto-accept restore disabled — restores from this peer remain pending; there is no manual approval prompt.");
    }

    public async Task<IActionResult> OnPostSetAutoAcceptDekRotationAsync(bool autoAccept)
    {
        var (ok, error) = await Api.SetAutoAcceptDekRotationAsync(NodeId, autoAccept);
        if (!ok)
            return Back(err: "Failed to update auto-accept DEK rotation setting: " + (error ?? "unknown"));
        return Back(msg: autoAccept
            ? "Auto-accept DEK rotation enabled — DEK rotations from this peer will apply automatically."
            : "Auto-accept DEK rotation disabled — DEK rotations from this peer will require manual approval.");
    }

    /// <summary>"Let blind copies call this node": the Api verifies the address and the certificate and publishes the row.</summary>
    public async Task<IActionResult> OnPostSetBlindCallableAsync(string trust, string? address, string password, string? expectedPin)
    {
        var (result, error) = await Api.SetBlindCallableAsync(NodeId, trust, address, password, expectedPin);
        if (result is null) return Back(err: error ?? "Could not change how blind copies reach this node.");
        var done = trust == "off" ? "Blind copies can no longer be paired to call this node." : "Blind copies can now be paired to call this node.";
        return Back(msg: string.IsNullOrEmpty(result.Notice) ? done : done + " " + result.Notice);
    }

    // ---- revoking, and the blind node's reseed ----------------------------------------------------------------------------------------------------------

    /// <summary>Revokes the device. The page of a revoked device is gone: the answer is the list.</summary>
    public async Task<IActionResult> OnPostRevokeNodeAsync()
    {
        bool ok;
        try
        {
            ok = await Api.RevokeNodeAsync(NodeId);
        }
        catch (Exception ex) when (IsBlind && ex is HttpRequestException or TaskCanceledException)
        {
            var down = BlindNodeErrors.AppDidNotAnswer(ex);
            return Back(err: down.Message, detail: down.Detail);
        }
        if (ok)
            return RedirectToPage("Devices", new { msg = IsBlind ? "Blind node disconnected. Wipe it on its own console." : "Node access revoked" });
        return Back(err: IsBlind ? BlindNodeErrors.ForDisconnect().Message : "Failed to revoke node access");
    }

    /// <summary>Sends a blind node a fresh copy.</summary>
    public async Task<IActionResult> OnPostReseedAsync()
    {
        var (ok, error) = await Api.ReseedBlindNodeAsync(NodeId);
        return ok
            ? Back(msg: "Blind node reseeded.")
            : Back(err: error!.Message, detail: error.Detail);
    }
}
