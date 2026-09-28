using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>The report of the last offline re-key; Desktop opens it after the first start on the new vault.</summary>
[Authorize(Roles = "superadmin")]
public class RekeyReportModel : PageModel
{
    public void OnGet()
    {
    }
}
