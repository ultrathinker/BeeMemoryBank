using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// Admin -> Dream (plan 3.9): the schedule, the limits, the model, the excluded folders and the dismiss rules of Dream. Read and saved by the
/// page's script through <c>/api-proxy/dream/settings</c> and <c>/api-proxy/dream/rules</c> (plan 3.8): the server draws the form, the script fills
/// it, so no handler of this page changes anything.
/// </summary>
public class DreamModel(ApiClient api) : AdminPageModel(api)
{
    /// <summary><c>dream-admin.js</c> fills the form, saves it and keeps the rules table (the busy helper's guard watches every send it makes).</summary>
    public override IReadOnlyList<string> PageScripts => ["/js/pages/dream-admin.js"];

    public void OnGet() { }
}
