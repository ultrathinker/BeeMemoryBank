using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>The page of "Connect a device" in the Admin area: what it is, and the way to the Connect page that does it. It has no data and no handler of its own.</summary>
public class ConnectDeviceModel(ApiClient api) : AdminPageModel(api)
{
    public void OnGet() { }
}
