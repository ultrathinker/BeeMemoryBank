using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// Endpoint filter enforcing AI-chat access control on the whole <c>/api/chat</c> group.
/// Superadmins always pass through untouched. Everyone else — including an agent bearer-key
/// caller — needs BOTH the node-wide "chat_globally_enabled" flag (chat_settings,
/// superadmin-controlled) AND a per-user "chat_access" flag (tbl_user, superadmin-controlled
/// per user) to be true. This is the actual security boundary — UI-side hiding of chat entry
/// points is a UX nicety on top of this, never a substitute for it.
/// </summary>
/// <remarks>
/// An agent bearer-key caller must NOT be waved through unconditionally
/// (<c>caller.AgentId.HasValue</c>): agent bearer keys are not "a separate,
/// already-authenticated auth path" — that conflates AUTHENTICATION (is this a real
/// key) with AUTHORIZATION (should ITS OWNER be allowed to use chat). An agent key is scoped to
/// its owning user's account, so it should never be MORE privileged than that user is at the
/// web UI. Otherwise, disabling chat for a user (chat_access=false) or for the whole
/// node (chat_globally_enabled=false) would do nothing for that user's agent keys, which could
/// keep using chat right through both kill switches. <see cref="CallerIdentity.Extract"/> already
/// resolves an agent's <c>UserId</c> to its OWNER (see AgentAuthMiddleware), so an agent's
/// chat access inherits its owner's — including the
/// IsSuperadmin case, which carries through the owner's role.
/// </remarks>
public sealed class ChatAccessEndpointFilter(IUserRepository userRepo, ChatSettingsRepository chatSettingsRepo) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var caller = CallerIdentity.Extract(context.HttpContext);
        if (caller.IsSuperadmin)
            return await next(context);

        if (caller.UserId is null)
            return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 401);

        if (!await chatSettingsRepo.GetChatGloballyEnabledAsync())
            return Results.Json(new ErrorResponse("AI chat is disabled by the administrator"), statusCode: 403);

        var user = await userRepo.GetByIdAsync(caller.UserId.Value);
        if (user is null || !user.ChatAccess)
            return Results.Json(new ErrorResponse("You don't have access to AI chat"), statusCode: 403);

        return await next(context);
    }
}

/// <summary>Extension helper for attaching <see cref="ChatAccessEndpointFilter"/> to a route group.</summary>
public static class ChatAccessEndpointFilterExtensions
{
    public static RouteGroupBuilder RequireChatAccess(this RouteGroupBuilder builder)
        => builder.AddEndpointFilter<ChatAccessEndpointFilter>();
}
