using System.Security.Claims;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

public class LoginModel(ApiClient api) : PageModel
{
    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    [BindProperty]
    public string ReturnUrl { get; set; } = "/Tree";

    public string? ErrorMessage { get; set; }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? "/Tree";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter username and password.";
            return Page();
        }

        var result = await api.LoginAsync(Username, Password);

        if (!result.Success)
        {
            if (result.IsLocked)
                ErrorMessage = "Server is locked. Contact administrator.";
            else
                ErrorMessage = result.Error ?? "Invalid username or password.";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, result.Username!),
            new(ClaimTypes.Role, result.Role!),
            new("DisplayName", result.DisplayName ?? result.Username!),
            new("UserId", result.UserId ?? "")
        };
        var identity = new ClaimsIdentity(claims, "BeeWebCookie");
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync("BeeWebCookie", principal,
            new AuthenticationProperties { IsPersistent = true });

        if (!string.IsNullOrEmpty(ReturnUrl) && !ReturnUrl.StartsWith("/"))
            ReturnUrl = "/Tree";
        return LocalRedirect(string.IsNullOrEmpty(ReturnUrl) ? "/Tree" : ReturnUrl);
    }
}
