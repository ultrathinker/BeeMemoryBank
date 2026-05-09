using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Core.Models;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// Represents the identity of the caller making an API request.
/// For direct user requests: UserId and IsSuperadmin are set.
/// For agent bearer-token requests: AgentId is set; ViaAgentName carries the agent's display name
/// (set by AgentAuthMiddleware).
/// </summary>
public sealed record CallerIdentity(int? UserId, int? AgentId, string? ViaAgentName, bool IsSuperadmin)
{
    public static CallerIdentity Extract(HttpContext ctx)
    {
        // AgentAuthMiddleware pre-builds a full CallerIdentity (with ViaAgentName and
        // owner-based UserId) when owner resolution succeeds. Use it directly.
        if (ctx.Items.TryGetValue("CallerIdentity", out var preBuilt) && preBuilt is CallerIdentity identity)
            return identity;

        int? userId = null;
        int? agentId = null;
        bool isSuperadmin = false;

        if (ctx.Items.TryGetValue("AuthAgent", out var agentObj) && agentObj is Agent agent)
        {
            // Agent without resolved owner (database not yet migrated to 004).
            // Fall back to legacy: agent is identified but has no owner-derived UserId.
            agentId = agent.Id;
        }
        else if (InternalKeyValidator.Validate(ctx))
        {
            // Web proxy request — trust forwarded X-User-Id / X-User-Role headers ONLY when
            // the request carried a valid internal key (or originated from localhost with no
            // key configured). Without this gate, any external caller could spoof
            // X-User-Role: superadmin on endpoints that don't individually invoke
            // InternalKeyValidator (notably /mcp) and inherit superadmin scope.
            var userIdHeader = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
            if (int.TryParse(userIdHeader, out var uid))
                userId = uid;

            var roleHeader = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (roleHeader == UserRoles.Superadmin)
                isSuperadmin = true;
        }

        return new CallerIdentity(userId, agentId, null, isSuperadmin);
    }

    /// <summary>
    /// Backward-compatible 3-value deconstruct for existing call-sites.
    /// Use the record properties directly when you need ViaAgentName.
    /// </summary>
    public void Deconstruct(out int? userId, out int? agentId, out bool isSuperadmin)
    {
        userId = UserId;
        agentId = AgentId;
        isSuperadmin = IsSuperadmin;
    }
}
