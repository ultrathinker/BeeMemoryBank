using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;

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
            CancellationToken ct) =>
        {
            if (await ResolveAuthorityAsync(ctx, seedId, pairing, store, whitelist) is not { } authority)
                return Results.Unauthorized();
            try
            {
                var progress = await seeds.ReceiveAsync(seedId, offset, total, sha256, authority, ctx.Request.Body, ct);
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
            catch (BlindSeedRejectedException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 400);
            }
            catch (InsufficientStorageException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: StatusCodes.Status507InsufficientStorage);
            }
        }).WithTags("Blind");

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

    /// <summary>Mapped on every node: a hub serves replicas too (plan 10).</summary>
    public static void MapBlindReplicaEndpoint(this WebApplication app)
    {
        app.MapGet("/api/blind/replica", async (
            HttpContext ctx, SyncTokenStore store, BlindReplicaPackageCache packages, INodeRole role, CancellationToken ct) =>
        {
            if (await SyncEndpoints.AuthenticatePeerAsync(ctx, store) is not { } peer) return Results.Unauthorized();
            // Only for a blind peer: a full node joins through /api/join and gets the join snapshot,
            // which is what its own restore and key handling expect.
            if (!BlindNodeId.IsBlind(peer)) return Results.StatusCode(StatusCodes.Status403Forbidden);

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
            try
            {
                var package = lease.Package;
                ctx.Response.Headers["X-BMB-Package-Sha256"] = package.Sha256;
                // The detached signature, as the restore route sends it: an Android blind node keeps it with the
                // package in its backups, so a restore from a backup checks the same two signatures.
                ctx.Response.Headers["X-BMB-Snapshot-Signature"] = Convert.ToBase64String(await File.ReadAllBytesAsync(package.FilePath + ".sig", ct));
                ctx.Response.Headers["X-BMB-Blind-Seed-Id"] = package.Manifest.SeedId.ToString();
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
        }).WithTags("Blind");
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
