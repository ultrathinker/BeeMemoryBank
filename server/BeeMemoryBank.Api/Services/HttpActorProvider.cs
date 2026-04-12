using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Determines the actor from HttpContext: if an agent exists (from middleware) → agent,
/// otherwise → web.
/// </summary>
public class HttpActorProvider(IHttpContextAccessor accessor) : IActorProvider
{
    public string ActorType
    {
        get
        {
            var ctx = accessor.HttpContext;
            if (ctx?.Items.TryGetValue("AuthAgent", out var obj) == true && obj is Agent)
                return "agent";
            return "web";
        }
    }

    public string? ActorName
    {
        get
        {
            var ctx = accessor.HttpContext;
            if (ctx?.Items.TryGetValue("AuthAgent", out var obj) == true && obj is Agent agent)
                return agent.Name;
            // For web users, return display name from forwarded header
            var displayName = ctx?.Request.Headers["X-User-DisplayName"].FirstOrDefault();
            return string.IsNullOrEmpty(displayName) ? null : displayName;
        }
    }
}
