using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// Native AI chat page (Phase 1 + Phase 2). The page is intentionally thin — all chat logic lives
/// in wwwroot/js/chat.js. Phase 2 streams turns from /api-proxy/chat/stream (a dedicated SSE
/// passthrough to the Api /api/chat/stream tool-loop endpoint) and renders a persisted-history
/// sidebar (conversations list / load / rename / delete). Open to any authenticated user; the Api
/// enforces session.IsUnlocked (409 when locked) and ACL via the ambient CallerScope.
/// </summary>
[Authorize]
public class AIModel(ApiClient api) : PageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        // Defense-in-depth: even if the nav link is hidden, this page is reachable directly by URL.
        // Ask the API whether this caller may use chat; if not, redirect to the homepage. The API
        // filter is the real boundary — this just avoids rendering a dead chat UI for a blocked user.
        var f = await api.ForwardGetAsync("chat/access");
        if (f.Status == 200)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(f.Body);
                if (doc.RootElement.TryGetProperty("allowed", out var allowedProp) && !allowedProp.GetBoolean())
                    return RedirectToPage("/Tree");
            }
            catch { /* malformed response — fail open, matches other best-effort checks here */ }
        }
        return Page();
    }
}
