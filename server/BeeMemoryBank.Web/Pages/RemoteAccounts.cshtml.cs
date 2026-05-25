using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize]
public class RemoteAccountsModel : PageModel
{
    public void OnGet() { }
}
