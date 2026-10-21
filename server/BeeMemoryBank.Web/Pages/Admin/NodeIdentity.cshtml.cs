using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

public class NodeIdentityModel(ApiClient api) : AdminPageModel(api)
{
    public NodeIdentityDto? Identity { get; private set; }

    public async Task OnGetAsync() => Identity = await Api.GetIdentityAsync();
}
