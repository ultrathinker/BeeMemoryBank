using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>The web session settings: read and saved by <c>admin.js</c> through <c>/api-proxy/session/settings</c>, so the page has no data and no handler.</summary>
public class WebSessionModel(ApiClient api) : AdminPageModel(api)
{
    public void OnGet() { }
}
