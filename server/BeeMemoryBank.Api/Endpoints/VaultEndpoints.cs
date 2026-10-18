using System.Globalization;
using System.Text.Json.Serialization;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.AspNetCore.Mvc;
using BeeMemoryBank.Infrastructure.AutoUnlock;
using BeeMemoryBank.Infrastructure.OsAutoUnlock;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>The routes of the encrypted database (docs/design/step-b-spec.md 6.7, 10): its status, and "Encrypt this node".</summary>
public static class VaultEndpoints
{
    public sealed record InboxStatus(
        [property: JsonPropertyName("events")] long Events,
        [property: JsonPropertyName("bytes")] long Bytes,
        [property: JsonPropertyName("blobs")] long Blobs,
        [property: JsonPropertyName("blob_bytes")] long BlobBytes,
        [property: JsonPropertyName("oldest_received_at")] DateTime? OldestReceivedAt);

    public sealed record AutoUnlockStatus(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("os")] bool Os,
        [property: JsonPropertyName("server")] bool Server);

    public sealed record MigrationStatus(
        [property: JsonPropertyName("phase")] string? Phase,
        [property: JsonPropertyName("percent")] int Percent,
        [property: JsonPropertyName("step")] string? Step,
        [property: JsonPropertyName("error_code")] string? ErrorCode,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("wipe_pending")] IReadOnlyList<string> WipePending,
        [property: JsonPropertyName("linked_plain")] IReadOnlyList<string> LinkedPlain);

    /// <summary>
    /// What <c>GET /api/vault/status</c> says. It answers while the database is closed (from the boot store, the gate and the files), carries no
    /// path, no size of any plain file and no count of anything inside the sealed database, and no key.
    /// </summary>
    public sealed record VaultStatus(
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("sealed_at")] string? SealedAt,
        [property: JsonPropertyName("dbk_generation")] int? DbkGeneration,
        [property: JsonPropertyName("inbox")] InboxStatus? Inbox,
        [property: JsonPropertyName("auto_unlock")] AutoUnlockStatus AutoUnlock,
        [property: JsonPropertyName("last_refusal")] string? LastRefusal,
        [property: JsonPropertyName("migration")] MigrationStatus? Migration);

    public sealed record SealRequest([property: JsonPropertyName("password")] string? Password);

    public sealed record ErasePlainCopiesRequest(
        [property: JsonPropertyName("password")] string? Password,
        [property: JsonPropertyName("paths")] string[]? Paths);

    public static void MapVaultEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/vault").WithTags("Vault");

        // Anonymous like /api/session/status: the login page and the encryption page ask it before anybody is signed in.
        // A block body with a return: an expression-bodied async lambda over HttpContext alone converts to RequestDelegate (its value is discarded and the
        // answer is an empty 200), the one overload that is not wanted here (analyzer ASP0016).
        group.MapGet("/status", async (HttpContext ctx) =>
            {
                var status = await BuildStatusAsync(ctx.RequestServices, ctx.RequestAborted);
                return Results.Json(status);
            })
            .WithMetadata(new SkipInternalKey());

        // "Encrypt this node" (spec 10): the internal key, a superadmin, never an agent or a remote token, and the master password again for
        // everything that changes or erases something.
        var owner = app.MapGroup("/api/vault").WithTags("Vault").RequireInternalKey().RequireSuperadmin().RequireNonAgent();

        owner.MapGet("/seal/preflight", async (HttpContext ctx, SealMigrationService seal) =>
        {
            var result = await seal.PreflightAsync(new SealOptions(CallerIsSuperadmin: true), ctx.RequestAborted);
            return Results.Json(new
            {
                can_start = result.CanStart,
                blockers = result.Blockers.Select(b => new { code = b.Code, message = b.Message }),
                warnings = result.Warnings.Select(w => new { code = w.Code, message = w.Message }),
                plain_copies = result.PlainCopies.Select(CopyBody),
                database_bytes = result.DatabaseBytes,
            });
        });

        owner.MapPost("/seal", async (SealRequest req, HttpContext ctx, SealMigrationService seal) =>
        {
            if (string.IsNullOrEmpty(req.Password))
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: StatusCodes.Status401Unauthorized);
            try
            {
                await seal.StartAsync(req.Password, new SealOptions(CallerIsSuperadmin: true), ctx.RequestAborted);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: StatusCodes.Status401Unauthorized);
            }
            catch (SealMigrationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message, ex.Code), statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Json(ProgressBody(seal.Progress ?? new SealProgress(SealProgress.Running, 1, "start")), statusCode: StatusCodes.Status202Accepted);
        });

        owner.MapGet("/seal/progress", (SealMigrationService seal) => Results.Json(ProgressBody(seal.Progress ?? new SealProgress(SealProgress.Idle, 0, "idle"))));

        owner.MapPost("/seal/erase-plain-copies", async (ErasePlainCopiesRequest req, HttpContext ctx, SessionService session, IVaultGate gate) =>
        {
            if (string.IsNullOrEmpty(req.Password) || !await session.VerifyMasterPasswordAsync(req.Password))
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: StatusCodes.Status401Unauthorized);
            if (req.Paths is not { Length: > 0 })
                return Results.Json(new ErrorResponse("Name the files to erase.", "paths_required"), statusCode: StatusCodes.Status400BadRequest);

            var dataPath = ctx.RequestServices.GetRequiredService<SnapshotService>().DataPath;
            var result = PlainCopyEraser.Erase(dataPath, gate.IsSealed ? PlainScanMode.Sealed : PlainScanMode.Preflight, req.Paths);

            // A count, no names (spec 10.2).
            var count = result.Erased.Count + result.UnlinkedOnly.Count;
            if (count > 0)
                await AuditAsync(ctx.RequestServices, "plain_copies_erased", $"{count} unencrypted copies of the vault were erased.");
            return Results.Json(new
            {
                erased = result.Erased,
                unlinked_only = result.UnlinkedOnly,
                not_erased = result.NotErased,
                refused = result.Refused.Select(r => new { path = r.Path, kind = r.Kind }),
                remaining = result.Remaining.Select(CopyBody),
            });
        });

    }

    private static object CopyBody(PlainCopyFinding c) => new
    {
        path = c.RelativePath,
        bytes = c.Bytes,
        last_write = c.LastWriteUtc,
        kind = c.Kind,
        detail = c.Detail,
        can_be_erased = PlainCopyKinds.CanBeErased(c.Kind),
    };

    private static async Task AuditAsync(IServiceProvider services, string action, string detail)
    {
        try
        {
            await services.GetRequiredService<IAuditLogRepository>().LogAsync("node", "-", action, "web", detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger("VaultEndpoints").LogWarning("The audit entry {Action} could not be written.", action);
        }
    }

    private static object ProgressBody(SealProgress p) => new
    {
        phase = p.Phase,
        percent = p.Percent,
        step = p.Step,
        error_code = p.ErrorCode,
        error = p.Error,
        plain_copies_found = p.PlainCopiesFound,
    };

    public static async Task<VaultStatus> BuildStatusAsync(IServiceProvider services, CancellationToken ct)
    {
        var gate = services.GetRequiredService<IVaultGate>();
        var start = services.GetRequiredService<SealedStartState>();
        var role = (services.GetService<HostRole?>() ?? HostRole.Server).Word();

        var os = false;
        if (services.GetService<OsAutoUnlockService>() is { IsSupported: true } osService)
        {
            try { os = await osService.IsEnabledAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { os = false; }   // a secret store that cannot answer is not "on"
        }
        var server = false;
        if (services.GetService<ServerAutoUnlockService>() is { } serverService)
        {
            try { server = (await serverService.GetStatusAsync(ct)).Enabled; }
            catch (Exception ex) when (ex is not OperationCanceledException) { server = false; }
        }
        var autoUnlock = new AutoUnlockStatus(role, os, server);
        var migration = MigrationStatusOf(services, start);

        if (!gate.IsSealed)
            return new VaultStatus("plain", gate.State.ToString(), null, null, null, autoUnlock, null, migration);

        var boot = services.GetRequiredService<BootStore>();
        string? sealedAt = null;
        int? generation = null;
        InboxStatus? inbox = null;
        if (boot.Exists)
        {
            sealedAt = await boot.GetMetaAsync(BootMetaKeys.SealedAt, ct);
            if (int.TryParse(await boot.GetMetaAsync(BootMetaKeys.DbkGeneration, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var g)) generation = g;
            var summary = await services.GetRequiredService<IRelayInbox>().GetSummaryAsync(ct);
            inbox = new InboxStatus(summary.Events, summary.EventBytes, summary.Blobs, summary.BlobBytes, summary.OldestReceivedAt);
        }
        var refusal = services.GetService<ISealedUnlockDiagnostics>() is VaultGate g2 ? await g2.ReadLastRefusalAsync(ct) : null;
        return new VaultStatus("sealed", gate.State.ToString(), sealedAt, generation, inbox, autoUnlock, refusal, migration);
    }

    /// <summary>
    /// What is still to be wiped (read from the disk now: a replaced plain file that is still there), the hard links a start or this process's
    /// migration unlinked, and what a running migration reports; null when there is nothing to say.
    /// </summary>
    private static MigrationStatus? MigrationStatusOf(IServiceProvider services, SealedStartState start)
    {
        var seal = services.GetService<SealMigrationService>();
        var dataPath = services.GetService<SnapshotService>()?.DataPath;
        IReadOnlyList<string> wipePending = dataPath is null ? start.Seal?.WipePendingNames ?? [] : SealSwapResolver.Leftovers(dataPath);
        IReadOnlyList<string> linked = [.. start.LinkedPlain, .. seal?.LastSwap?.LinkedPlainNames ?? []];
        var running = seal?.Progress;
        if (running is null && wipePending.Count == 0 && linked.Count == 0) return null;
        return new MigrationStatus(running?.Phase, running?.Percent ?? 0, running?.Step, running?.ErrorCode, running?.Error, wipePending, linked);
    }
}
