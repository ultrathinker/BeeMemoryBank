using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>Branding: the product name of this node, read and saved by <c>admin.js</c> through <c>/api-proxy/branding</c>, so the page has no data and no handler.</summary>
public class BrandingModel(ApiClient api) : AdminPageModel(api)
{
    public void OnGet() { }
}
