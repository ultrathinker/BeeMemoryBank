using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindLimits;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// Blind nodes (plan 4, 5.2, 10): what a blind node serves — its pair code, the seed it takes in,
/// the replica it hands to an Android blind node — and the PC's side of pairing and reseeding.
/// </summary>
public static class BlindEndpoints
{
    /// <summary>The header a first seed carries the pair code's one-time secret in.</summary>

    /// <summary>
    /// Mapped on a blind node only: pairing and seeding. (The blind node's own subsystems — status,
    /// backups, console, wipe — are <c>MapBlindNodeEndpoints</c>, BMB-54.)
    /// </summary>
    public static void MapBlindSeedEndpoints(this WebApplication app)
    {
        // "Local admin" (CONTRACTS §2): the blind console on the same host, with the internal key —
        // signed in as the superadmin, like the rest of the console's routes.
        var local = app.MapGroup("/api/blind").WithTags("Blind").RequireInternalKey().RequireSuperadmin();
        local.MapGet("/pair-code", async (BlindPairing pairing) => Results.Ok(PairCodeDto(await pairing.GetCodeAsync(renew: false))));
        local.MapPost("/pair-code/renew", async (BlindPairing pairing) => Results.Ok(PairCodeDto(await pairing.GetCodeAsync(renew: true))));

        app.MapPost("/api/blind/seed", async (
            HttpContext ctx,
            Guid seedId, long offset, long total, string sha256,
            BlindSeedService seeds,
            BlindPairing pairing,
            SyncTokenStore store,
            IWhitelistRepository whitelist,
            ILogger<BlindSeedService> logger,
            CancellationToken ct) =>
        {
            if (await ResolveAuthorityAsync(ctx, seedId, pairing, store, whitelist) is not { } authority)
                return Results.Unauthorized();
            // The seeder is known: the traffic of this part is reserved now (the storage limit is judged by the seed itself, part by part).
            if (ctx.ReserveForPeer() is { } admissionPause) return new BlindPauseResult(admissionPause);
            try
            {
                var progress = await seeds.ReceiveAsync(seedId, offset, total, sha256, authority, ctx.Request.Body, ct, ctx.Request.ContentLength);
                return progress.Applied
                    ? Results.Ok(new { seed_id = progress.SeedId, status = "applied" })
                    : Results.Json(new { seed_id = progress.SeedId, received = progress.Received, total = progress.Total },
                        statusCode: StatusCodes.Status202Accepted);
            }
            catch (BlindSeedConflictException ex)
            {
                return Results.Json(new { error = "SEED_IN_PROGRESS", seed_id = ex.ActiveSeedId }, statusCode: 409);
            }
            catch (BlindSeedOffsetException ex)
            {
                return Results.Json(new { error = "OFFSET_MISMATCH", seed_id = seedId, received = ex.Received }, statusCode: 409);
            }
            catch (BlindSeedAuthorityGoneException)
            {
                return Results.Unauthorized();
            }
            catch (BlindSeedWipingException)
            {
                ctx.Response.Headers.RetryAfter = "5";
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            catch (BlindStoragePausedException ex)
            {
                // The node's storage limit, reached while the seed arrives: the same answer the door gives (503, X-BMB-Paused: disk, Retry-After), which the full node reads as a pause.
                return new BlindPauseResult(ex.Pause);
            }
            catch (BlindSeedRejectedException ex)
            {
                // The sender is told too, but it is the operator of THIS node who looks in the log first. The reason is a CODE and the fixed sentence of that code
                // (BlindSeedRefusals): nothing that was written about the failure underneath can leave this node. The log says the code and the TYPE of the
                // cause, never its text; the exception object itself is not logged.
                var reason = BlindSeedRefusals.Sentence(ex.Reason);
                logger.LogWarning("Blind seed {SeedId} refused: {Code}: {Reason} (cause: {Cause})", seedId, ex.Reason, reason, ex.InnerException?.GetType().Name ?? "none");
                return Results.Json(new ErrorResponse(reason), statusCode: 400);
            }
            catch (InsufficientStorageException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: StatusCodes.Status507InsufficientStorage);
            }
        }).WithMetadata(BlindMeteredEndpoint.Instance).WithTags("Blind");

        app.MapGet("/api/blind/seed/{seedId:guid}", async (
            HttpContext ctx, Guid seedId, BlindSeedService seeds, BlindPairing pairing, SyncTokenStore store,
            IWhitelistRepository whitelist) =>
        {
            if (await ResolveAuthorityAsync(ctx, seedId, pairing, store, whitelist) is null) return Results.Unauthorized();
            return seeds.GetProgress(seedId) is { } p
                ? Results.Ok(new { seed_id = p.SeedId, received = p.Received, total = p.Total })
                : Results.NotFound();
        }).WithTags("Blind");
    }

    /// <summary>How long a caller waits before it asks an activated node without a sealed baseline again (spec 9.7).</summary>
    public const int NoBaselineRetryAfterSeconds = 600;

    /// <summary>
    /// Mapped on every node: a hub serves replicas too (plan 10). Once activated (step-a-spec 8.7) the replica is format 2:
    /// a <c>full</c> package, or with <c>?have=&lt;baseline id&gt;</c> 304 when that is still the baseline here, else a
    /// <c>baseline</c> package (the caller fetches missing blobs with /api/sync/blobs/get). An activated node with nothing
    /// sealed to serve answers 503 with Retry-After, never format 1.
    /// </summary>
    public static void MapBlindReplicaEndpoint(this WebApplication app)
    {
        app.MapGet("/api/blind/replica", async (
            HttpContext ctx, SyncTokenStore store, BlindReplicaPackageCache packages, INodeRole role, string? have, CancellationToken ct) =>
        {
            if (await SyncEndpoints.AuthenticatePeerAsync(ctx, store) is not { } peer) return Results.Unauthorized();
            if (ctx.ReserveForPeer() is { } admissionPause) return new BlindPauseResult(admissionPause);
            // Only for a blind peer: a full node joins through /api/join and gets the join snapshot,
            // which is what its own restore and key handling expect.
            if (!BlindNodeId.IsBlind(peer)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            // A hub answers the internet (ADR 0007): one peer's token cannot pull the package in a loop. After the
            // token and the blind mark are checked, so a caller without either learns nothing about the limit.
            if (!packages.TryAdmit(peer))
            {
                ctx.Response.Headers.RetryAfter = ((int)BlindReplicaPackageCache.AdmissionWindow.TotalSeconds).ToString();
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            if (ctx.RequestServices.GetService<ISyncProtocolPolicy>() is { SealsMetadata: true })
            {
                if (await ServableBaselineAsync(ctx, ct) is not { } baseline)
                    return NoSealedBaseline(ctx);
                if (have is not null && Guid.TryParse(have, out var held) && held == baseline.Files.Manifest.BaselineId)
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                var kind = have is null ? BlindPackageKinds.Full : BlindPackageKinds.Baseline;
                return await ServeAsync(ctx, await packages.AcquireV2Async(kind, baseline, ct), ct);
            }

            // The producer's row in the package is what makes it a reseed authority on the receiving
            // blind node, so it says superadmin only where the network verifiably does: a blind node
            // never is, and a full node asks its full peers (review L-merge #2). The cache asks only
            // when it builds a package, never for a request the cached one answers, so a Range resume
            // costs no peer calls and cannot swap the signed bytes under the client.
            var lease = await packages.AcquireAsync(async buildCt =>
            {
                // Only a full node can know it (IReplicaProducerAuthority asks its full peers); a blind node registers none. Resolved here,
                // when a package is really built, as the code this replaced resolved BlindPreflight: not for a request that is answered 401/403
                // or served from the cache.
                if (role.IsBlind || ctx.RequestServices.GetService<IReplicaProducerAuthority>() is not { } producerAuthority) return false;
                return await producerAuthority.IsSuperadminAsync(buildCt);
            }, ct);
            return await ServeAsync(ctx, lease, ct);
        }).WithMetadata(BlindMeteredEndpoint.Instance).WithTags("Blind");
    }

    private static async Task<IResult> ServeAsync(HttpContext ctx, BlindReplicaPackageCache.Lease lease, CancellationToken ct)
    {
        try
        {
            var package = lease.Served;
            ctx.Response.Headers["X-BMB-Package-Sha256"] = package.Sha256;
            // The detached signature, as the restore route sends it: an Android blind node keeps it with the
            // package in its backups, so a restore from a backup checks the same two signatures.
            ctx.Response.Headers["X-BMB-Snapshot-Signature"] = Convert.ToBase64String(await File.ReadAllBytesAsync(package.FilePath + ".sig", ct));
            ctx.Response.Headers["X-BMB-Blind-Seed-Id"] = package.SeedId.ToString();
            var stream = new FileStream(package.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                bufferSize: 81920, FileOptions.Asynchronous);
            ctx.Response.OnCompleted(() => lease.DisposeAsync().AsTask());
            return Results.File(stream, "application/gzip", "bmb-blind-package.tar.gz", enableRangeProcessing: true);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    /// <summary>What this node can serve sealed (spec 9.7): a blind host's adopted baseline, a full hub's newest produced one.</summary>
    internal static async Task<ServableBaseline?> ServableBaselineAsync(HttpContext ctx, CancellationToken ct) =>
        ctx.RequestServices.GetService<IBaselineSource>() is { } source ? await source.GetServableAsync(ct) : null;

    /// <summary>An activated node with no sealed baseline yet: 503 and Retry-After, never a format-1 package (spec 9.7).</summary>
    internal static IResult NoSealedBaseline(HttpContext ctx)
    {
        ctx.Response.Headers.RetryAfter = NoBaselineRetryAfterSeconds.ToString();
        return Results.Json(new { error = "NO_SEALED_BASELINE", message = "This node has no sealed baseline yet." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// The seeder named under the pair code's secret (first seed, <see cref="BlindSeederProof"/>) or
    /// the sync token of a peer this node marks superadmin (reseed, plan 5.2). Checked on every part,
    /// so an ordinary peer is refused before it has uploaded anything. A presented proof is decided on
    /// its own: a wrong one is not rescued by a token.
    /// </summary>
    private static async Task<BlindSeedAuthority?> ResolveAuthorityAsync(
        HttpContext ctx, Guid seedId, BlindPairing pairing, SyncTokenStore store, IWhitelistRepository whitelist)
    {
        var headers = ctx.Request.Headers;
        if (headers.TryGetValue(BlindSeederProof.MacHeader, out var mac))
            return Guid.TryParse(headers[BlindSeederProof.NodeIdHeader].ToString(), out var seeder)
                   && headers[BlindSeederProof.KeyHeader].ToString() is { Length: > 0 } key
                   && await pairing.VerifySeederAsync(seedId, seeder, key, mac.ToString()) is { } codeId
                ? new BlindSeedAuthority.PairingSecret(seeder, key, codeId)
                : null;

        if (await SyncEndpoints.AuthenticatePeerAsync(ctx, store) is not { } peer) return null;
        return await whitelist.GetByNodeIdAsync(peer) is { IsSuperadmin: true }
            ? new BlindSeedAuthority.SuperadminPeer(peer)
            : null;
    }

    private static object PairCodeDto(Sync.Blind.BlindPairCode code) =>
        new
        {
            code = code.ToString(), node_id = code.NodeId, address = code.Address, expires_at = code.ExpiresAt,
            notice = BlindPairing.AuthorityNotice
        };
}
