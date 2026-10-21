using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class TrustedNodesModel(ApiClient api) : AdminPageModel(api)
{
    public NodeIdentityDto? Identity { get; private set; }
    public List<WhitelistEntryDto>? Whitelist { get; private set; }
    public Dictionary<Guid, NodeContactDto>? NodeSyncStatus { get; private set; }

    public async Task OnGetAsync()
    {
        await Task.WhenAll(
            Api.GetIdentityAsync().ContinueWith(t => Identity = t.Result),
            Api.GetWhitelistAsync().ContinueWith(t => Whitelist = t.Result),
            Api.GetNodeSyncStatusAsync().ContinueWith(t => NodeSyncStatus = t.Result));
    }

    public async Task<IActionResult> OnPostSetPeerSuperadminAsync(Guid nodeId, bool isSuperadmin)
    {
        var (ok, error) = await Api.SetPeerSuperadminAsync(nodeId, isSuperadmin);
        return ok
            ? Back(msg: isSuperadmin
                ? "Peer promoted to superadmin."
                : "Peer demoted. It can no longer revoke peers, hard-delete content or restore the network.")
            : Back(err: error ?? "Failed to change peer authority");
    }

    public async Task<IActionResult> OnPostRevokeNodeAsync(Guid nodeId)
    {
        var ok = await Api.RevokeNodeAsync(nodeId);
        return ok
            ? Back(msg: "Node access revoked")
            : Back(err: "Failed to revoke node access");
    }

    public async Task<IActionResult> OnPostChangeNodeAddressAsync(Guid nodeId, string newApiAddress, string password)
    {
        var (ok, error) = await Api.ChangeNodeAddressAsync(nodeId, newApiAddress, password);
        return ok
            ? Back(msg: "Node address updated")
            : Back(err: error ?? "Failed to update node address");
    }

    /// <summary>"Let blind copies call this node": the Api verifies the address and the certificate and publishes the row.</summary>
    public async Task<IActionResult> OnPostSetBlindCallableAsync(Guid nodeId, string trust, string? address, string password, string? expectedPin)
    {
        var (result, error) = await Api.SetBlindCallableAsync(nodeId, trust, address, password, expectedPin);
        if (result is null) return Back(err: error ?? "Could not change how blind copies reach this node.");
        var done = trust == "off" ? "Blind copies can no longer be paired to call this node." : "Blind copies can now be paired to call this node.";
        return Back(msg: string.IsNullOrEmpty(result.Notice) ? done : done + " " + result.Notice);
    }

    public async Task<IActionResult> OnPostSetAutoAcceptRestoreAsync(Guid nodeId, bool autoAccept)
    {
        var (ok, error) = await Api.SetAutoAcceptRestoreAsync(nodeId, autoAccept);
        if (!ok)
            return Back(err: "Failed to update auto-accept setting: " + (error ?? "unknown"));
        return Back(msg: autoAccept
            ? "Auto-accept restore enabled — restores from this peer will apply automatically."
            : "Auto-accept restore disabled — restores from this peer remain pending; there is no manual approval prompt.");
    }

    public async Task<IActionResult> OnPostSetAutoAcceptDekRotationAsync(Guid nodeId, bool autoAccept)
    {
        var (ok, error) = await Api.SetAutoAcceptDekRotationAsync(nodeId, autoAccept);
        if (!ok)
            return Back(err: "Failed to update auto-accept DEK rotation setting: " + (error ?? "unknown"));
        return Back(msg: autoAccept
            ? "Auto-accept DEK rotation enabled — DEK rotations from this peer will apply automatically."
            : "Auto-accept DEK rotation disabled — DEK rotations from this peer will require manual approval.");
    }
}
