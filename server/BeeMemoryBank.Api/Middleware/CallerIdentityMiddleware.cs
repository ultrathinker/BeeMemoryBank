using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Middleware;

/// <summary>
/// The API decides who the caller is (release 2.12). A request that carries the internal key and an <c>X-User-Id</c> comes from the
/// Web layer on behalf of a signed-in person; the Web layer vouches for WHO, and this middleware reads WHAT that person is — role and active
/// flag — from <c>tbl_user</c> (a few seconds of <see cref="CallerStateCache"/>, invalidated by every change <c>UserService</c> makes to that
/// user). <c>X-User-Role</c> is not read for such a request. So a cookie that was issued to a superadmin is a superadmin's only as long as the
/// row says so: the very next call after a demotion or a deletion is the demoted or deleted person's call.
///
/// <list type="bullet">
/// <item><description>An inactive or missing user is not a superadmin and not authenticated: the identity carries no user at all.</description></item>
/// <item><description>No <c>X-User-Id</c> (the tray's lock and alarms, <c>NodeFrontClient</c>, the CLI, the blind console: machine-local callers that
/// hold the internal key and name nobody) changes nothing: <see cref="CallerIdentity.Extract"/> keeps honouring the role header for them.</description></item>
/// <item><description>While a sealed database is closed there is no <c>tbl_user</c> to read, so nobody named by <c>X-User-Id</c> is a superadmin —
/// except on the routes in <see cref="ClosedDatabaseRoutes"/>, the only superadmin routes <see cref="SealedVaultMiddleware"/> still lets through
/// while closed (the progress poll of the migration that is itself closing the file). Everything else answers 503 before it gets that far.</description></item>
/// <item><description>An agent's request (bearer key) keeps the identity <see cref="AgentAuthMiddleware"/> built; this middleware leaves it alone.</description></item>
/// </list>
/// Sits after <see cref="SealedVaultMiddleware"/> and in front of <see cref="CallerScopeMiddleware"/>. A host that does not have it (the blind
/// node: no users) gets the fail-closed reading of <see cref="CallerIdentity.Extract"/>: the person is kept, no rights are granted.
/// </summary>
public sealed class CallerIdentityMiddleware(RequestDelegate next)
{
    /// <summary>Method and path of the superadmin routes that are served while a sealed database is closed, where the role header is still honoured.</summary>
    private static readonly (string Method, string Path)[] ClosedDatabaseRoutes =
    [
        ("GET", "/api/vault/seal/progress"),
    ];

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Items.ContainsKey(CallerIdentity.ItemKey)
            && !ctx.Items.ContainsKey("AuthAgent")
            && InternalKeyValidator.Validate(ctx))
        {
            switch (CallerIdentity.ForwardedUser(ctx))
            {
                case CallerIdentity.ForwardedUserKind.User:
                    ctx.Items[CallerIdentity.ItemKey] = await ResolveAsync(ctx, CallerIdentity.ForwardedUserId(ctx));
                    break;

                case CallerIdentity.ForwardedUserKind.Malformed:
                    ctx.Items[CallerIdentity.ItemKey] = new CallerIdentity(null, null, null, false);
                    break;
            }
        }

        await next(ctx);
    }

    private static async Task<CallerIdentity> ResolveAsync(HttpContext ctx, int userId)
    {
        var services = ctx.RequestServices;

        // A sealed database that is closed cannot be read: the role cannot be looked up. Nobody is a superadmin then, but for the one
        // progress route that is allowed to be reached; there the header is what it always was.
        if (services.GetService<IVaultGate>() is { IsSealed: true, IsUsable: false })
        {
            var superadmin = IsClosedDatabaseRoute(ctx) && ctx.Request.Headers["X-User-Role"].FirstOrDefault() == UserRoles.Superadmin;
            return new CallerIdentity(userId, null, null, superadmin);
        }

        var users = services.GetRequiredService<IUserRepository>();
        var databaseId = services.GetService<IDbConnectionFactory>()?.DatabaseId ?? "default";
        var role = await CallerStateCache.GetRoleAsync(databaseId, userId, users);

        // Missing or inactive: not authenticated, not a superadmin, no person to scope folders for.
        return role is null
            ? new CallerIdentity(null, null, null, false)
            : new CallerIdentity(userId, null, null, CallerStateCache.IsSuperadmin(role));
    }

    private static bool IsClosedDatabaseRoute(HttpContext ctx)
    {
        foreach (var (method, path) in ClosedDatabaseRoutes)
            if (string.Equals(ctx.Request.Method, method, StringComparison.OrdinalIgnoreCase)
                && ctx.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
