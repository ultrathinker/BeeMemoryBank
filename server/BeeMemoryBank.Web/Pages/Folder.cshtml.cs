using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize]
public class FolderModel(ApiClient api) : PageModel
{
    public string CurrentPath { get; private set; } = "/";
    public string FolderName { get; private set; } = "/";
    public TreeChildrenDto? Children { get; private set; }
    public async Task<IActionResult> OnGetAsync(string? path = "/")
    {
        CurrentPath = string.IsNullOrEmpty(path) ? "/" : path;
        FolderName = CurrentPath == "/" ? "Root" : CurrentPath.Split('/').Last(s => s.Length > 0);

        Children = await api.GetChildrenAsync(CurrentPath);

        return Page();
    }
}
