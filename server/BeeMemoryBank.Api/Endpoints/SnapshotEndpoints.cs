using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text.Json;

namespace BeeMemoryBank.Api.Endpoints;

public static class SnapshotEndpoints
{
    public static void MapSnapshotEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/snapshots").WithTags("Snapshots");

        group.MapGet("/", (SnapshotService svc, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            // Snapshot filenames carry timestamps and originator node IDs. Restrict
            // listing to superadmins (matches restore/upload/delete which already gate
            // on this) — non-superadmin Users have no admin reason to enumerate them.
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            return Results.Ok(svc.List());
        });

        group.MapPost("/", async (SnapshotService svc, SessionService session, HttpContext ctx, IAuditLogRepository auditRepo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            // Creating a snapshot is an expensive VACUUM INTO + tar/gzip operation.
            // Without a role gate, any authenticated User could DoS the disk.
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            var info = await svc.CreateAsync();
            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
            await auditRepo.LogAsync("snapshot", info.FileName, "snapshot_created", "web", $"Snapshot created by user {actor}");
            return Results.Created($"/api/snapshots/{info.FileName}", info);
        });

        group.MapGet("/{fileName}/download", (string fileName, SnapshotService svc, SessionService session, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            // Snapshots contain the encrypted DB (incl. wrapped DEKs and key slots).
            // Even though contents are at rest under master-DEK encryption, exfil
            // enables offline brute-force against weak passwords. Restrict to admin.
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            try
            {
                var filePath = svc.GetSnapshotPath(fileName);
                return Results.File(filePath, "application/gzip", Path.GetFileName(fileName));
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new ErrorResponse($"Snapshot {fileName} not found"));
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new ErrorResponse("Invalid snapshot file name"));
            }
        });

        group.MapPost("/restore", async (RestoreSnapshotRequest req, SnapshotService svc, SessionService session,
            MaintenanceModeService maintenance, HttpContext ctx, ILogger<Program> logger, IAuditLogRepository auditRepo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);

            var unlockOk = await session.UnlockAsync(req.MasterPassword);
            if (!unlockOk)
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 403);

            // Audit the intent BEFORE we touch the DB. Restore replaces the
            // entire vault — including tbl_audit_log itself — so a post-action
            // log entry can be wiped out by the very operation it describes.
            // Logging up-front means at least the "started" record survives in
            // any later snapshot taken from the pre-restore state.
            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
            await auditRepo.LogAsync("snapshot", req.FileName, "snapshot_restore_started", "web",
                $"Standalone={req.StandaloneMode}, backupFirst={req.CreateBackupFirst}, by user {actor}");

            string? backupFileName = null;
            try
            {
                maintenance.Enter("Restoring from snapshot...");
                session.Lock();

                if (req.CreateBackupFirst)
                {
                    logger.LogInformation("Creating backup snapshot before restore");
                    var backup = await svc.CreateAsync();
                    backupFileName = backup.FileName;
                    logger.LogInformation("Backup created: {FileName}", backupFileName);
                }

                logger.LogInformation("Starting restore from {FileName}", req.FileName);
                await svc.RestoreAsync(req.FileName, standaloneMode: req.StandaloneMode);
                logger.LogInformation("Restore completed successfully");

                maintenance.Exit();
                return Results.Ok(new { success = true, backupFileName });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Restore failed for {FileName}", req.FileName);
                maintenance.Exit();
                return Results.Json(
                    new ErrorResponse(backupFileName != null
                        ? $"Restore failed. A backup was saved as {backupFileName}. Check server logs for details."
                        : "Restore failed. No backup was created. Check server logs for details."),
                    statusCode: 500);
            }
        });

        group.MapDelete("/{fileName}", async (string fileName, SnapshotService svc, SessionService session, HttpContext ctx, IAuditLogRepository auditRepo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            if (!svc.Delete(fileName))
                return Results.NotFound(new ErrorResponse($"Snapshot {fileName} not found"));

            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
            await auditRepo.LogAsync("snapshot", fileName, "snapshot_deleted", "web", $"Snapshot deleted by user {actor}");
            return Results.NoContent();
        });

        group.MapPost("/upload", async (
            HttpRequest request,
            SnapshotService svc,
            SessionService session,
            HttpContext ctx,
            ILogger<Program> logger,
            IAuditLogRepository auditRepo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            if (!session.IsUnlocked) return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);

            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart/form-data" });

            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "No file provided" });

            const long maxSize = 10L * 1024 * 1024 * 1024;
            if (file.Length > maxSize)
                return Results.StatusCode(413);

            try
            {
                await using var stream = file.OpenReadStream();
                var response = await svc.SaveUploadedAsync(stream);
                var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
                await auditRepo.LogAsync("snapshot", file.FileName, "snapshot_uploaded", "web",
                    $"Snapshot uploaded ({file.Length} bytes) by user {actor}");
                return Results.Ok(response);
            }
            catch (InvalidOperationException ex)
            {
                // SaveUploadedAsync throws InvalidOperationException when the manifest signature
                // claims a trusted originator but doesn't verify against their pubkey, or when
                // the signature blob is unreadable. Treat as 400 (bad upload), not 500.
                logger.LogWarning("Snapshot upload rejected: {Reason}", ex.Message);
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        })
        .DisableAntiforgery()
        .WithName("UploadSnapshot");

        group.MapPost("/restore-network", async (
            RestoreInitiationRequest req,
            SnapshotService snapshotSvc,
            SnapshotRestoreService restoreSvc,
            SessionService session,
            INodeIdentityRepository nodeRepo,
            ILamportClock clock,
            IEventLogRepository eventLogRepo,
            HttpContext ctx,
            ILogger<Program> logger) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            if (!session.IsUnlocked) return Results.Json(new ErrorResponse("Session is locked"), statusCode: 403);
            if (req.Mode != RestoreMode.NetworkWide) return Results.BadRequest("Use /restore for standalone");

            var filePath = snapshotSvc.FindSnapshotFileById(req.SnapshotFileId);
            if (filePath == null || !File.Exists(filePath))
                return Results.NotFound(new { error = "Snapshot file not found" });

            var identity = await nodeRepo.GetAsync();
            if (identity == null) return Results.BadRequest("Node identity not found");

            var evtId = Guid.NewGuid();
            var pendingDir = Path.Combine(snapshotSvc.SnapshotsDir, "restore-pending");
            Directory.CreateDirectory(pendingDir);
            var destPath = Path.Combine(pendingDir, $"{evtId}.bin");

            // CRITICAL: rebuild snapshot with secrets filtered out before distributing to peers.
            // Distributing the original file would leak the originator's identity (Ed25519 private key,
            // wrapped Master DEK, user records) to every peer that accepts the restore — and via
            // distributed seeding to every peer that downloads from them.
            await snapshotSvc.CreateFilteredVariantAsync(filePath, destPath);

            var fi = new FileInfo(destPath);
            string hash;
            await using (var stream = File.OpenRead(destPath))
            {
                var hashBytes = await SHA256.HashDataAsync(stream);
                hash = Convert.ToHexStringLower(hashBytes);
            }

            var payload = new RestoreNetworkEventPayload(
                SnapshotHash: hash,
                RestorePointTs: DateTime.UtcNow.ToString("O"),
                FileSizeBytes: fi.Length,
                ExpiresAt: DateTime.UtcNow.AddDays(30).ToString("O"),
                SourceUrl: Environment.GetEnvironmentVariable("BMB_API_URL") ?? "http://localhost:5300",
                FilterSecrets: true
            );

            var evt = new SyncEvent
            {
                EventId = evtId,
                NodeId = identity.NodeId,
                LamportTs = clock.Tick(),
                EventType = EventTypes.RestoreNetwork,
                Payload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
                ProtocolVersion = 1,
                CreatedAt = DateTime.UtcNow
            };

            var sigPayload = EventSignature.BuildPayload(evt);
            var masterDek = session.GetMasterDek();
            try
            {
                evt.Signature = NodeIdentityCrypto.SignWithIdentity(
                    identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                    identity.NodeId, masterDek, sigPayload);
            }
            finally
            {
                Array.Clear(masterDek);
            }

            await eventLogRepo.AppendAsync(evt);
            
            _ = Task.Run(async () => {
                try {
                    await restoreSvc.AcceptRestoreAsync(evt.EventId.ToString(), payload, evt);
                } catch (Exception ex) {
                    logger.LogError(ex, "Background restore failed");
                }
            });

            return Results.Accepted(value: new { eventId = evt.EventId.ToString() });
        })
        .WithName("InitiateNetworkRestore");

        group.MapGet("/restore/{eventId}/file", async (
            string eventId,
            SnapshotService snapshotSvc,
            IWhitelistRepository whitelistRepo,
            HttpContext ctx,
            INodeIdentityRepository nodeRepo,
            IEventLogRepository eventLogRepo,
            SyncTokenStore tokenStore,
            ILogger<Program> logger) =>
        {
            // Use the same Bearer-token auth as the rest of /api/sync (challenge-response).
            // The X-BMB-Node-Id header is trivially spoofable; tokens are issued by /api/sync/authenticate
            // only after successful Ed25519 signature verification, so they bind a real whitelisted peer.
            var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
            if (authHeader == null || !authHeader.StartsWith("Bearer ", StringComparison.Ordinal))
                return Results.Unauthorized();
            var token = authHeader["Bearer ".Length..];
            if (!tokenStore.TryValidateToken(token, out _))
                return Results.Unauthorized();

            if (!Guid.TryParse(eventId, out var eventIdGuid)) return Results.BadRequest();

            // SECURITY: only serve files that correspond to a real RESTORE_NETWORK event in our log
            // and only from the dedicated restore-pending directory. Searching the full snapshots dir
            // (or scanning by substring) would let any whitelisted peer enumerate / exfiltrate
            // unrelated local snapshots — including pre-restore backups containing originator identity.
            var pendingPath = Path.Combine(snapshotSvc.SnapshotsDir, "restore-pending", $"{eventIdGuid}.bin");
            if (!File.Exists(pendingPath))
                return Results.NotFound();

            // Verify the eventId actually corresponds to an event we have in the log. Without
            // this check, any leftover .bin in restore-pending could be served indefinitely.
            // (Event type is implicitly RESTORE_NETWORK because only restore code writes to
            // restore-pending/<eventId>.bin.)
            if (!await eventLogRepo.ExistsAsync(eventIdGuid))
                return Results.NotFound();

            // We expose SHA256(file) as a header for clients that want to verify before downloading.
            // The signature header was dropped in commit baa0773 because integrity is already
            // proven by payload.SnapshotHash inside the originator-signed RESTORE_NETWORK event.
            // Signing here would require an unlocked session for every file serve — fragile —
            // and adds nothing on top of the originator hash check. Hash-only is correct.
            byte[] hashBytes;
            await using (var hashStream = File.OpenRead(pendingPath))
                hashBytes = await SHA256.HashDataAsync(hashStream);
            ctx.Response.Headers["X-BMB-Snapshot-Sha256"] = Convert.ToHexStringLower(hashBytes);

            return Results.File(pendingPath, "application/octet-stream", enableRangeProcessing: true);
        })
        .WithName("ServeRestoreFile");

        group.MapGet("/restore/progress", (SnapshotRestoreService restoreSvc, HttpContext ctx) =>
        {
            // This endpoint is intentionally reachable from the locked Login screen, so it cannot
            // require InternalKey (Web proxy adds the header, but we want to support direct calls
            // from the splash UI). Return only the minimum information: bucket the step into a
            // coarse status and strip the eventId / error text so an unauth observer cannot
            // fingerprint the node or read filesystem paths leaked through exception messages.
            var p = restoreSvc.GetProgress();
            string status = p.CurrentStep switch
            {
                RestoreFlowStep.Idle => "idle",
                RestoreFlowStep.Completed => "completed",
                RestoreFlowStep.Failed => "failed",
                RestoreFlowStep.NeedsAdminDecision => "needs_admin",
                _ => "in_progress"
            };
            // For authenticated callers (Web proxy presents X-Internal-Key) we expose the full
            // progress payload so the admin's polling loop can render percentage + error.
            if (InternalKeyValidator.Validate(ctx))
                return Results.Ok(p);
            return Results.Ok(new
            {
                status,
                percentage = p.PercentageComplete,
                requiresMasterPassword = p.RequiresMasterPassword
            });
        })
        .WithName("GetRestoreProgress");

        group.MapPost("/restore/continue-without-backup", async (
            RestoreContinueWithoutBackupRequest req,
            SnapshotRestoreService restoreSvc,
            HttpContext ctx,
            ILogger<Program> logger) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            try
            {
                await restoreSvc.ContinueWithoutBackupAsync(req.EventId.ToString(), req.MasterPassword);
                return Results.Accepted();
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Unauthorized();
            }
            catch (Exception ex)
            {
                logger.LogWarning("Restore continue-without-backup failed for event {EventId}: {ExceptionType}", req.EventId, ex.GetType().Name);
                return Results.BadRequest(new ErrorResponse("Restore continuation failed. Check server logs for details."));
            }
        })
        .WithName("ContinueWithoutBackup");

        group.MapPost("/restore/cancel", async (
            [FromQuery] string eventId,
            SnapshotRestoreService restoreSvc,
            SessionService session,
            HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            if (ctx.Request.Headers["X-User-Role"].FirstOrDefault() != UserRoles.Superadmin)
                return Results.Json(new ErrorResponse("Forbidden — superadmin only"), statusCode: 403);
            // Note: do NOT require session.IsUnlocked here — sessions are intentionally locked
            // throughout maintenance/restoration mode (see SnapshotRestoreService.AcceptRestoreAsync),
            // so the cancel endpoint must remain reachable from the locked Login screen.
            if (!Guid.TryParse(eventId, out _))
                return Results.BadRequest(new ErrorResponse("eventId must be a valid GUID"));
            await restoreSvc.CancelAsync(eventId);
            return Results.Ok();
        })
        .WithName("CancelRestore");
    }
}
