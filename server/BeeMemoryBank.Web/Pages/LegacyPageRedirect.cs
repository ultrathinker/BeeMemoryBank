using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// An address of an earlier version whose page now lives in the Admin area (release 2.7.2: a page of the Admin area holds its whole function, no "second page" behind
/// it). The old address stays for bookmarks, the first-run page and the apps: a GET answers a temporary redirect (302) to the new page, with the query it came with
/// (<c>msg</c>, <c>err</c>, ...). It has no handler for anything else: the forms post to the new page. Superadmin only, as the page was: the answer to anybody else is
/// the same as before.
/// </summary>
[Authorize(Roles = "superadmin")]
public abstract class LegacyPageRedirectModel(string target, string? fragment = null) : PageModel
{
    public IActionResult OnGet() => new RedirectResult(Address(Request.Query, target, fragment), permanent: false);

    /// <summary>The new address: <paramref name="target"/>, the query of the old one (without a <c>handler</c>, which names a handler of the old page) and the fragment.</summary>
    public static string Address(IQueryCollection query, string target, string? fragment = null)
    {
        var kept = query.Where(q => !string.Equals(q.Key, "handler", StringComparison.OrdinalIgnoreCase));
        return target + QueryString.Create(kept).ToUriComponent() + (string.IsNullOrEmpty(fragment) ? "" : "#" + fragment);
    }
}
