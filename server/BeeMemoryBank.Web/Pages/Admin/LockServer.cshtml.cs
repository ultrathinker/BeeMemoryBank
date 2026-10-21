using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

public class LockServerModel(ApiClient api) : AdminPageModel(api)
{
    /// <summary>
    /// What can put the master DEK back after a Lock (GET /api/session/lock-impact). Null when the API could not answer: the page then says nothing
    /// rather than claiming the node is safe.
    /// </summary>
    public LockImpactDto? LockImpact { get; private set; }

    public async Task OnGetAsync() => LockImpact = await Api.GetLockImpactAsync();
}
