using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>The recovery key: its one button is a script (<c>admin.js</c> asks <c>/api-proxy/keys/add-recovery</c> and shows the key once), so the page has no data and no handler.</summary>
public class RecoveryKeyModel(ApiClient api) : AdminPageModel(api)
{
    public void OnGet() { }
}
