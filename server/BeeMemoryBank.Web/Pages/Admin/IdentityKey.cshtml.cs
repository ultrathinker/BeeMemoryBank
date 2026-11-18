using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// "Node identity key" (release 2.12, plan WS-D): this node's identity key fingerprint, the keys it retired, the devices that hold a re-key
/// back, identity conflicts of other nodes, and "Give this node a new identity key".
/// </summary>
public class IdentityKeyModel(ApiClient api) : AdminPageModel(api)
{
    public NodeIdentityKeyDto? Identity { get; private set; }

    public async Task OnGetAsync() => Identity = await Api.GetNodeIdentityKeyAsync();

    public async Task<IActionResult> OnPostAcknowledgeAsync(Guid nodeId)
    {
        if (nodeId == Guid.Empty) return Back(err: "Pick the device whose changes you have looked at.");
        var (ok, err) = await Api.AcknowledgeRetiredKeyAttentionAsync(nodeId);
        return ok ? Back(msg: "Noted. The warning is gone until it happens again.") : Back(err: err ?? "It could not be acknowledged.");
    }

    public async Task<IActionResult> OnPostAcknowledgeSignedAsThisNodeAsync()
    {
        var (ok, err) = await Api.AcknowledgeSignedAsThisNodeAsync();
        return ok ? Back(msg: "Noted. The warning is gone until it happens again.") : Back(err: err ?? "It could not be acknowledged.");
    }

    public async Task<IActionResult> OnPostConfirmAsync(Guid nodeId, Guid rotationEventId, bool confirm)
    {
        if (!confirm || nodeId == Guid.Empty || rotationEventId == Guid.Empty)
            return Back(err: "Pick the key the device shows and tick the box to confirm that you checked it on the device itself.");
        var (ok, err) = await Api.ConfirmNodeKeyAsync(nodeId, rotationEventId);
        return ok
            ? Back(msg: "The conflict is settled. The device's changes are accepted again under the key you confirmed.")
            : Back(err: err ?? "The conflict could not be settled.");
    }

    public async Task<IActionResult> OnPostRekeyAsync(string? reason, bool confirm)
    {
        if (!confirm) return Back(err: "Tick the box to confirm that this node gets a new identity key.");
        var (ok, fingerprint, err) = await Api.RekeyNodeAsync(reason);
        return ok
            ? Back(msg: $"This node has a new identity key ({fingerprint}). Your other devices switch to it as they sync.")
            : Back(err: err ?? "The node could not get a new identity key.");
    }
}
