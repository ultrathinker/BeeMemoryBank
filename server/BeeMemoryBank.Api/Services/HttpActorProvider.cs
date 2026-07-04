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

    public string? ViaAgentName
    {
        get
        {
            var ctx = accessor.HttpContext;
            if (ctx?.Items.TryGetValue("AuthAgent", out var obj) == true && obj is Agent agent)
                return agent.Name;
            // Phase 3 (AI chat): while a chat-driven WRITE tool executes, ChatToolDispatcher sets a
            // marker on HttpContext.Items so writes are attributed to the AI in /Activity ("via agent:
            // chat"), exactly like MCP-agent-driven edits (plan §1 "Audit", §2 Phase 3). Read tools are
            // deliberately NOT tagged — only writes. This is an additive read of a marker key; existing
            // callers/behaviour are unaffected.
            if (ctx?.Items.TryGetValue(ChatToolDispatcher.ChatActorItemsKey, out var chatMarker) == true
                && chatMarker is string s && s.Length > 0)
                return s;
            return null;
        }
    }
}
