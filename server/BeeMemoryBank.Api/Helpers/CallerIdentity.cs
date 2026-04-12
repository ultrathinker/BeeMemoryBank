using BeeMemoryBank.Core.Models;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Api.Helpers;

public static class CallerIdentity
{
    public static (int? userId, int? agentId, bool isSuperadmin) Extract(HttpContext ctx)
    {
        int? userId = null;
        int? agentId = null;
        bool isSuperadmin = false;

        if (ctx.Items.TryGetValue("AuthAgent", out var agentObj) && agentObj is Agent agent)
        {
            // Agent identified by bearer token — agents are never superadmin,
            // ignore X-User-Id/X-User-Role headers to prevent privilege escalation
            agentId = agent.Id;
        }
        else
        {
            // Web proxy request — trust forwarded headers (protected by InternalKeyValidator)
            var userIdHeader = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
            if (int.TryParse(userIdHeader, out var uid))
                userId = uid;

            var roleHeader = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (roleHeader == "superadmin")
                isSuperadmin = true;
        }

        return (userId, agentId, isSuperadmin);
    }
}
