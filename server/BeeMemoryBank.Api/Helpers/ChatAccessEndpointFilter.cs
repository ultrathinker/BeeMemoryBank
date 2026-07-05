using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// Endpoint filter enforcing AI-chat access control on the whole <c>/api/chat</c> group.
/// Superadmins and agent callers (a separate, already-authenticated auth path via
/// AgentAuthMiddleware) always pass through untouched. Everyone else needs BOTH the
/// node-wide "chat_globally_enabled" flag (chat_settings, superadmin-controlled) AND
/// their own per-user "chat_access" flag (tbl_user, superadmin-controlled per user) to be
/// true. This is the actual security boundary — UI-side hiding of chat entry points is a
/// UX nicety on top of this, never a substitute for it.
/// </summary>
public sealed class ChatAccessEndpointFilter(IUserRepository userRepo, ChatSettingsRepository chatSettingsRepo) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var caller = CallerIdentity.Extract(context.HttpContext);
        if (caller.IsSuperadmin || caller.AgentId.HasValue)
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
