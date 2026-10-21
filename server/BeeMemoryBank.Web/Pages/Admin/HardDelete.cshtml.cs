using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>The page of "Hard Delete" in the Admin area: what it is, and the way to the page that does it. It has no data and no handler of its own.</summary>
public class HardDeleteModel(ApiClient api) : AdminPageModel(api)
{
    public void OnGet() { }
}
