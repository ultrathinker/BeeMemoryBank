using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Middleware;

/// <summary>
/// Non-blocking middleware: if the request contains an agent Bearer token,
/// validates it, resolves the owner user, builds a CallerIdentity, and auto-unlocks
/// the session. Without a token — passes through.
/// </summary>
/// <remarks>
/// AUDIT NOTE: This middleware intentionally calls next(context) unconditionally.
/// It is NOT an authentication gate — it only performs opportunistic session unlock.
/// Authorization is enforced at the endpoint level: content endpoints check session.IsUnlocked,
/// write endpoints check InternalKeyValidator. The MCP endpoint is only accessible on localhost
/// (API port 5300 is not exposed via Nginx). This is defense-in-depth, not the sole auth layer.
///
/// AUTO-UNLOCK: Agents are permitted to auto-unlock the session via their encrypted DEK.
/// This is intentional — it ensures MCP clients can work without manual intervention.
/// Agents cannot call session/lock or session/unlock endpoints directly (blocked by
/// RequireNonAgent endpoint filter added in migration 004). The asymmetry is by design:
/// auto-unlock serves the owner's session; lock/unlock via API is a human operation.
/// </remarks>
public class AgentAuthMiddleware(RequestDelegate next, ILogger<AgentAuthMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IAgentRepository agentRepo, IUserRepository userRepo, SessionService session)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = authHeader["Bearer ".Length..].Trim();
            if (apiKey.StartsWith("bee_"))
            {
                var keyHash = AgentKeyHelper.ComputeKeyHash(apiKey);
                var agent = await agentRepo.GetByKeyHashAsync(keyHash);

                if (agent != null)
                {
                    // Resolve owner user and build a full CallerIdentity.
                    // agent.OwnerUserId is 0 on databases that haven't run migration 004 yet;
                    // in that case we fall through with the legacy agent-only identity.
                    if (agent.OwnerUserId > 0)
                    {
                        var owner = await userRepo.GetByIdAsync(agent.OwnerUserId);
                        if (owner == null || !owner.IsActive)
                        {
                            logger.LogWarning("Agent {AgentId} blocked: owner {OwnerId} is deactivated or missing",
                                agent.Id, agent.OwnerUserId);
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return;
                        }

                        context.Items["AuthAgent"] = agent;

                        _ = agentRepo.UpdateAccessAsync(agent.Id)
                            .ContinueWith(static t => { /* swallow — non-critical tracking */ },
                                TaskContinuationOptions.OnlyOnFaulted);

                        context.Items["CallerIdentity"] = new CallerIdentity(
                            UserId: owner.Id,
                            AgentId: agent.Id,
                            ViaAgentName: agent.Name,
                            IsSuperadmin: owner.Role == UserRoles.Superadmin);
                    }
                    else
                    {
                        context.Items["AuthAgent"] = agent;

                        _ = agentRepo.UpdateAccessAsync(agent.Id)
                            .ContinueWith(static t => { /* swallow — non-critical tracking */ },
                                TaskContinuationOptions.OnlyOnFaulted);
                    }

                    if (!session.IsUnlocked)
                    {
                        try
                        {
                            byte[] masterDek;
                            if (agent.KdfVersion == 1 && agent.Salt != null)
                            {
                                masterDek = AgentKeyHelper.DecryptDekV1(
                                    apiKey, agent.EncryptedDek, agent.DekIV, agent.Salt);
                            }
                            else
                            {
                                masterDek = AgentKeyHelper.DecryptDek(
                                    apiKey, agent.EncryptedDek, agent.DekIV);
                            }
                            
                            session.UnlockWithDek(masterDek);
                        }
                        catch
                        {
                            // Failed to decrypt — key is invalid.
                            // Do not block the request, session will remain locked.
                        }
                    }
                }
            }
        }

        await next(context);
    }
}
