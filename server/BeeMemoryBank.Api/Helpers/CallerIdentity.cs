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
    /// <summary>The <see cref="HttpContext.Items"/> key under which a middleware parks a finished identity (agent auth, the user lookup).</summary>
    public const string ItemKey = "CallerIdentity";

    public static CallerIdentity Extract(HttpContext ctx)
    {
        // AgentAuthMiddleware pre-builds a full CallerIdentity (with ViaAgentName and
        // owner-based UserId) when owner resolution succeeds, and CallerIdentityMiddleware
        // does the same for a signed-in Web user (role and active flag read from tbl_user).
        // Use it directly.
        if (ctx.Items.TryGetValue(ItemKey, out var preBuilt) && preBuilt is CallerIdentity identity)
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
            // Only a request that carried a valid internal key is looked at (never on loopback alone — see
            // InternalKeyValidator). Without this gate, any external caller could spoof
            // X-User-Role: superadmin on endpoints that don't individually invoke
            // InternalKeyValidator (notably /mcp) and inherit superadmin scope.
            switch (ForwardedUser(ctx))
            {
                case ForwardedUserKind.User:
                    // The Web layer names a person. WHAT that person is, this node decides from tbl_user
                    // (CallerIdentityMiddleware, which pre-builds the identity above). Reaching this line means
                    // no such lookup ran for the request (a host without it, a hand-made context): keep the
                    // person, grant nothing. The role header of such a request is never read.
                    userId = ForwardedUserId(ctx);
                    break;

                case ForwardedUserKind.Malformed:
                    // An id that is there but not one id: fail closed, no person and no rights.
                    break;

                default:
                    // Machine-local callers that hold the internal key and name nobody (the tray's lock and
                    // alarms, NodeFrontClient, the CLI, the blind console) state the role they act in, as always.
                    if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() == UserRoles.Superadmin)
                        isSuperadmin = true;
                    break;
            }
        }

        return new CallerIdentity(userId, agentId, null, isSuperadmin);
    }

    /// <summary>How the request names a user in <c>X-User-Id</c>.</summary>
    public enum ForwardedUserKind
    {
        /// <summary>No usable <c>X-User-Id</c>: a machine-local caller (or one whose value is not an id at all, which names nobody either).</summary>
        None,
        /// <summary>Exactly one positive integer id.</summary>
        User,
        /// <summary>More than one <c>X-User-Id</c> header: refused rather than guessed at.</summary>
        Malformed,
    }

    public static ForwardedUserKind ForwardedUser(HttpContext ctx)
    {
        var values = ctx.Request.Headers["X-User-Id"];
        if (values.Count == 0) return ForwardedUserKind.None;
        if (values.Count > 1) return ForwardedUserKind.Malformed;
        return int.TryParse(values[0], out var uid) && uid > 0 ? ForwardedUserKind.User : ForwardedUserKind.None;
    }

    /// <summary>The id of <see cref="ForwardedUserKind.User"/>. Call only after <see cref="ForwardedUser"/> said so.</summary>
    public static int ForwardedUserId(HttpContext ctx) => int.Parse(ctx.Request.Headers["X-User-Id"].First()!);

    /// <summary>
    /// Stable key recorded as the uploader of media that isn't linked to an article yet
    /// (tbl_media.uploaded_by). An agent resolves to its owning user, so a file an agent uploads
    /// for its owner counts as the owner's. Null when the caller has no identity at all.
    /// </summary>
    public string? MediaOwnerKey =>
        UserId is { } uid ? $"u{uid}" : AgentId is { } aid ? $"a{aid}" : null;

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
