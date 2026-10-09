using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>Body of POST /api/blind/claim: the restored device asking to be trusted.</summary>
public sealed record BlindClaimRequest(Guid NodeId, string DisplayName, string PublicKeyB64, string? ApiAddress);

/// <summary>What the blind node tells a restoring device about itself.</summary>
public sealed record BlindClaimResponse(Guid BlindNodeId, string DisplayName, string PublicKeyB64);

/// <summary>
/// The signed events the blind node holds: a restore checks rows newer than the last anchor against them.
/// Who the restored node trusts and how far it has pulled come from the package's signed
/// blind-manifest.json, not from here.
/// </summary>
public sealed record BlindRestoreEvents(IReadOnlyList<SyncEvent> Events);

/// <summary>
/// Restore from a blind node (plan 6.7). Only a node in the blind role answers; everywhere else these
/// routes are 404. The code comes from the blind node's own console (local admin, internal key); the
/// package, the events and the claim are public routes guarded by that one-time code.
/// </summary>
public static class BlindRestoreEndpoints
{
    public const string RestoreCodeHeader = "X-Restore-Code";
    private const int MaxEvents = 200_000;

    public static void MapBlindRestoreEndpoints(this WebApplication app)
    {
        // POST /api/blind/restore-code — issue a code (logged). Local admin only. The code names this node
        // (NodeId, key fingerprint, address, TLS pin) around the one-time secret: BlindRestoreCode.
        app.MapPost("/api/blind/restore-code", async (INodeRole role, BlindRestoreCodeService codes, HttpContext ctx,
            INodeIdentityRepository nodeRepo, IConfiguration config) =>
        {
            if (!role.IsBlind) return Results.NotFound();
            var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException("Node not initialized");
            var tls = ctx.RequestServices.GetRequiredService<BlindTlsIdentity>();
            var (secret, expiresAt) = await codes.IssueAsync(ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "console");
            var address = config["BMB_PUBLIC_ADDRESS"];
            var code = new BlindRestoreCode(identity.NodeId, BlindRestoreCode.FingerprintOf(identity.Ed25519PublicKey),
                string.IsNullOrWhiteSpace(address) ? null : address.Trim().TrimEnd('/'), tls.Spki, secret, expiresAt);
            return Results.Ok(new { code = code.ToString(), expiresAt });
        }).RequireInternalKey().RequireSuperadmin().WithTags("BlindRestore");

        // GET /api/blind/restore-codes — the issue log (never the codes).
        app.MapGet("/api/blind/restore-codes", async (INodeRole role, BlindRestoreCodeService codes) =>
            role.IsBlind ? Results.Ok(await codes.LogAsync()) : Results.NotFound())
            .RequireInternalKey().RequireSuperadmin().WithTags("BlindRestore");

        // GET /api/blind/restore/package — the blind package (BlindPackageBuilder: the signed peer snapshot,
        // tbl_hard_delete_audit, projections nulled, blind-manifest.json), signed by this node.
        app.MapGet("/api/blind/restore/package", async (
            HttpContext ctx, INodeRole role, BlindRestoreCodeService codes, BlindPackageBuilder builder,
            INodeIdentityRepository nodeRepo, CancellationToken ct) =>
        {
            if (!role.IsBlind) return Results.NotFound();
            if (!await codes.ValidateAsync(ctx.Request.Headers[RestoreCodeHeader].FirstOrDefault()))
                return Results.Unauthorized();

            var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException("Node not initialized");
            // A blind node is never a superadmin, in its own package as anywhere else.
            var package = await builder.BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: false, ct);
            // The join import checks the sidecar signature; the embedded one is checked first (RestoreFromPackageAsync).
            var sigPath = package.FilePath + ".sig";
            var signature = File.Exists(sigPath) ? await File.ReadAllBytesAsync(sigPath, ct) : [];
            try { File.Delete(sigPath); } catch (IOException) { }

            ctx.Response.Headers["X-BMB-Snapshot-Signature"] = Convert.ToBase64String(signature);
            ctx.Response.Headers["X-BMB-Snapshot-Producer"] = identity.NodeId.ToString();
            ctx.Response.Headers["X-BMB-Snapshot-Producer-Key"] = Convert.ToBase64String(identity.Ed25519PublicKey);
            // Built per request; it goes away when the response is done with it.
            var stream = new FileStream(package.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            return Results.File(stream, "application/gzip", "bmb-blind-restore.tar.gz");
        }).WithMetadata(new SkipInternalKey()).WithTags("BlindRestore");

        // GET /api/blind/restore/events — signed events this node still holds, with the keys to check
        // them: a restore verifies rows newer than the last anchor against these (plan 5.5).
        app.MapGet("/api/blind/restore/events", async (
            HttpContext ctx, INodeRole role, BlindRestoreCodeService codes, IEventLogRepository eventLog) =>
        {
            if (!role.IsBlind) return Results.NotFound();
            if (!await codes.ValidateAsync(ctx.Request.Headers[RestoreCodeHeader].FirstOrDefault()))
                return Results.Unauthorized();

            // Wire hygiene: this node's stored events carry actor fields and transported entity
            // ids locally; a restoring device re-derives both, so they do not travel (SyncWire).
            return Results.Json(
                new BlindRestoreEvents(SyncWire.Strip(await eventLog.GetAllAfterSequenceAsync(0, MaxEvents))),
                SyncWire.Options);
        }).WithMetadata(new SkipInternalKey()).WithTags("BlindRestore");

        // POST /api/blind/claim — the restored device becomes this blind node's superadmin, locally:
        // the new root of trust exists only here (plan 6.7 step 4); no restore_network.
        app.MapPost("/api/blind/claim", async (
            BlindClaimRequest req, HttpContext ctx, INodeRole role, BlindRestoreCodeService codes,
            IWhitelistRepository whitelist, INodeIdentityRepository nodeRepo, IAuditLogRepository audit) =>
        {
            if (!role.IsBlind) return Results.NotFound();
            var code = ctx.Request.Headers[RestoreCodeHeader].FirstOrDefault();
            // Open, or spent by this same device (a repeated claim, see below).
            if (!await codes.ValidateAsync(code, claimant: req.NodeId)) return Results.Unauthorized();

            byte[] publicKey;
            try { publicKey = Convert.FromBase64String(req.PublicKeyB64 ?? ""); }
            catch (FormatException) { return Results.BadRequest(new ErrorResponse("Bad public key.")); }
            if (publicKey.Length != 32 || req.NodeId == Guid.Empty || BlindNodeId.IsBlind(req.NodeId))
                return Results.BadRequest(new ErrorResponse("Bad node id or public key."));

            // Idempotent for the device that spent the code (review release-a #9): the code is spent before the whitelist
            // and audit writes, which are not in its transaction, so a failure between them used to leave the code burnt and
            // the device untrusted. The device's retry (the restore client retries a 5xx) now repeats the claim. A second
            // device is refused (another node id). A repeat may only finish what the first claim did not write - no row
            // yet - or confirm the row it wrote (active, same key): it never changes the key of a trusted device, and never
            // brings back a row that was revoked since.
            var use = await codes.ConsumeAsync(code!, req.NodeId);
            if (use == RestoreCodeUse.Refused) return Results.Unauthorized();

            var now = DateTime.UtcNow;
            var existing = await whitelist.GetByNodeIdAsync(req.NodeId, includeDeleted: true);
            if (use == RestoreCodeUse.Repeat && existing != null
                && !(existing is { Status: "A", DeletedAt: null } && existing.Ed25519PublicKey.AsSpan().SequenceEqual(publicKey)))
                return Results.Conflict(new ErrorResponse(
                    "This device's claim is already recorded with another key, or was revoked since; a used restore code cannot change that. Issue a new code."));
            // A repeat that got past the check above names an active row with the same key: that row is already what the first claim
            // wrote, so nothing is rewritten (a repeat must not change the name or the address kept for the device).
            var entry = existing ?? new WhitelistEntry { NodeId = req.NodeId, CreatedAt = now };
            if (!(use == RestoreCodeUse.Repeat && existing != null))
            {
                entry.DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? "Restored device" : req.DisplayName.Trim();
                entry.Ed25519PublicKey = publicKey;
                entry.ApiAddress = req.ApiAddress;
                entry.Status = "A";
                entry.DeletedAt = null;
                entry.IsSuperadmin = true;
                entry.UpdatedAt = now;
                if (existing == null) await whitelist.CreateAsync(entry);
                else await whitelist.UpdateAsync(entry);
            }

            await audit.LogAsync("whitelist", req.NodeId.ToString(), "blind_restore_claimed", "restore",
                use == RestoreCodeUse.Repeat
                    ? $"Restored device {entry.DisplayName} ({req.NodeId}) repeated its claim of this blind node with the restore code it had used"
                    : $"Restored device {entry.DisplayName} ({req.NodeId}) claimed this blind node with a restore code");

            var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException("Node not initialized");
            return Results.Ok(new BlindClaimResponse(identity.NodeId, identity.DisplayName, Convert.ToBase64String(identity.Ed25519PublicKey)));
        }).WithMetadata(new SkipInternalKey()).WithTags("BlindRestore");
    }
}
