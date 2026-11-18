using BeeMemoryBank.Core.Models;
using Microsoft.AspNetCore.Authorization;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// Which requests are for a superadmin and nobody else: a page whose model says <c>[Authorize(Roles = "superadmin")]</c>, an explicit route with
/// <c>RequireAuthorization(policy =&gt; policy.RequireRole("superadmin"))</c>, and a <c>/api-proxy</c> path whose <see cref="ProxyRouteTable"/> rule asks for
/// that role. The cookie's security stamp is checked on EVERY such request, with no cache, and a lookup that cannot be answered is "sign in again"
/// (<see cref="PrincipalStampValidator"/>): a cookie that was issued to a superadmin must stop being one at the very next request after a demotion or a deletion,
/// and an API hiccup must never be what keeps it alive.
/// </summary>
internal static class SuperadminRoutes
{
    /// <summary>
    /// The poll of the "Encrypt this node" migration. The migration closes the very database the stamp lives in for a few seconds, so a strict check would turn the
    /// last poll into a sign-out; the API serves exactly this one superadmin route while closed (<c>CallerIdentityMiddleware</c>) and keeps the answer harmless (progress only).
    /// </summary>
    private const string ClosedDatabasePollPath = "vault/seal/progress";

    public static bool IsSuperadminRoute(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is not null)
        {
            foreach (var data in endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                if (NamesSuperadmin(data.Roles)) return true;

            foreach (var policy in endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>())
                foreach (var roles in policy.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement>())
                    if (roles.AllowedRoles.Any(r => string.Equals(r, UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase))) return true;
        }

        // The catch-all forwarder: its one endpoint carries no role, the table gives each prefix and method its own.
        if (context.Request.Path.StartsWithSegments("/api-proxy", StringComparison.OrdinalIgnoreCase, out var rest))
        {
            var proxyPath = rest.Value?.TrimStart('/') ?? "";
            if (HttpMethods.IsGet(context.Request.Method)
                && string.Equals(proxyPath, ClosedDatabasePollPath, StringComparison.OrdinalIgnoreCase))
                return false;

            var (outcome, _, rule, _) = ProxyRouteTable.Match(proxyPath, context.Request.Method);
            if (outcome == ProxyRouteTable.MatchOutcome.Matched
                && string.Equals(rule?.RequiredRole, UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool NamesSuperadmin(string? roles) =>
        !string.IsNullOrEmpty(roles)
        && roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(r => string.Equals(r, UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase));
}
