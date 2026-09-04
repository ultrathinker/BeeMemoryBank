using BeeMemoryBank.Api.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// Endpoint filter that enforces "superadmin only" structurally, the way
/// <see cref="InternalKeyEndpointFilter"/> enforces the internal-key gate: attach it once with
/// <c>.RequireSuperadmin()</c> on a group or endpoint instead of re-typing a header comparison at
/// the top of every handler. Resolves the caller through <see cref="CallerIdentity.Extract"/>, so
/// the same filter covers a Web user (role header, honoured only behind a valid internal key), an
/// MCP agent (its owner's role), and a remote token (never superadmin) without the handler having
/// to know which one it is talking to.
///
/// Before this existed the same rule was written four different ways across the endpoint files
/// (raw <c>X-User-Role</c> compare, <c>CallerIdentity.Extract(ctx).IsSuperadmin</c>,
/// <c>InternalKeyValidator.ValidateInternalAndRole</c>, and — for <c>/api/keys/*</c> — not at all).
/// A filter on the route is visible at the registration site and can be asserted by a test that
/// walks <c>EndpointDataSource</c>; an inline check inside a handler body can be forgotten silently.
/// </summary>
public sealed class SuperadminEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!CallerIdentity.Extract(context.HttpContext).IsSuperadmin)
            return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

        return await next(context);
    }
}

/// <summary>Marker metadata so tests can tell which endpoints carry the superadmin gate.</summary>
public sealed class RequiresSuperadmin;

/// <summary>Extension helpers for attaching <see cref="SuperadminEndpointFilter"/>.</summary>
public static class RequireSuperadminExtensions
{
    /// <summary>Requires a superadmin caller for every endpoint in the group.</summary>
    public static RouteGroupBuilder RequireSuperadmin(this RouteGroupBuilder builder)
        => builder.AddEndpointFilter<SuperadminEndpointFilter>().WithMetadata(new RequiresSuperadmin());

    /// <summary>Requires a superadmin caller for a single endpoint.</summary>
    public static RouteHandlerBuilder RequireSuperadmin(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<SuperadminEndpointFilter>().WithMetadata(new RequiresSuperadmin());
}
