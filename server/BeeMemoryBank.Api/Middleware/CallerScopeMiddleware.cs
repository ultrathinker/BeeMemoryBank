using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Middleware;

public class CallerScopeMiddleware
{
    private readonly RequestDelegate _next;

    public CallerScopeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var holder = ctx.RequestServices.GetRequiredService<CallerScopeHolder>();
        var (userId, agentId, isSuperadmin) = CallerIdentity.Extract(ctx);

        if (isSuperadmin)
        {
            holder.Scope = new HttpCallerScope(true, [], []);
        }
        else if (userId.HasValue)
        {
            var folderAccess = ctx.RequestServices.GetRequiredService<FolderAccessService>();
            var (denyPaths, allowPaths) = await folderAccess.GetAccessInfoAsync(userId, agentId);
            holder.Scope = new HttpCallerScope(false, denyPaths, allowPaths);
        }
        else if (agentId.HasValue)
        {
            // Legacy agent without an OwnerUserId (pre-migration 004 or owner deleted).
            // FolderAccessService.GetAccessInfoAsync(null, …) returns empty allow/deny sets,
            // and IsAccessDenied with both empty defaults to "not denied" → full vault access.
            // Fail closed: deny everything. Operator must reassign ownership or recreate the agent.
            var denyAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/" };
            holder.Scope = new HttpCallerScope(false, denyAll, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            // No authenticated identity (no valid agent bearer, no validated web-proxy user
            // headers, not superadmin). Default to deny-all instead of allow-all — otherwise
            // FolderAccessService.IsAccessDenied with both sets empty returns "not denied"
            // and MCP tools (which don't individually invoke InternalKeyValidator) would
            // return data to anonymous callers. Use "/" as a deny prefix so every path is
            // blocked via MatchesAnyPrefix.
            var denyAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/" };
            holder.Scope = new HttpCallerScope(false, denyAll, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        await _next(ctx);
    }
}
