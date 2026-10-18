using System.Net.Http.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// One row of the Trusted Nodes "Last contact" column. <c>UpdatedAt</c> keeps its documented meaning, the DELIVERY time: the later of the last pull from the
/// node and the last push of events to it (<c>0001-01-01</c> when neither exists: a node that only calls in); a client of an earlier version reads it as
/// before. The contact (the last completed session, either way, whether or not events moved, BMB-313) is in the NEW field <c>LastContactAt</c>, with
/// <c>Direction</c> (whose call it was), <c>PushedAt</c> (when events last went out) and whether this node's calls to it fail.
/// </summary>
file record SyncStatusEntry(
    Guid NodeId, DateTime UpdatedAt, DateTime? LastContactAt = null, string? Direction = null, DateTime? PushedAt = null, string State = "unknown",
    DateTime? NotAnsweringSince = null, string? NotAnsweringReason = null);

public static class WhitelistEndpoints
{
    public static void MapWhitelistEndpoints(this WebApplication app)
    {
        // Superadmin for the WHOLE group, reads included.
        //
        // The five mutating routes asserted that first (only the two auto-accept toggles did
        // before the endpoint-filter sweep; editing a peer's stored URL and revoking a peer
        // outright were reachable by ANY signed-in user with an unlocked session, and both
        // reshape this node's sync topology).
        //
        // The three GETs followed. Their old comment claimed "the Nodes page renders them for
        // everyone" — there is no Nodes page: the only renderer is Admin.cshtml, which is
        // [Authorize(Roles = "superadmin")], and the only other caller is that page's own
        // sync-status poll. So no ordinary user ever saw this data through the UI, while the API
        // handed it to any of them who asked: every peer's node id, display name and — the part
        // that matters — its api_address, an internal URL of someone else's machine. That is a
        // map of the private network this node syncs with, and it is exactly what an attacker who
        // has compromised one low-privilege account wants next. Key material was never in here,
        // but "no key material" was the wrong test.
        var group = app.MapGroup("/api/whitelist").WithTags("Whitelist")
            .RequireInternalKey().RequireSuperadmin();

        group.MapGet("/sync-status", async (
            HttpContext ctx, ISyncPositionRepository syncRepo, ISyncPushPositionRepository pushRepo, IWhitelistRepository whitelistRepo, PeerContactLog contacts,
            IEventLogRepository eventLogRepo, INodeIdentityRepository nodeRepo, SyncScheduler? scheduler) =>
        {
            // "Last contact" = the last successful session in EITHER direction, as the sync status of the header reads it (PeerContactView): this node's own call
            // (the pull position a finished pull leaves, and this process's record of it) or the peer's call (its authentication, its token on a request).
            // It used to be the later of the pull and the push position, and only the pull position says that THIS node called: a push position moves when
            // events go out or when the peer reports, so a node with no address (never pulled from: a phone) showed the time of its last event, and in a
            // quiet network nothing at all (once "739772d ago", before pre-2000 times were treated as never).
            var identity = await nodeRepo.GetAsync();
            var pulls = (await syncRepo.GetAllAsync()).ToDictionary(p => p.RemoteNodeId, p => (DateTime?)p.UpdatedAt);
            var pushes = (await pushRepo.GetAllAsync()).ToDictionary(p => p.RemoteNodeId);
            var headSeq = await eventLogRepo.GetMaxSequenceAsync();
            var entries = new List<SyncStatusEntry>();
            foreach (var node in (await whitelistRepo.GetAllActiveAsync()).Where(n => n.NodeId != identity?.NodeId))
            {
                pushes.TryGetValue(node.NodeId, out var push);
                var pulled = pulls.GetValueOrDefault(node.NodeId);
                var view = PeerContactView.Build(node, contacts.Get(node.NodeId, node.ApiAddress), pulled, push, headSeq, 0, DateTime.UtcNow, scheduler?.Interval);
                if (view.LastContactAt is null && view.LastPushAt is null && view.NotAnsweringSince is null) continue;
                // UpdatedAt: the delivery time of before (the later of the pull position and the push position); the contact is the new LastContactAt.
                var delivered = new[] { pulled ?? default, push?.PushedAt ?? default }.Max();
                entries.Add(new SyncStatusEntry(node.NodeId, delivered, view.LastContactAt, view.LastContactDirection, view.LastPushAt,
                    view.State, view.NotAnsweringSince, view.NotAnsweringReason));
            }
            return Results.Ok(entries);
        });

        // POST /api/whitelist/backfill-revokes — heal legacy revoked rows that carry no
        // whitelist_revoke event. An earlier JoinEndpoints bug set status='R' on stale rows without
        // emitting the event, so the revocation never propagated and (post-migration-021) a stale
        // whitelist_add can resurrect the ghost node, since those rows sit at version 0. This emits
        // the missing, signed revoke event for each and stamps the row version, closing that hole.
        //
        // Deliberately operator-triggered, NOT an unattended startup task: it publishes real,
        // outward-facing revoke events to every peer on live data, so it belongs in a session
        // where someone is watching (see WhitelistRevokeBackfill's own remarks). Idempotent — once
        // every revoked row has its event, a second call heals 0 and changes nothing. Needs an
        // unlocked session because each revoke event is signed with this node's identity key.
        group.MapPost("/backfill-revokes", async (
            SessionService session,
            DbConnectionFactory dbFactory,
            INodeIdentityRepository nodeRepo,
            IEventLogger eventLogger,
            IWhitelistRepository repo) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var backfill = new WhitelistRevokeBackfill(dbFactory, nodeRepo, eventLogger, repo);
            var healed = await backfill.RunIfNeededAsync();
            return Results.Ok(new { healed });
        }).RequireNonAgent();

        // GET /api/whitelist — list active entries (no unlock required)
        group.MapGet("/", async (HttpContext ctx, IWhitelistRepository repo) =>
        {
            var entries = await repo.GetAllActiveAsync();
            return Results.Ok(entries.Select(WhitelistEntryResponse.From));
        });

        // GET /api/whitelist/{nodeId} — single entry
        group.MapGet("/{nodeId:guid}", async (Guid nodeId, HttpContext ctx, IWhitelistRepository repo) =>
        {
            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            return entry != null
                ? Results.Ok(WhitelistEntryResponse.From(entry))
                : Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));
        });

        // PUT /api/whitelist/{nodeId} — update DisplayName and CanGenerateEmbeddings. NOT the address: that has one door, /address below
        // (BMB-307 fix round: this route used to take ApiAddress with no password, no check of what answers there and no validation).
        group.MapPut("/{nodeId:guid}", async (
            Guid nodeId,
            UpdateWhitelistEntryRequest req,
            IWhitelistRepository repo,
            IEventLogger eventLogger,
            SessionService session,
            HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            // A client that still sends the address gets an answer, not a silent drop: the change it asked for would not be made.
            if (req.ApiAddress != null)
                return Results.BadRequest(new ErrorResponse(
                    $"The address is not changed here. Use PUT /api/whitelist/{nodeId}/address (master password; the new address is checked first)."));

            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            if (entry == null || entry.Status != "A")
                return Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));

            if (req.DisplayName != null) entry.DisplayName = req.DisplayName;
            if (req.CanGenerateEmbeddings.HasValue) entry.CanGenerateEmbeddings = req.CanGenerateEmbeddings.Value;

            // Tell the mesh, and stamp the row with the version that event published — the same
            // shape the /superadmin and /address handlers below already use. The event IS the
            // sync: without it, renaming a peer here stays a purely local edit and every other
            // node keeps the old values forever.
            var version = await eventLogger.LogWhitelistUpdateAsync(nodeId, apiAddress: null, entry.DisplayName);

            entry.UpdatedAt = DateTime.UtcNow;
            entry.LamportTs = version.LamportTs;
            entry.SourceNodeId = version.SourceNodeId;
            await repo.UpdateAsync(entry);
            eventLogger.SignalSync();
            return Results.Ok(WhitelistEntryResponse.From(entry));
        });

        // PUT /api/whitelist/{nodeId}/superadmin — promote or demote a peer
        group.MapPut("/{nodeId:guid}/superadmin", async (
            Guid nodeId,
            SetPeerSuperadminRequest req,
            IWhitelistRepository repo,
            IEventLogger eventLogger,
            SessionService session,
            IAuditLogRepository auditRepo,
            HttpContext ctx) =>
        {
            // A node that joins with the master password arrives as a superadmin (JoinAuthority,
            // BMB-42). This endpoint is how that authority is taken away again without cutting the
            // peer off from content entirely — and how a peer recorded content-only before BMB-42
            // gets it back. Before
            // demotion existed, "no longer trust this peer with cluster state" meant revoking it
            // outright, which also stopped it receiving content; this is the step in between.
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            // A blind node holds no DEK and must never steer cluster state (plan 3.2): refuse to
            // raise one, whatever its row says. Demoting one stays allowed — it only takes away.
            if (req.IsSuperadmin && BlindNodeId.IsBlind(nodeId))
                return Results.BadRequest(new ErrorResponse("A blind node can never be a superadmin"));

            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            if (entry == null || entry.Status != "A")
                return Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));

            if (entry.IsSuperadmin == req.IsSuperadmin)
                return Results.Ok(WhitelistEntryResponse.From(entry));

            // Tell the rest of the mesh FIRST, and write the row with the version that event
            // carried. Each node enforces the flag from its OWN whitelist row — EventApplier checks
            // it before accepting hard_delete, restore_network and whitelist changes — so a
            // demotion that reaches only this node leaves the peer just as powerful everywhere
            // else. A node that is offline picks it up on catch-up, and compares it against a row
            // that now agrees with what was announced.
            var version = await eventLogger.LogWhitelistUpdateAsync(nodeId, apiAddress: null, displayName: null,
                isSuperadmin: req.IsSuperadmin);

            entry.IsSuperadmin = req.IsSuperadmin;
            entry.UpdatedAt = DateTime.UtcNow;
            entry.LamportTs = version.LamportTs;
            entry.SourceNodeId = version.SourceNodeId;
            await repo.UpdateAsync(entry);
            eventLogger.SignalSync();

            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
            await auditRepo.LogAsync("whitelist", nodeId.ToString(),
                req.IsSuperadmin ? "peer_promoted" : "peer_demoted", "web",
                $"Peer {entry.DisplayName} {(req.IsSuperadmin ? "promoted to" : "demoted from")} superadmin by user {actor}");

            return Results.Ok(WhitelistEntryResponse.From(entry));
        }).RequireSuperadmin().RequireNonAgent();

        // PUT /api/whitelist/{nodeId}/address — change node URL with validation. Superadmin (the group), NOT an agent (a local agent key would otherwise
        // act as a superadmin here), the master password, and then PeerAddressService: the one writer of a peer's address, shared with the phone.
        group.MapPut("/{nodeId:guid}/address", async (
            Guid nodeId,
            ChangeNodeAddressRequest req,
            SessionService session,
            PeerAddressService addresses,
            HttpContext ctx,
            CancellationToken ct) =>
        {

            // 1. Re-authenticate. The whitelist_update event below is signed with the node identity
            // key, which needs the master DEK — so the vault must already be open, like the sibling
            // endpoints. This used to unlock the shared session with the supplied password when
            // locked (and skip the password entirely when not), turning a URL edit into a hidden
            // unlock path. Now the password is a plain re-authentication in both states.
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
            if (!await session.VerifyMasterPasswordAsync(req.Password))
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 403);

            // Everything else - the peer, not this node, the policy and the canonical form, no change when nothing changes, no other peer's address, what
            // answers there (a sync server, with this node id AND the key on record), the event, the loop's memory, the audit line - is the service's.
            // Its messages never quote the address.
            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
            var result = await addresses.ChangeAsync(nodeId, req.NewApiAddress, actor, "web", ct: ct);
            return result.Outcome switch
            {
                PeerAddressOutcome.NotFound => Results.NotFound(new ErrorResponse(result.Message)),
                _ when result.Succeeded => Results.Ok(WhitelistEntryResponse.From(result.Entry!)),
                _ => Results.BadRequest(new ErrorResponse(result.Message)),
            };
        }).RequireNonAgent();

        // PUT /api/whitelist/{nodeId}/hub — let blind copies call this node, or stop (ADR 0007)
        //
        // A blind copy (Windows, macOS, Android) never listens; it calls a node whose whitelist row says how to
        // trust its TLS endpoint: "pin" (the key the node presents now, recorded) or "public-ca" (the system's
        // chain must vouch for the certificate, name included). The row is verified before it is saved — the
        // address answers as this very node (its id AND its Ed25519 key, which the call code will carry) and the
        // TLS check of the mode passes — and then published as a whitelist_update, so every node and the pairing
        // screen see the same row. Superadmin, not an agent, master password re-authentication: it decides
        // which certificate every blind copy of this network will accept.
        group.MapPut("/{nodeId:guid}/hub", async (
            Guid nodeId,
            SetHubRequest req,
            IWhitelistRepository repo,
            IEventLogger eventLogger,
            SessionService session,
            INodeIdentityRepository nodeIdentityRepo,
            HubTrustProbe probe,
            BeeMemoryBank.Sync.Blind.SpkiPinRegistry pins,
            IAuditLogRepository auditRepo,
            PeerAddressService addresses,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            // Same re-authentication as the address change: the vault must be open (the event is signed with the
            // node identity key) and the password is a plain proof, never an unlock.
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
            if (string.IsNullOrEmpty(req.Password) || !await session.VerifyMasterPasswordAsync(req.Password))
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 403);

            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            if (entry == null || entry.Status != "A")
                return Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));

            var localIdentity = await nodeIdentityRepo.GetAsync();
            if (localIdentity != null && localIdentity.NodeId == nodeId)
                return Results.BadRequest(new ErrorResponse("This is your own node. Set this on a node that lists it as a trusted node."));
            if (BlindNodeId.IsBlind(nodeId))
                return Results.BadRequest(new ErrorResponse(
                    "A blind node is called by the certificate pinned in its pair code. Add it again from its pair code to change that."));

            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
            var mode = req.Trust?.Trim().ToLowerInvariant();

            if (mode == "off")
            {
                if (entry.EffectiveTlsTrust is null)
                    return Results.Ok(new HubSetResponse(WhitelistEntryResponse.From(entry), null, false, null));

                // "" takes the pin off for a node that predates the mode as well (WhitelistUpdatePayload).
                var off = await addresses.ApplyProofAsync(entry, new PeerProof(null, TlsSpki: "", TlsTrust: BlindTrust.None, PairingChanged: true), actor, "web");
                if (!off.Succeeded) return Results.BadRequest(new ErrorResponse(off.Message));
                pins.Invalidate();
                await auditRepo.LogAsync("whitelist", nodeId.ToString(), "hub_disabled", "web",
                    $"Blind copies can no longer be told to call {entry.DisplayName} (set by user {actor})");
                return Results.Ok(new HubSetResponse(WhitelistEntryResponse.From(entry), null, false,
                    "Blind copies can no longer be paired to call this node. Copies already paired keep calling it until it stops answering them."));
            }

            if (mode != BlindTrust.Pin && mode != BlindTrust.PublicCa)
                return Results.BadRequest(new ErrorResponse("Choose \"Pinned certificate\", \"Normal certificate\" or off."));

            var address = req.Address;
            if (string.IsNullOrWhiteSpace(address)) address = entry.ApiAddress;
            address = address?.Trim();
            if (!string.IsNullOrEmpty(address) && !address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
            if (BlindListener.Origin(address) is not { } origin)
                return Results.BadRequest(new ErrorResponse(
                    "Blind copies call https only: give the address as https://host or https://host:port, without a path."));
            // The one policy for a peer's address (no user name, no query, no link-local or metadata target), and the one canonical form that is stored and sent on.
            var canonical = PeerAddressPolicy.Canonicalize(origin);
            if (!canonical.Ok) return Results.BadRequest(new ErrorResponse(canonical.Refusal!));
            origin = canonical.Address!;

            string? expectedPin = null;
            if (mode == BlindTrust.Pin && !string.IsNullOrWhiteSpace(req.ExpectedPin))
            {
                expectedPin = req.ExpectedPin.Trim();
                if (!BeeMemoryBank.Api.Services.Recovery.RestoreTlsPin.IsWellFormed(expectedPin))
                    return Results.BadRequest(new ErrorResponse("The pin is not well formed: it is 43 characters (A-Z, a-z, 0-9, - and _)."));
            }

            var conflict = (await repo.GetAllActiveAsync()).FirstOrDefault(e => e.NodeId != nodeId
                && string.Equals(e.ApiAddress?.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
            if (conflict != null)
                return Results.BadRequest(new ErrorResponse($"URL is already used by node {conflict.DisplayName}"));

            HubProbeResult found;
            try
            {
                found = await probe.ProbeAsync(origin, mode, expectedPin, ct);
            }
            catch (HubProbeException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }

            if (found.NodeId != nodeId)
                return Results.BadRequest(new ErrorResponse(
                    $"The node at {origin} is not the node you chose ({nodeId}). This is a different node!"));
            if (found.Ed25519PublicKeyB64 != Convert.ToBase64String(entry.Ed25519PublicKey))
                return Results.BadRequest(new ErrorResponse(
                    $"The node at {origin} has the right id but another key than the one this network knows. Not trusting it."));

            var pin = mode == BlindTrust.Pin ? found.PresentedPin : null;
            if (!BlindListener.IsCallable(origin, pin, entry.Ed25519PublicKey, mode))
                return Results.BadRequest(new ErrorResponse("Could not read a certificate key from the node."));

            // The one writer of a peer's row on a running node: the probe read this key, so it is the proof; the event is logged first and the row carries its version
            // ("" removes a pin an older node may hold when the new mode pins nothing), and the loop forgets what it learned about the old address or the old key.
            var paired = await addresses.ApplyProofAsync(entry,
                new PeerProof(origin, ProvenKey: Convert.FromBase64String(found.Ed25519PublicKeyB64), TlsSpki: pin ?? "", TlsTrust: mode), actor, "web");
            if (!paired.Succeeded) return Results.BadRequest(new ErrorResponse(paired.Message));
            pins.Invalidate();
            await auditRepo.LogAsync("whitelist", nodeId.ToString(), "hub_enabled", "web",
                $"Blind copies can be told to call {entry.DisplayName} at {origin} ({mode}) (set by user {actor})");

            var notice = mode == BlindTrust.PublicCa
                ? "Blind copies will accept this node only with a certificate a public authority vouches for. Anyone a public authority can be made to issue a certificate for this name to could impersonate it to a blind copy; \"Pinned certificate\" avoids that."
                : expectedPin is null
                    ? "The key was read from the connection just now and nothing else vouched for it. Compare it with the key the node shows for itself if you can."
                    : "The key matches the pin you gave.";
            if (mode == BlindTrust.Pin && found.PubliclyTrusted)
                notice += " This certificate is also valid through a public authority: if it is renewed with a new key, the pin must be set again. \"Normal certificate\" does not have that problem.";
            return Results.Ok(new HubSetResponse(WhitelistEntryResponse.From(entry), pin, found.PubliclyTrusted, notice));
        }).RequireNonAgent();

        // PUT /api/whitelist/{nodeId}/auto-accept-restore — toggle auto-accept restore
        group.MapPut("/{nodeId:guid}/auto-accept-restore", async (
            Guid nodeId,
            SetAutoAcceptRestoreRequest req,
            IWhitelistRepository repo,
            SessionService session,
            INodeIdentityRepository nodeIdentityRepo,
            HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            if (entry == null || entry.Status != "A")
                return Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));

            var localIdentity = await nodeIdentityRepo.GetAsync();
            if (localIdentity != null && localIdentity.NodeId == nodeId)
                return Results.BadRequest(new ErrorResponse("Cannot set auto-accept for the local node"));

            // A blind node holds no DEK and never steers cluster state; letting its restore replace this
            // vault unattended is the one thing it must never be trusted with.
            if (req.AutoAccept && BlindNodeId.IsBlind(nodeId))
                return Results.BadRequest(new ErrorResponse("Auto-accept restore can never be enabled for a blind node."));

            await repo.SetAutoAcceptRestoreAsync(nodeId.ToString(), req.AutoAccept);
            return Results.Ok(new { success = true, autoAccept = req.AutoAccept });
        });

        // PUT /api/whitelist/{nodeId}/auto-accept-dek-rotation — toggle auto-accept DEK rotation
        group.MapPut("/{nodeId:guid}/auto-accept-dek-rotation", async (
            Guid nodeId,
            SetAutoAcceptDekRotationRequest req,
            IWhitelistRepository repo,
            SessionService session,
            INodeIdentityRepository nodeIdentityRepo,
            HttpContext ctx) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            if (entry == null || entry.Status != "A")
                return Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));

            var localIdentity = await nodeIdentityRepo.GetAsync();
            if (localIdentity != null && localIdentity.NodeId == nodeId)
                return Results.BadRequest(new ErrorResponse("Cannot set auto-accept for the local node"));

            // Enabling auto-accept DEK rotation for a peer means "I let this peer rewrap my entire
            // vault's DEK, unattended, whenever it proposes a rotation" — a superadmin-level trust.
            // A demoted peer (or one recorded before BMB-42) is content-only; refuse to arm
            // auto-accept for a non-superadmin peer so an operator can't accidentally grant DEK
            // authority to a content-only node it would otherwise reason is harmless. Disabling is
            // always allowed.
            if (req.AutoAccept && (!entry.IsSuperadmin || BlindNodeId.IsBlind(nodeId)))
                return Results.BadRequest(new ErrorResponse(
                    "Auto-accept DEK rotation can only be enabled for a superadmin peer."));

            await repo.SetAutoAcceptDekRotationAsync(nodeId.ToString(), req.AutoAccept);
            return Results.Ok(new { success = true, autoAccept = req.AutoAccept });
        });

        // DELETE /api/whitelist/{nodeId} — revoke access (requires unlock)
        group.MapDelete("/{nodeId:guid}", async (
            Guid nodeId,
            IWhitelistRepository repo,
            IEventLogger eventLogger,
            SessionService session,
            HttpContext ctx,
            IPeerStateResetter? peerState) =>
        {
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var entry = await repo.GetByNodeIdAsync(nodeId, includeDeleted: true);
            if (entry == null || entry.Status != "A")
                return Results.NotFound(new ErrorResponse($"Node {nodeId} not found in whitelist"));

            // Log first: the revoke's version is what a later whitelist_add is compared against,
            // and an add issued BEFORE this revoke must lose that comparison.
            var version = await eventLogger.LogWhitelistRevokeAsync(nodeId);
            await repo.RevokeAsync(nodeId, version);
            peerState?.PeerChanged(nodeId);   // what the sync loop learned about this peer is forgotten (BMB-313)

            return Results.NoContent();
        });
    }
}
