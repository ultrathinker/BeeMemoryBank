using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>The page of "Blind nodes" in the Admin area: what a blind node is, and the way to the page that adds and manages them. It has no data and no handler of its own.</summary>
public class BlindNodesModel(ApiClient api) : AdminPageModel(api)
{
    public void OnGet() { }
}
