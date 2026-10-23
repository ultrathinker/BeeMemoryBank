using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class TrustedNodesModel(ApiClient api) : AdminPageModel(api)
{
    public NodeIdentityDto? Identity { get; private set; }
    public List<WhitelistEntryDto>? Whitelist { get; private set; }
    public Dictionary<Guid, NodeContactDto>? NodeSyncStatus { get; private set; }

    /// <summary>The devices the card "Grant superadmin to devices joined with the password" offers (BMB-42); the card is shown only while there are any.</summary>
    public IReadOnlyList<WhitelistEntryDto> PasswordPeersToPromote => CandidatesOf(Whitelist);

    /// <summary>
    /// Active peers that are not superadmins and not blind nodes (a blind node never is one): devices that joined before "whoever knows the master password is a
    /// superadmin" and are still recorded as plain peers, so their hard deletes and password notices are refused by the mesh.
    /// </summary>
    public static IReadOnlyList<WhitelistEntryDto> CandidatesOf(IEnumerable<WhitelistEntryDto>? whitelist) =>
        (whitelist ?? [])
            .Where(e => e.Status == "A" && !e.IsSuperadmin && !BlindNodeId.IsBlind(e.NodeId))
            .ToList();

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

    /// <summary>
    /// BMB-42: promotes, on an explicit confirm, the devices ticked on the card - each through the ordinary <c>PUT /api/whitelist/{nodeId}/superadmin</c>, one
    /// <c>whitelist_update</c> per device, so the rest of the mesh agrees. There is no silent migration: a row recorded content-only by the old default looks
    /// exactly like one an admin demoted on purpose, so a person decides.
    /// </summary>
    public async Task<IActionResult> OnPostPromotePasswordPeersAsync(List<Guid> nodeIds)
    {
        // Promote only what is STILL a candidate: a device revoked, or promoted elsewhere, since the
        // list was shown is skipped rather than acted on from a stale form.
        var current = CandidatesOf(await Api.GetWhitelistAsync()).Select(c => c.NodeId).ToHashSet();
        var failed = new List<string>();
        var promoted = 0;
        foreach (var nodeId in nodeIds.Where(current.Contains))
        {
            var (ok, error) = await Api.SetPeerSuperadminAsync(nodeId, isSuperadmin: true);
            if (ok) promoted++;
            else failed.Add($"{nodeId.ToString()[..8]}: {error}");
        }

        return failed.Count == 0
            ? Back(msg: $"{promoted} device(s) granted superadmin.")
            : Back(err: $"{promoted} granted; failed: {string.Join("; ", failed)}");
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
