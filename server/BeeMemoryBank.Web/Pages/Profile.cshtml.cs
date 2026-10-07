using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize]
public class ProfileModel(IConfiguration configuration) : PageModel
{
    public string Username => User.Identity?.Name ?? "";
    public string DisplayName => User.FindFirst("DisplayName")?.Value ?? Username;
    public string Role => User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "user";

    // The MCP address for the ready-to-paste connection snippets shown when an agent key is created.
    // /mcp is served by the API, not by this page, so the address the user browses from is only
    // right when the node's front door (desktop, service, Mac) or a reverse proxy puts /mcp there;
    // McpAddressResolver decides, and says so in words when no address a client can use is known
    // (plain Docker, where the API port is not even published) instead of printing a dead URL.
    public McpAddress Mcp => McpAddressResolver.Resolve(new McpAddressInputs(
        ConfiguredUrl: configuration["BMB_MCP_URL"],
        BehindNodeFront: !string.IsNullOrEmpty(configuration["BMB_BEHIND_LOOPBACK_PROXY"]),
        ViaReverseProxy: ArrivedThroughProxy(HttpContext.Request),
        PublishedApiPort: configuration["BMB_API_PUBLISHED_PORT"],
        BrowseOrigin: BrowseOrigin(HttpContext.Request)));

    // Base origin the user is currently browsing from (scheme://host[:port]). Honours
    // X-Forwarded-Proto so it is correct behind a reverse proxy with TLS termination, mirroring
    // the pattern in Shared/_HttpsRequiredAlert.cshtml.
    public static string BrowseOrigin(HttpRequest request)
    {
        var forwarded = request.Headers["X-Forwarded-Proto"].FirstOrDefault()?.Split(',')[0].Trim();
        var scheme = forwarded is "http" or "https" ? forwarded : request.Scheme;
        return $"{scheme}://{request.Host}";
    }

    // A proxy in front of the page announces itself in these headers; with trusted proxies the
    // forwarded-headers middleware moves them to X-Original-*, so look for both. Only used to
    // choose which address to print, never to decide anything about access.
    public static bool ArrivedThroughProxy(HttpRequest request) =>
        ProxyHeaders.Any(request.Headers.ContainsKey);

    private static readonly string[] ProxyHeaders =
    [
        "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto",
        "X-Original-For", "X-Original-Host", "X-Original-Proto",
    ];
}
