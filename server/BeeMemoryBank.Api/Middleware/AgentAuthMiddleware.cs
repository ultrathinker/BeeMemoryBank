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
/// This middleware intentionally calls next(context) unconditionally.
/// It is NOT an authentication gate — it only performs opportunistic session unlock.
/// Authorization is enforced at the endpoint level: content endpoints check session.IsUnlocked,
/// write endpoints check InternalKeyValidator. The MCP endpoint (and every other
/// route on this port — /api/session/unlock, /api/session/status, /api/join,
/// /api/init/reset, ...) is only ever meant to be reachable on localhost / from within this
/// process's own container — NEVER published straight to a host port or the internet.
/// Enforced by the deployment: docker-compose.yml does not publish
/// port 5300 at all, and a node that does need cross-node sync (/api/sync, /api/join) or /mcp
/// reachable from another machine publishes it bound to the HOST's loopback behind a
/// path-filtering reverse proxy — see docs/deployment.md. This is defense-in-depth, not the sole
/// auth layer.
///
/// AUTO-UNLOCK: an agent whose row carries a wrapped master DEK is permitted to auto-unlock the
/// session with it. This is intentional — it ensures MCP clients can work without manual
/// intervention. Agents cannot call session/lock or session/unlock endpoints directly (blocked by
/// RequireNonAgent endpoint filter added in migration 004). The asymmetry is by design:
/// auto-unlock serves the owner's session; lock/unlock via API is a human operation.
///
/// NOT every agent carries a wrapped DEK. Only an agent owned by a superadmin gets one at
/// creation time (AgentEndpoints/AgentCommand) — an agent key must never become a de-facto key
/// to the entire vault regardless of what its owner's folder ACL allows, and only a superadmin
/// could unlock the vault anyway (an ordinary user's login returns 403 "Server is locked").
/// See Agent.CanAutoUnlock and migration
/// 014_agent_dek_optional.sql (which strips wrapped DEKs from every pre-existing non-superadmin
/// agent). An agent without one simply skips the unlock attempt below — the vault stays exactly
/// as locked or unlocked as it already was, and a subsequent tool call that needs decrypted
/// content fails the ordinary "vault is locked" way (McpSessionGuardMiddleware /
/// RequiresUnlockedSessionAttribute), not silently.
/// </remarks>
public class AgentAuthMiddleware(RequestDelegate next, ILogger<AgentAuthMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context,
        IAgentRepository agentRepo,
        IUserRepository userRepo,
        SessionService session,
        IRemoteApiTokenRepository remoteTokenRepo)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = authHeader["Bearer ".Length..].Trim();

            // Cross-instance remote token: looks like "bmbrt_<40-hex>".
            // No auto-unlock — these tokens have no DEK material. The endpoint
            // refuses to serve plaintext if the local session is still locked.
            if (apiKey.StartsWith("bmbrt_"))
            {
                var hash = RemoteTokenHelper.Hash(apiKey);
                var record = await remoteTokenRepo.GetByTokenHashAsync(hash);
                if (record != null && record.ExpiresAt > DateTime.UtcNow)
                {
                    var owner = await userRepo.GetByIdAsync(record.UserId);
                    if (owner != null && owner.IsActive)
                    {
                        // SECURITY: remote tokens must NEVER carry superadmin
                        // privileges, even if their owning user is a superadmin.
                        // The token leaves the node by design (handed to a friend's
                        // BMB), so a leak would otherwise expose the entire vault
                        // with no ACL — snapshot/accessible endpoints bypass
                        // checks when IsSuperadmin=true. Always downgrade to user
                        // scope. Cross-instance use cases never need elevation;
                        // the token's read access is bounded by the owner-side
                        // ACL grants on their guest user.
                        context.Items["CallerIdentity"] = new CallerIdentity(
                            UserId: owner.Id,
                            AgentId: null,
                            ViaAgentName: $"remote:{record.Label ?? "unlabelled"}",
                            IsSuperadmin: false);

                        // Marker for endpoints that want to know the caller came in via a bmbrt_
                        // remote token rather than a bee_ agent key. The MCP gate classifies
                        // directly from the header value (it must — an expired bmbrt_ never
                        // reaches this branch and would otherwise slip past the marker-based
                        // check); this marker is kept for any future endpoint that wants the
                        // same information.
                        context.Items["IsRemoteToken"] = true;

                        // Sliding 90-day window: each successful auth bumps expiry.
                        // Awaited (not fire-and-forget) — the scoped repo handle
                        // would otherwise be disposed before the UPDATE completed
                        // and the renewal would silently never persist. SQLite
                        // single-row UPDATE is fast enough to await on every hit.
                        try
                        {
                            await remoteTokenRepo.TouchAsync(record.Id, DateTime.UtcNow, DateTime.UtcNow.AddDays(90));
                        }
                        catch
                        {
                            // non-critical; allow the request to proceed
                        }
                    }
                }
                await next(context);
                return;
            }

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

                        // Awaited (not fire-and-forget) — agentRepo is request-scoped, so a
                        // detached ContinueWith could still be running after this request's DI
                        // scope (and the repo's underlying connection) is disposed at the end of
                        // the request, silently failing every time.
                        try
                        {
                            await agentRepo.UpdateAccessAsync(agent.Id);
                        }
                        catch
                        {
                            // non-critical; allow the request to proceed
                        }

                        context.Items["CallerIdentity"] = new CallerIdentity(
                            UserId: owner.Id,
                            AgentId: agent.Id,
                            ViaAgentName: agent.Name,
                            IsSuperadmin: owner.Role == UserRoles.Superadmin);
                    }
                    else
                    {
                        context.Items["AuthAgent"] = agent;

                        try
                        {
                            await agentRepo.UpdateAccessAsync(agent.Id);
                        }
                        catch
                        {
                            // non-critical; allow the request to proceed
                        }
                    }

                    // Only an agent that actually carries wrapped key material
                    // (owner was a superadmin at creation time — see AgentEndpoints/AgentCommand)
                    // is even attempted here. An ordinary user's agent has agent.CanAutoUnlock ==
                    // false (EncryptedDek/DekIV are null — see migration 014 for pre-existing
                    // rows), so this whole block is skipped for it: the session simply stays
                    // however it already was. That is not a silent failure — a request that then
                    // needs decrypted content still hits the same "locked" errors a human's
                    // locked session already produces elsewhere (McpSessionGuardMiddleware's
                    // RequiresUnlockedSession check, or a content endpoint's own session.IsUnlocked
                    // check) — it just never gets a chance to unlock things by itself. Skipping the
                    // attempt entirely (rather than trying and letting decryption fail) also means
                    // an ordinary user's agent behaves identically whether or not it happens to
                    // hold a stale/garbage EncryptedDek — no decrypt attempt ever leaks, via
                    // timing or otherwise, whether such a blob would have been valid.
                    if (!session.IsUnlocked && agent.CanAutoUnlock)
                    {
                        try
                        {
                            byte[] masterDek;
                            if (agent.KdfVersion == 1 && agent.Salt != null)
                            {
                                masterDek = AgentKeyHelper.DecryptDekV1(
                                    apiKey, agent.EncryptedDek!, agent.DekIV!, agent.Salt);
                            }
                            else
                            {
                                masterDek = AgentKeyHelper.DecryptDek(
                                    apiKey, agent.EncryptedDek!, agent.DekIV!);
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
