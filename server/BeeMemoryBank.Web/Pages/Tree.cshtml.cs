using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize]
public class TreeModel(ApiClient api) : PageModel
{
    /// <summary>The "no recovery key has been issued" notice of an upgraded node (release 2.7.5); null when the node did not answer.</summary>
    public RecoveryKeyStatusDto? RecoveryKeyStatus { get; private set; }

    public async Task OnGetAsync() => RecoveryKeyStatus = await api.GetRecoveryKeyStatusAsync();
}
