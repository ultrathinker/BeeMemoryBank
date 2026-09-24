using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Determines the actor from HttpContext: if an agent exists (from middleware) → agent,
/// otherwise → web.
/// </summary>
public class HttpActorProvider(IHttpContextAccessor accessor) : IActorProvider
{
    /// <summary>HttpContext.Items key memoizing the resolved web-user display name per request.</summary>
    private const string DisplayNameCacheKey = "HttpActorProvider.ActorDisplayName";

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
            if (ctx is null) return null;

            // Web callers: the display name is resolved from the trusted X-User-Id, NOT read from
            // a header. A display name is user-controlled text and may be non-ASCII, and non-ASCII
            // values cannot cross the socket as an HTTP header — the Web therefore stopped sending
            // X-User-DisplayName (SocketsHttpHandler rejected every such request outright).
            // X-User-Id is only honoured when the request carried the internal key
            // (CallerIdentity.Extract), so the attribution is exactly as trustworthy as the id it
            // is derived from.
            //
            // Resolved lazily and at most once per request: only version-history snapshots and
            // audit events read attribution, so a plain GET must not pay a DB round-trip. The
            // IActorProvider surface is a synchronous property, so the one small read pays
            // GetAwaiter().GetResult() — safe here: it runs on a request thread with no
            // SynchronizationContext, against a local SQLite connection, at most once per request.
            if (ctx.Items.TryGetValue(DisplayNameCacheKey, out var cached))
                return cached as string;

            var displayName = ResolveWebUserDisplayNameAsync(ctx).GetAwaiter().GetResult();
            // Cache the miss too (null) so a user id that no longer resolves costs one lookup per
            // request, not one per attribution read.
            ctx.Items[DisplayNameCacheKey] = displayName;
            return displayName;
        }
    }

    private static async Task<string?> ResolveWebUserDisplayNameAsync(HttpContext ctx)
    {
        var userId = CallerIdentity.Extract(ctx).UserId;
        if (userId is null) return null;

        var user = await ctx.RequestServices.GetRequiredService<IUserRepository>().GetByIdAsync(userId.Value);
        return string.IsNullOrEmpty(user?.DisplayName) ? null : user!.DisplayName;
    }

    public string? ViaAgentName
    {
        get
        {
            var ctx = accessor.HttpContext;
            if (ctx?.Items.TryGetValue("AuthAgent", out var obj) == true && obj is Agent agent)
                return agent.Name;
            // AI chat: while a chat-driven WRITE tool executes, ChatToolDispatcher sets a
            // marker on HttpContext.Items so the write is attributed to the AI in /Activity ("via agent:
            // chat"), exactly like MCP-agent-driven edits. Read tools are
            // deliberately NOT tagged — only writes.
            if (ctx?.Items.TryGetValue(ChatToolDispatcher.ChatActorItemsKey, out var chatMarker) == true
                && chatMarker is string s && s.Length > 0)
                return s;
            return null;
        }
    }
}
