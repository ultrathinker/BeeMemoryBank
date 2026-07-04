using Microsoft.AspNetCore.Authorization;
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
public class AIModel : PageModel
{
    public void OnGet() { }
}
