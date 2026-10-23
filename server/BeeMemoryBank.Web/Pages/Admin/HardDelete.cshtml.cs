using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// "Hard Delete" in the Admin area: the list of articles and folders with its filter, the confirmation of each deletion (which first shows what it will remove) and
/// the audit log. The list, the preview, the deletion and the log are loaded and sent by the script of the page (<c>hard-delete.js</c>) through the proxy, which
/// checks the superadmin and the open session on every call; the page itself has no data and no handler of its own.
/// </summary>
public class HardDeleteModel(ApiClient api) : AdminPageModel(api)
{
    public override IReadOnlyList<string> PageScripts => ["/js/pages/hard-delete.js"];

    public void OnGet() { }
}
