using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// <c>/Admin</c>: the list of the pages of the Admin area (<see cref="AdminPageCatalog"/>), grouped under headings. It loads only what it warns about: a master
/// password another node changed, data-key rotations other nodes started, devices a restore left waiting. Everything else is on the page of its block.
/// </summary>
public class IndexModel(ApiClient api) : AdminPageModel(api)
{
    /// <summary>Devices a restore kept inactive until a superadmin confirms them (no anchor vouched for them).</summary>
    public List<RestoredPeerDto> RestoredPeers { get; private set; } = [];

    public async Task OnGetAsync()
    {
        await Task.WhenAll(
            LoadMasterPasswordNoticeAsync(),
            LoadPeerPendingRotationsAsync(),
            Api.GetRestoredPeersAsync().ContinueWith(t => RestoredPeers = t.Result));
    }

    public Task<IActionResult> OnPostDismissPasswordNoticeAsync() => DismissPasswordNoticeAsync();

    public Task<IActionResult> OnPostPeerAcceptDekRotationAsync(string eventId) => PeerAcceptDekRotationAsync(eventId);

    public Task<IActionResult> OnPostPeerRejectDekRotationAsync(string eventId) => PeerRejectDekRotationAsync(eventId);
}
