using System.Globalization;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Api.Services.Sealed;

/// <summary>
/// The size limits of the relay inbox (docs/design/step-b-spec.md 6.2): it refuses with 507 once it holds more than <see cref="MaxInboxBytes"/>
/// of events and blobs (2 GiB, <c>BMB_SEALED_INBOX_MAX_MB</c>) or the free space under it drops below <see cref="MinFreeBytes"/> (1 GiB).
/// </summary>
public sealed class SealedRelayLimits(Func<long>? freeSpace = null)
{
    public const string MaxInboxEnvironmentVariable = "BMB_SEALED_INBOX_MAX_MB";

    public long MaxInboxBytes { get; set; } = FromEnvironment() ?? 2L * 1024 * 1024 * 1024;

    public long MinFreeBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>The free space under the boot store, or a negative value when it cannot be told.</summary>
    public long FreeBytes(string path)
    {
        if (freeSpace is not null) return freeSpace();
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return -1;
        }
    }

    private static long? FromEnvironment() =>
        long.TryParse(Environment.GetEnvironmentVariable(MaxInboxEnvironmentVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var mb) && mb > 0
            ? mb * 1024 * 1024
            : null;
}

/// <summary>
/// The sync routes a closed (or opening, or importing) sealed node answers from its boot store (docs/design/step-b-spec.md 6.1-6.5). It
/// never opens the sealed database: the identity and whitelist are the boot store's mirrors, the tokens the in-memory
/// <see cref="SyncTokenStore"/>, the events and blobs the relay inbox. Handed requests by the sealed-vault middleware (track 3).
/// </summary>
public sealed class SealedRelay : ISealedRelay
{
    private const long PushMaxRequestBytes = 32L * 1024 * 1024;
    private const long PullResponseByteTarget = 32L * 1024 * 1024;

    private static readonly HashSet<string> Routes = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET /api/sync/identity",
        "POST /api/sync/challenge",
        "POST /api/sync/authenticate",
        "GET /api/sync/events",
        "POST /api/sync/events",
        "POST /api/sync/report-position",
        "POST /api/sync/blobs/check",
        "POST /api/sync/blobs",
        "POST /api/sync/blobs/get",
        "GET /api/sync/ping",
    };

    private readonly IBootStore _boot;
    private readonly IBootMirrorStore _mirror;
    private readonly RelayInbox _inbox;
    private readonly SyncTokenStore _tokens;
    private readonly RelayWriteLock _writeLock;
    private readonly SealedRelayLimits _limits;
    // Serializes quota checks with their writes. The relay receipt lock intentionally permits concurrent requests.
    private readonly SemaphoreSlim _quotaLock = new(1, 1);
    private readonly IVaultGate? _gate;
    private readonly ILogger<SealedRelay>? _logger;

    public SealedRelay(IBootStore boot, IBootMirrorStore mirror, RelayInbox inbox, SyncTokenStore tokens, RelayWriteLock writeLock,
        SealedRelayLimits limits, IVaultGate? gate = null, ILogger<SealedRelay>? logger = null)
    {
        _boot = boot;
        _mirror = mirror;
        _inbox = inbox;
        _tokens = tokens;
        _writeLock = writeLock;
        _limits = limits;
        _gate = gate;
        _logger = logger;
    }

    public bool Handles(PathString path, string method) => Routes.Contains(Key(method, path));

    public async Task HandleAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var result = Key(context.Request.Method, context.Request.Path) switch
        {
            "GET /api/sync/identity" => await IdentityAsync(ct),
            "POST /api/sync/challenge" => await ChallengeAsync(context, ct),
            "POST /api/sync/authenticate" => await AuthenticateAsync(context, ct),
            "GET /api/sync/events" => await PullAsync(context, ct),
            "POST /api/sync/events" => await ReceiveEventsAsync(context, ct),
            "POST /api/sync/report-position" => await ReportPositionAsync(context, ct),
            "POST /api/sync/blobs/check" => await BlobsCheckAsync(context, ct),
            "POST /api/sync/blobs" => await BlobsStoreAsync(context, ct),
            "POST /api/sync/blobs/get" => await BlobsGetAsync(context, ct),
            "GET /api/sync/ping" => await PingAsync(context, ct),
            _ => SealedRefusal(context, StatusCodes.Status503ServiceUnavailable, "vault_sealed", null),
        };
        await result.ExecuteAsync(context);
    }

    private static string Key(string method, PathString path)
    {
        var text = path.Value ?? "";
        if (text.Length > 1 && text.EndsWith('/')) text = text.TrimEnd('/');
        return $"{method.ToUpperInvariant()} {text}";
    }

    // ---- answers --------------------------------------------------------------------------------------------------------------------

    private static IResult SealedRefusal(HttpContext context, int status, string code, int? retryAfterSeconds)
    {
        if (retryAfterSeconds is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        var message = code switch
        {
            "vault_sealed_busy" => "This node is being backed up for a few seconds: try again shortly.",
            "vault_sealed_range" => "This node is closed and cannot serve events from that position; they are available after it is unlocked.",
            _ => "This node's database is encrypted and closed: unlock it first.",
        };
        return Results.Json(new ErrorResponse(message, code), statusCode: status);
    }

    private static IResult Busy(HttpContext context) => SealedRefusal(context, StatusCodes.Status503ServiceUnavailable, "vault_sealed_busy", 5);

    // ---- identity, challenge, authenticate (6.1) -----------------------------------------------------------------------------------

    private async Task<IResult> IdentityAsync(CancellationToken ct)
    {
        if (await _mirror.GetIdentityAsync(ct) is not { } identity) return Results.Problem("Node is not initialized.", statusCode: 503);
        // A sealed node has applied the metadata activation (spec 2.5): it declares the sealed protocol.
        return Results.Ok(new SyncIdentityResponse(identity.NodeId, identity.DisplayName, Convert.ToBase64String(identity.Ed25519PublicKey),
            EventClasses.SealedProtocol, MetadataCapabilities.Advertised, BuildVersion.Current));
    }

    private async Task<IResult> ChallengeAsync(HttpContext context, CancellationToken ct)
    {
        var limiter = context.RequestServices.GetRequiredService<SyncEndpoints.SyncChallengeRateLimiter>();
        if (!limiter.TryAcquire(context.Connection.RemoteIpAddress?.ToString() ?? "unknown")) return Results.StatusCode(429);

        if (await _mirror.GetIdentityAsync(ct) is not { } identity) return Results.Problem("Node is not initialized.", statusCode: 503);
        return Results.Ok(new SyncChallengeResponse(_tokens.IssueChallenge(identity.NodeId), identity.NodeId));
    }

    private async Task<IResult> AuthenticateAsync(HttpContext context, CancellationToken ct)
    {
        SyncAuthRequest? req;
        try
        {
            req = await context.Request.ReadFromJsonAsync<SyncAuthRequest>(ct);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
        {
            return Results.BadRequest(new ErrorResponse("Invalid JSON"));
        }
        if (req is null || string.IsNullOrEmpty(req.ChallengeB64) || string.IsNullOrEmpty(req.SignatureB64)) return Results.BadRequest(new ErrorResponse("Invalid JSON"));

        if (!_tokens.ConsumeChallenge(req.ChallengeB64, out var serverNodeId))
        {
            _logger?.LogWarning("Relay auth 401 for {NodeId}: challenge not found or expired", req.NodeId);
            return Results.Unauthorized();
        }

        // Active rows of the mirror only (6.1): a suspended or unknown node cannot authenticate while this one is closed.
        if (await _mirror.GetActivePeerAsync(req.NodeId, ct) is not { } peer)
        {
            _logger?.LogWarning("Relay auth 401 for {NodeId}: not an active peer of the whitelist mirror", req.NodeId);
            return Results.Unauthorized();
        }

        byte[] challengeBytes;
        byte[] signature;
        try
        {
            challengeBytes = Convert.FromBase64String(req.ChallengeB64);
            signature = Convert.FromBase64String(req.SignatureB64);
        }
        catch (FormatException)
        {
            return Results.BadRequest("Invalid base64 format.");
        }

        // The same domain-separated payload as the open node verifies, bound to OUR node id (SyncEndpoints.cs, M6); an unbound V1 signature is never honoured.
        var payload = "BMB-CHALLENGE-V2\0"u8.ToArray().Concat(serverNodeId.ToByteArray()).Concat(challengeBytes).ToArray();
        if (!Ed25519Signer.Verify(peer.PublicKey, payload, signature))
        {
            _logger?.LogWarning("Relay auth 401 for {NodeId}: Ed25519 signature verify failed", req.NodeId);
            return Results.Unauthorized();
        }

        // No protocol or capability observation is recorded: that table is in the sealed database (6.1).
        if (!SealedPolicy.IsCompatiblePeer(req.ProtocolVersion, req.Capabilities))
        {
            _logger?.LogWarning("Relay auth 426 for {NodeId}: protocol {Version} is not accepted by this node", req.NodeId, req.ProtocolVersion?.ToString() ?? "(none)");
            return Results.Problem("This network encrypts its metadata; update this node to version 2.6 or newer.",
                statusCode: StatusCodes.Status426UpgradeRequired);
        }

        return Results.Ok(new SyncAuthResponse(_tokens.IssueToken(req.NodeId, req.ProtocolVersion!.Value, req.Capabilities)));
    }

    /// <summary>The rule of an activated node (<c>SyncProtocolPolicy.IsCompatiblePeer</c> after the activation); a sealed node always is one.</summary>
    private static class SealedPolicy
    {
        public static bool IsCompatiblePeer(int? declaredProtocol, IReadOnlyCollection<string>? capabilities) =>
            declaredProtocol >= EventClasses.SealedProtocol
            || (declaredProtocol == SyncProtocolVersion.Current && MetadataCapabilities.HasEnvelopeV1(capabilities));
    }

    /// <summary>The peer behind the bearer token, or null: the token must be valid and its node an active row of the whitelist mirror.</summary>
    private async Task<Guid?> AuthenticatePeerAsync(HttpContext context, CancellationToken ct)
    {
        var header = context.Request.Headers.Authorization.FirstOrDefault();
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        if (!_tokens.TryValidateToken(header["Bearer ".Length..], out var nodeId)) return null;
        return await _mirror.GetActivePeerAsync(nodeId, ct) is null ? null : nodeId;
    }

    private static bool IsInvisible(HttpContext context) =>
        context.RequestServices.GetService<InvisibleModeService>()?.IsInvisible == true;

    // ---- GET events (6.1, 6.3) -------------------------------------------------------------------------------------------------------

    private async Task<IResult> PullAsync(HttpContext context, CancellationToken ct)
    {
        if (await AuthenticatePeerAsync(context, ct) is null) return Results.Unauthorized();
        if (IsInvisible(context)) return Results.StatusCode(503);

        long.TryParse(context.Request.Query["afterSequence"].FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var after);
        if (!int.TryParse(context.Request.Query["limit"].FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)) limit = 1000;
        limit = Math.Clamp(limit, 1, 1000);

        // While importing the inbox is never served (rows that arrive then are imported and numbered by the sealed log), and no position
        // passes an event the open node appends meanwhile (6.3).
        if (_gate is { State: VaultState.Importing }) return SealedRefusal(context, 503, "vault_sealed_range", null);
        if (!await CanServeAfterAsync(after, ct)) return SealedRefusal(context, 503, "vault_sealed_range", null);

        var rows = await _inbox.ReadAfterAsync(after, limit, ct);
        var events = new List<SyncEvent>(rows.Count);
        long size = 0;
        foreach (var row in rows)
        {
            var eventSize = row.Event.Payload?.Length ?? 0;
            if (events.Count > 0 && size + eventSize > PullResponseByteTarget) break;
            events.Add(row.Event);
            size += eventSize;
        }
        return Results.Ok(events);
    }

    /// <summary>After a clean close, serve from <c>sealed_head</c>; after a crash, from <c>seq_floor - 1</c> (6.3).</summary>
    private async Task<bool> CanServeAfterAsync(long after, CancellationToken ct)
    {
        var clean = await _boot.GetMetaAsync(BootMetaKeys.CleanClose, ct) == "1";
        if (clean)
        {
            // No head recorded at all (a boot store written by the migration, not by a close): the floor is the line.
            var head = long.TryParse(await _boot.GetMetaAsync(BootMetaKeys.SealedHead, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : (long?)null;
            if (head is { } sealedHead) return after >= sealedHead;
        }
        var floor = long.TryParse(await _boot.GetMetaAsync(BootMetaKeys.SeqFloor, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var f) ? f : 1;
        return after >= floor - 1;
    }

    // ---- POST events: receipt (6.2) ---------------------------------------------------------------------------------------------------

    private enum Receipt { Stored, Duplicate, Refused, Deferred, InsufficientStorage }

    private async Task<IResult> ReceiveEventsAsync(HttpContext context, CancellationToken ct)
    {
        if (await AuthenticatePeerAsync(context, ct) is not { } pusher) return Results.Unauthorized();
        if (IsInvisible(context)) return Results.StatusCode(503);

        using var lease = await _writeLock.TryEnterReceiptAsync(ct);
        if (lease is null) return Busy(context);
        // The switch to Open has happened since the middleware looked: the normal endpoint takes the push, the pusher retries (nothing is stored).
        if (_gate is { IsSealed: true, State: VaultState.Open or VaultState.Closing or VaultState.Plain }) return SealedRefusal(context, 503, "vault_sealed_busy", 1);

        var maxBody = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (maxBody is { IsReadOnly: false }) maxBody.MaxRequestBodySize = PushMaxRequestBytes;

        SyncEvent[] events;
        try
        {
            events = await context.Request.ReadFromJsonAsync<SyncEvent[]>(ct) ?? [];
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        {
            return Results.StatusCode(413);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.BadRequest(new ErrorResponse("Invalid JSON"));
        }
        if (events.Length > 2000) return Results.BadRequest(new ErrorResponse("Batch too large (max 2000 events)"));

        int stored = 0, skipped = 0;
        long? lastStored = null;
        var prefixEnded = false;
        await _quotaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var evt in events)
            {
                Receipt receipt;
                try
                {
                    receipt = await ReceiveOneAsync(evt, pusher, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The boot store could not take it (locked, full disk): the pusher retries, its cursor stops here.
                    _logger?.LogWarning(ex, "Relay could not store event {EventId}.", evt.EventId);
                    receipt = Receipt.Deferred;
                }

                if (receipt == Receipt.InsufficientStorage) return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
                if (receipt is Receipt.Stored or Receipt.Duplicate)
                {
                    stored++;
                    if (!prefixEnded) lastStored = evt.SequenceNum;
                }
                else
                {
                    // A refusal, permanent or deferrable, is answered as the open node answers a failed apply: skipped, and the stored prefix ends.
                    skipped++;
                    prefixEnded = true;
                    if (receipt == Receipt.Deferred) break;
                }
            }
            return Results.Ok(new SyncApplyResult(stored, skipped, lastStored));
        }
        finally
        {
            _quotaLock.Release();
        }
    }

    private async Task<Receipt> ReceiveOneAsync(SyncEvent evt, Guid pusher, CancellationToken ct)
    {
        // 1. protocol and the blind-node rules
        if (!SyncProtocolVersion.CanApply(evt.ProtocolVersion))
        {
            _logger?.LogWarning("Relay refused event {EventId}: unknown protocol version {Version}.", evt.EventId, evt.ProtocolVersion);
            return Receipt.Refused;
        }
        if (EventInvariants.Violation(evt) is not null)
        {
            _logger?.LogWarning("Relay refused event {EventId} from {NodeId}: it breaks a blind-node rule.", evt.EventId, evt.NodeId);
            return Receipt.Refused;
        }

        // 2. already in the inbox: counted as received (a row of the sealed log cannot be seen while closed; the import drops those)
        if (await _inbox.ContainsAsync(evt.EventId, ct)) return Receipt.Duplicate;

        // 3. the originator is an active row of the mirror; an unknown one may be a node added while this one was closed (deferred)
        if (await _mirror.GetActivePeerAsync(evt.NodeId, ct) is not { } originator)
        {
            _logger?.LogInformation("Relay deferred event {EventId}: originator {NodeId} is not an active peer of the whitelist mirror.", evt.EventId, evt.NodeId);
            return Receipt.Deferred;
        }

        // 4. signature and the wire shape of the envelope
        if (!Ed25519Signer.Verify(originator.PublicKey, EventSignature.BuildPayload(evt), evt.Signature))
        {
            _logger?.LogWarning("Relay refused event {EventId} from {NodeId}: invalid signature.", evt.EventId, evt.NodeId);
            return Receipt.Refused;
        }
        if (BeeMemoryBank.Sync.Metadata.SealedEnvelope.WireViolation(evt) is not null)
        {
            _logger?.LogWarning("Relay refused event {EventId} from {NodeId}: malformed envelope.", evt.EventId, evt.NodeId);
            return Receipt.Refused;
        }

        // The boot store is a locked-disk boundary: every protocol has a finite event-type allowlist.
        // Known protocol-1..3 content reaches the deferred rule below; protocol 4 accepts sealed content and controls only.
        if (evt.EventType != EventClasses.Sealed
            && !EventClasses.Control.Contains(evt.EventType)
            && (evt.ProtocolVersion >= EventClasses.SealedProtocol || !EventClasses.IsSealable(evt.EventType)))
        {
            _logger?.LogWarning("Relay refused event {EventId} from {NodeId}: unknown event type.", evt.EventId, evt.NodeId);
            return Receipt.Refused;
        }

        // 5. clear content never enters the boot store: deferred, retried after the unlock
        if (EventClasses.IsClearContent(evt.ProtocolVersion, evt.EventType))
        {
            _logger?.LogInformation("Relay deferred event {EventId} ({EventType}): clear content is not accepted while the node is locked.", evt.EventId, evt.EventType);
            return Receipt.Deferred;
        }

        if (!await HasCapacityForAsync(Encoding.UTF8.GetByteCount(evt.Payload), ct).ConfigureAwait(false)) return Receipt.InsufficientStorage;

        // 6. a revoke or an update from an active superadmin suspends its target in the mirror (6.4), then the event is stored
        if (originator.IsSuperadmin && evt.EventType is EventTypes.WhitelistRevoke or EventTypes.WhitelistUpdate
            && TargetOf(evt) is { } target)
        {
            await _mirror.SuspendAsync(target, ct);
            _logger?.LogInformation("Relay suspended node {Target} in the whitelist mirror after event {EventId}.", target, evt.EventId);
        }

        // 7. stored with the pusher and its sequence number
        await _inbox.AppendAsync(evt, pusher, evt.SequenceNum, ct);
        return Receipt.Stored;
    }

    private static Guid? TargetOf(SyncEvent evt)
    {
        try
        {
            using var document = JsonDocument.Parse(evt.Payload);
            return document.RootElement.TryGetProperty("node_id", out var id) && id.TryGetGuid(out var guid) ? guid : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> HasCapacityForAsync(long incomingBytes, CancellationToken ct)
    {
        var summary = await _inbox.GetSummaryAsync(ct);
        if (incomingBytes < 0 || summary.EventBytes > _limits.MaxInboxBytes || summary.BlobBytes > _limits.MaxInboxBytes - summary.EventBytes)
            return false;
        if (incomingBytes > _limits.MaxInboxBytes - summary.EventBytes - summary.BlobBytes) return false;
        var freeBytes = _limits.FreeBytes(_boot.FilePath);
        return freeBytes >= _limits.MinFreeBytes;
    }

    // ---- report-position (6.1) -----------------------------------------------------------------------------------------------------

    private async Task<IResult> ReportPositionAsync(HttpContext context, CancellationToken ct)
    {
        if (await AuthenticatePeerAsync(context, ct) is not { } peer) return Results.Unauthorized();
        if (IsInvisible(context)) return Results.StatusCode(503);

        if (!long.TryParse(context.Request.Query["sequence"].FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) || sequence < 0)
            return Results.BadRequest(new ErrorResponse("sequence is required"));

        using var lease = await _writeLock.TryEnterReceiptAsync(ct);
        if (lease is null) return Busy(context);

        await _inbox.RecordReportAsync(peer, sequence, ct);
        return Results.Ok();
    }

    // ---- blobs (6.5) ----------------------------------------------------------------------------------------------------------------

    private static bool ValidateHashList(List<string>? hashes, out IResult problem)
    {
        problem = Results.Empty;
        if (hashes is null) { problem = Results.BadRequest(new ErrorResponse("Missing hashes")); return false; }
        if (hashes.Count > 2000) { problem = Results.BadRequest(new ErrorResponse("Too many hashes (max 2000)")); return false; }
        if (hashes.Any(h => !BlobReferences.IsWellFormedHash(h)))
        {
            problem = Results.BadRequest(new ErrorResponse("Malformed blob hash"));
            return false;
        }
        return true;
    }

    private static async Task<SyncBlobHashList?> ReadHashListAsync(HttpContext context, CancellationToken ct)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<SyncBlobHashList>(ct);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
        {
            return null;
        }
    }

    private async Task<IResult> BlobsCheckAsync(HttpContext context, CancellationToken ct)
    {
        if (await AuthenticatePeerAsync(context, ct) is null) return Results.Unauthorized();
        if (IsInvisible(context)) return Results.StatusCode(503);
        var request = await ReadHashListAsync(context, ct);
        if (!ValidateHashList(request?.Hashes, out var problem)) return problem;

        // The sealed tbl_blob cannot be read: every hash the inbox lacks is "missing" (a pusher then ships blobs it would not have to).
        var have = await _inbox.ExistingBlobHashesAsync(request!.Hashes, ct);
        return Results.Ok(new SyncBlobMissing(request.Hashes.Where(h => !have.Contains(h)).Distinct().ToList()));
    }

    private async Task<IResult> BlobsStoreAsync(HttpContext context, CancellationToken ct)
    {
        if (await AuthenticatePeerAsync(context, ct) is not { } peer) return Results.Unauthorized();
        if (IsInvisible(context)) return Results.StatusCode(503);

        using var lease = await _writeLock.TryEnterReceiptAsync(ct);
        if (lease is null) return Busy(context);
        if (_gate is { IsSealed: true, State: VaultState.Open or VaultState.Closing or VaultState.Plain }) return SealedRefusal(context, 503, "vault_sealed_busy", 1);

        var maxBody = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (maxBody is { IsReadOnly: false }) maxBody.MaxRequestBodySize = PushMaxRequestBytes;

        SyncBlobBatch? batch;
        try
        {
            batch = await context.Request.ReadFromJsonAsync<SyncBlobBatch>(ct);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        {
            return Results.StatusCode(413);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.BadRequest(new ErrorResponse("Invalid JSON"));
        }
        if (batch?.Blobs is null) return Results.BadRequest(new ErrorResponse("Missing blobs"));
        if (batch.Blobs.Count > 2000) return Results.BadRequest(new ErrorResponse("Batch too large (max 2000 blobs)"));
        int stored = 0, rejected = 0;
        await _quotaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var blob in batch.Blobs)
            {
                byte[] data;
                try { data = Convert.FromBase64String(blob.Data ?? ""); }
                catch (FormatException) { rejected++; continue; }
                if (data.Length == 0 || !BlobReferences.IsWellFormedHash(blob.Hash)) { rejected++; continue; }

                // Bytes that do not hash to the claim are rejected, not stored under their real hash (SyncEndpoints.cs).
                if (!BlobHash.Matches(blob.Hash, data))
                {
                    _logger?.LogWarning("Peer {NodeId} uploaded bytes that do not hash to claimed {Claimed}; rejected", peer, blob.Hash);
                    rejected++;
                    continue;
                }
                if (!(await _inbox.ExistingBlobHashesAsync([blob.Hash], ct).ConfigureAwait(false)).Contains(blob.Hash)
                    && !await HasCapacityForAsync(data.LongLength, ct).ConfigureAwait(false))
                    return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
                await _inbox.StoreBlobAsync(blob.Hash, data, ct);
                stored++;
            }
            return Results.Ok(new SyncBlobStoreResult(stored, rejected));
        }
        finally
        {
            _quotaLock.Release();
        }
    }

    private async Task<IResult> BlobsGetAsync(HttpContext context, CancellationToken ct)
    {
        if (await AuthenticatePeerAsync(context, ct) is null) return Results.Unauthorized();
        if (IsInvisible(context)) return Results.StatusCode(503);
        var request = await ReadHashListAsync(context, ct);
        if (!ValidateHashList(request?.Hashes, out var problem)) return problem;

        var found = new List<SyncBlob>();
        long size = 0;
        foreach (var hash in request!.Hashes.Distinct(StringComparer.Ordinal))
        {
            var data = await _inbox.GetBlobAsync(hash, ct);
            if (data is null) continue;
            if (found.Count > 0 && size + data.Length > PullResponseByteTarget) break;
            found.Add(new SyncBlob(hash, Convert.ToBase64String(data)));
            size += data.Length;
        }

        // A hash the inbox does not hold is in the sealed tbl_blob, which cannot be read now: deferrable on the caller's side.
        if (found.Count == 0 && request.Hashes.Count > 0) return SealedRefusal(context, 503, "vault_sealed", null);
        return Results.Ok(new SyncBlobBatch(found));
    }

    // ---- ping -----------------------------------------------------------------------------------------------------------------------

    private async Task<IResult> PingAsync(HttpContext context, CancellationToken ct)
    {
        // The route is behind the internal key today (RequireInternalKey); it stays so.
        if (!InternalKeyValidator.Validate(context)) return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
        if (IsInvisible(context)) return Results.StatusCode(503);
        long.TryParse(context.Request.Query["afterSequence"].FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var after);
        var summary = await _inbox.GetSummaryAsync(ct);
        return summary.MaxRelaySeq > after ? Results.Ok(new { count = summary.Events }) : Results.NoContent();
    }
}
