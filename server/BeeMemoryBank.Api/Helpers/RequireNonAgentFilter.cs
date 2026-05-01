using BeeMemoryBank.Api.Models;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// Endpoint filter that blocks requests originating from agent bearer tokens.
/// Returns HTTP 403 if the request was made by an agent (AgentId is set in CallerIdentity).
///
/// Apply to any endpoint that must not be callable by agents:
///   session/lock, session/unlock, user management, agent management.
///
/// Does NOT block the agent's auto-unlock in AgentAuthMiddleware — that happens
/// transparently at the middleware layer before endpoints are reached.
/// </summary>
public sealed class RequireNonAgentFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var caller = CallerIdentity.Extract(context.HttpContext);
        if (caller.AgentId.HasValue)
            return Results.Json(new ErrorResponse("Agents cannot perform this operation"), statusCode: 403);

        return await next(context);
    }
}

/// <summary>Extension helpers for attaching RequireNonAgentFilter to routes and groups.</summary>
public static class RequireNonAgentExtensions
{
    public static RouteHandlerBuilder RequireNonAgent(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<RequireNonAgentFilter>();

    public static RouteGroupBuilder RequireNonAgent(this RouteGroupBuilder builder)
        => builder.AddEndpointFilter<RequireNonAgentFilter>();
}
