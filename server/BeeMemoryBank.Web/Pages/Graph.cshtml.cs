using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize]
public class GraphModel : PageModel
{
    public string? FocusConcept { get; private set; }

    public IActionResult OnGet(string? concept = null)
    {
        FocusConcept = concept;
        return Page();
    }
}
