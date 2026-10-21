using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class RestoredDevicesModel(ApiClient api) : AdminPageModel(api)
{
    /// <summary>Devices a restore kept inactive until a superadmin confirms them (no anchor vouched for them).</summary>
    public List<RestoredPeerDto> RestoredPeers { get; private set; } = [];

    public async Task OnGetAsync() => RestoredPeers = await Api.GetRestoredPeersAsync();

    public async Task<IActionResult> OnPostConfirmRestoredPeerAsync(Guid nodeId) =>
        await Api.ConfirmRestoredPeerAsync(nodeId)
            ? Back(msg: "Device confirmed: it syncs with this one again. Promote it separately if it should be a superadmin.")
            : Back(err: "Could not confirm the device.");
}
