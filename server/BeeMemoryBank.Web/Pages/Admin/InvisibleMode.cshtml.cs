using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class InvisibleModeModel(ApiClient api) : AdminPageModel(api)
{
    /// <summary>Invisible Mode (BMB-313: it lives here, not in the header): this node calls nobody and answers nobody. Null when the node did not say.</summary>
    public bool? IsInvisible { get; private set; }

    public async Task OnGetAsync() => IsInvisible = await Api.GetInvisibleModeAsync();

    /// <summary>Invisible Mode on or off (superadmin, like the page). On, this node rejects incoming sync calls and stops its own; the sync icon shows a badge.</summary>
    public async Task<IActionResult> OnPostSetInvisibleModeAsync(bool enabled)
    {
        var (ok, error) = await Api.SetInvisibleModeAsync(enabled);
        if (!ok) return Back(err: error ?? "Could not change Invisible Mode.");
        return Back(msg: enabled
            ? "Invisible Mode is on: this node calls nobody and rejects incoming sync calls. The sync icon shows a badge while it is on."
            : "Invisible Mode is off: this node syncs with its peers again.");
    }
}
