using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// Endpoint marker that EXEMPTS an endpoint from the internal-key gate. Attach with
/// <c>.WithMetadata(new SkipInternalKey())</c> on intentionally-anonymous endpoints
/// (login screen polling, sync bearer-token challenge endpoints, first-run init status).
/// The <see cref="InternalKeyEndpointFilter"/> inspects the matched endpoint's metadata
/// and skips validation when this marker is present, so the exception stays visible at
/// the registration site instead of being hidden in a separate allow-list.
/// </summary>
public sealed class SkipInternalKey;

/// <summary>
/// Endpoint filter that enforces <see cref="InternalKeyValidator.Validate"/> structurally,
/// so a newly added endpoint inside a protected group cannot accidentally forget the gate.
/// Per-endpoint role/superadmin re-checks stay in the handlers (defense-in-depth); this only
/// hoists the internal-key check that was previously copy-pasted at the top of every handler.
///
/// Endpoints that must remain reachable without an internal key (login-screen polling, sync
/// challenge-response bearer auth, first-run init status) are marked with
/// <see cref="SkipInternalKey"/> and bypass this filter.
/// </summary>
public sealed class InternalKeyEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        // Skip the gate for explicitly-exempted endpoints (decision O5: in-group opt-out marker).
        if (httpContext.GetEndpoint()?.Metadata.GetMetadata<SkipInternalKey>() is not null)
            return await next(context);

        if (!InternalKeyValidator.Validate(httpContext))
            return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

        return await next(context);
    }
}

/// <summary>Extension helpers for attaching <see cref="InternalKeyEndpointFilter"/>.</summary>
public static class RequireInternalKeyExtensions
{
    /// <summary>Applies the internal-key gate to every endpoint in the group.</summary>
    public static RouteGroupBuilder RequireInternalKey(this RouteGroupBuilder builder)
        => builder.AddEndpointFilter<InternalKeyEndpointFilter>();

    /// <summary>Applies the internal-key gate to a single endpoint registered directly on the app.</summary>
    public static RouteHandlerBuilder RequireInternalKey(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<InternalKeyEndpointFilter>();
}
