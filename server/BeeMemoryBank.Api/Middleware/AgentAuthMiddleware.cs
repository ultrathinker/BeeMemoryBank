using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Middleware;

/// <summary>
/// Non-blocking middleware: if the request contains an agent Bearer token,
/// validates it and auto-unlocks the session. Without a token — passes through.
/// </summary>
/// <remarks>
/// AUDIT NOTE: This middleware intentionally calls next(context) unconditionally.
/// It is NOT an authentication gate — it only performs opportunistic session unlock.
/// Authorization is enforced at the endpoint level: content endpoints check session.IsUnlocked,
/// write endpoints check InternalKeyValidator. The MCP endpoint is only accessible on localhost
/// (API port 5300 is not exposed via Nginx). This is defense-in-depth, not the sole auth layer.
/// </remarks>
public class AgentAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAgentRepository agentRepo, SessionService session)
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
                    context.Items["AuthAgent"] = agent;

                    _ = agentRepo.UpdateAccessAsync(agent.Id)
                        .ContinueWith(static t => { /* swallow — non-critical tracking */ },
                            TaskContinuationOptions.OnlyOnFaulted);

                    if (!session.IsUnlocked)
                    {
                        try
                        {
                            var masterDek = AgentKeyHelper.DecryptDek(
                                apiKey, agent.EncryptedDek, agent.DekIV);
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
