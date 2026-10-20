using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// The network map (BMB-322): the page the header's "Sync Status" opens. It draws this node and the nodes it knows on a circle, with an arrow for every call. The
/// page itself holds nothing: the script asks <c>/api-proxy/sync/network</c>, which the API serves to a superadmin only, like the sync status it extends.
/// </summary>
[Authorize(Roles = "superadmin")]
public class NetworkModel : PageModel
{
    public void OnGet() { }
}
