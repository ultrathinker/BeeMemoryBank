using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

public class NeedsAdminDecisionException : Exception
{
    public string EventId { get; }
    public NeedsAdminDecisionException(string eventId, string message) : base(message)
    {
        EventId = eventId;
    }
}

public class SnapshotRestoreService : IRestoreInitiator
{
    private readonly SnapshotService _snapshotService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SessionService _sessionService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DbConnectionFactory _connFactory;
    private readonly MaintenanceModeService _maintenance;
    private readonly ILogger<SnapshotRestoreService> _logger;
    private readonly string _dataPath;

    private volatile RestoreFlowStep _currentStep = RestoreFlowStep.Idle;
    private volatile int _percentage = 0;
    private volatile string? _statusMessage;
    private volatile string? _errorMessage;
    private string? _currentEventId;

    // Single-flight: only one restore-flow instance can run on this node at a time.
    // EventApplier auto-accept and the Web/CLI initiate paths both eventually call
    // AcceptRestoreAsync via Task.Run, so concurrent attempts are real (e.g. two pulls
    // delivering the same event in quick succession, or two events from different peers).
    private readonly SemaphoreSlim _executeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public SnapshotRestoreService(
        SnapshotService snapshotService,
        IServiceScopeFactory scopeFactory,
        SessionService sessionService,
        IHttpClientFactory httpClientFactory,
        DbConnectionFactory connFactory,
        MaintenanceModeService maintenance,
        ILogger<SnapshotRestoreService> logger,
        string dataPath)
    {
        _snapshotService = snapshotService;
        _scopeFactory = scopeFactory;
        _sessionService = sessionService;
        _httpClientFactory = httpClientFactory;
        _connFactory = connFactory;
        _maintenance = maintenance;
        _logger = logger;
        _dataPath = dataPath;
    }

    public RestoreProgressResponse GetProgress()
    {
        return new RestoreProgressResponse(
            _currentEventId != null ? Guid.Parse(_currentEventId) : null,
            _currentStep,
            _percentage,
            _statusMessage,
            _errorMessage,
            _currentStep == RestoreFlowStep.NeedsAdminDecision && (_errorMessage?.Contains("backup") == true)
        );
    }

    public async Task AcceptRestoreAsync(string eventId, RestoreNetworkEventPayload payload, SyncEvent restoreEvent)
    {
        if (!await _executeLock.WaitAsync(TimeSpan.Zero))
        {
            _logger.LogWarning("Refusing concurrent restore for event {EventId} — another restore is in progress (current={Current})", eventId, _currentEventId);
            throw new InvalidOperationException("Another restore is already in progress on this node.");
        }

        using var scope = _scopeFactory.CreateScope();
        var stateRepo = scope.ServiceProvider.GetRequiredService<IRestoreEventStateRepository>();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var whitelistRepo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        // Block normal API traffic for the duration of the network-wide restore. The standalone
        // /restore endpoint already does this; without it here, clients could read mid-replace
        // tbl_article state or attempt writes that would race the destructive import.
        // Distributed-seeding endpoints (/api/snapshots/restore/{guid}/file + /api/sync/challenge
        // and /api/sync/authenticate which the seeder pulls) are explicitly allow-listed in
        // MaintenanceMiddleware so peers can still pull our snapshot from us if WE are the
        // originator hosting the file.
        _maintenance.Enter("Network-wide snapshot restore in progress…");
        try
        {
            if (!string.IsNullOrEmpty(payload.ExpiresAt)
                && DateTime.TryParse(payload.ExpiresAt, out var expiresAt)
                && DateTime.UtcNow > expiresAt)
            {
                await stateRepo.UpdateStateAsync(eventId, RestoreEventState.Failed, "Snapshot expired");
                throw new InvalidOperationException($"Restore event {eventId} has expired at {payload.ExpiresAt}");
            }

            using var replayConn = _connFactory.CreateConnection();
            var shieldThreshold = await replayConn.QuerySingleOrDefaultAsync<long?>(
                "SELECT MAX(ignore_events_before_lamport_ts) FROM tbl_restore_replay_shield WHERE peer_node_id = @NodeId",
                new { NodeId = restoreEvent.NodeId.ToString() });
            if (shieldThreshold.HasValue && restoreEvent.LamportTs <= shieldThreshold.Value)
            {
                _logger.LogWarning("Rejecting replay: event {EventId} lamport_ts {Ts} <= shield {Shield}", eventId, restoreEvent.LamportTs, shieldThreshold.Value);
                await stateRepo.UpdateStateAsync(eventId, RestoreEventState.Rejected, "Replay detected (lamport_ts <= shield)");
                throw new InvalidOperationException($"Restore event {eventId} is a replay (lamport_ts {restoreEvent.LamportTs} <= shield {shieldThreshold.Value})");
            }

            _currentEventId = eventId;
            _currentStep = RestoreFlowStep.SessionsClosing;
            _percentage = 5;
            _statusMessage = "Closing sessions...";
            _errorMessage = null;

            await stateRepo.UpdateStateAsync(eventId, RestoreEventState.Downloading);

            // NOTE: do NOT lock the session here. Both the pre-restore backup (CreateAsync with
            // encryptDb=true requires an unlocked session) and the snapshot apply
            // (ApplyNetworkRestoreAsync → DecryptDbIfNeededAsync) require an unlocked session
            // to access the master DEK. Maintenance mode (above) already blocks normal API
            // traffic for the duration of the restore. We lock the session AFTER the apply
            // completes — at that point the in-memory DEK is stale (the restored DB has new
            // key slots), so users must re-unlock with the master password against the new DB.

            _currentStep = RestoreFlowStep.PreRestoreBackup;
            _percentage = 10;
            _statusMessage = "Creating pre-restore backup...";

            try
            {
                await _snapshotService.CreateAsync(filterSecrets: false, sign: false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("disk space"))
            {
                await stateRepo.UpdateStateAsync(eventId, RestoreEventState.Failed, "Insufficient disk space for backup");
                _currentStep = RestoreFlowStep.NeedsAdminDecision;
                _errorMessage = "Insufficient disk space for backup";
                throw new NeedsAdminDecisionException(eventId, "Insufficient disk space for backup");
            }

            try
            {
                await ExecuteDownloadAndApplyAsync(eventId, payload, restoreEvent, stateRepo, nodeRepo, whitelistRepo);

                // Apply succeeded — restored DB has different key slots than the in-memory DEK
                // was derived against. Force re-unlock so the user re-authenticates against the
                // newly restored slots.
                _sessionService.Lock();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Insufficient disk space"))
            {
                // Disk-space failures during apply (extract + ATTACH need ~2x snapshot size)
                // get the same NeedsAdminDecision treatment as backup-time disk failures so the
                // admin sees the same UI flow rather than a generic "Failed".
                await stateRepo.UpdateStateAsync(eventId, RestoreEventState.Failed, ex.Message);
                _currentStep = RestoreFlowStep.NeedsAdminDecision;
                _errorMessage = ex.Message;
                throw new NeedsAdminDecisionException(eventId, ex.Message);
            }
        }
        catch (Exception ex)
        {
            if (ex is NeedsAdminDecisionException) throw;

            _currentStep = RestoreFlowStep.Failed;
            _errorMessage = ex.Message;
            _logger.LogError(ex, "Restore failed for event {EventId}", eventId);
            await stateRepo.UpdateStateAsync(eventId, RestoreEventState.Failed, ex.Message);
            throw;
        }
        finally
        {
            _maintenance.Exit();
            _executeLock.Release();
        }
    }

    /// <summary>
    /// Sweeps tbl_restore_event_state for rows stuck in Pending/Downloading/Applying. If the
    /// originator's whitelist entry has auto_accept_restore = true, re-invoke AcceptRestoreAsync.
    /// Mirrors DekRotationService.RetryPendingAutoAcceptsAsync — recovers from fire-and-forget
    /// Task.Run failures (network blip mid-download, locked session at apply time, process crash
    /// before sweep). Safe to call repeatedly: AcceptRestoreAsync uses _executeLock + state-machine
    /// checks for idempotency.
    /// </summary>
    public async Task RetryPendingRestoresAsync()
    {
        if (!_sessionService.IsUnlocked) return;

        using var scope = _scopeFactory.CreateScope();
        var stateRepo = scope.ServiceProvider.GetRequiredService<IRestoreEventStateRepository>();
        var eventRepo = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var whitelistRepo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();

        // Pending = arrived while session was locked, never started.
        // Downloading/Applying = previous attempt died (Task.Run threw, or process crashed before
        // the startup sweep in Program.cs flipped them to Failed). Both are safe to retry —
        // partial download/apply is overwritten, state machine re-asserts.
        var pending = await stateRepo.GetByStateAsync(RestoreEventState.Pending);
        var downloading = await stateRepo.GetByStateAsync(RestoreEventState.Downloading);
        var applying = await stateRepo.GetByStateAsync(RestoreEventState.Applying);
        var stuck = pending.Concat(downloading).Concat(applying).ToList();
        if (stuck.Count == 0) return;

        var localIdentity = await nodeRepo.GetAsync();
        var localNodeId = localIdentity?.NodeId.ToString();

        foreach (var row in stuck)
        {
            try
            {
                var evt = await eventRepo.GetByIdAsync(row.EventId);
                if (evt == null) continue;
                if (evt.NodeId.ToString().Equals(localNodeId, StringComparison.OrdinalIgnoreCase))
                    continue; // self-originated, no auto-accept loop

                var autoAccept = await whitelistRepo.GetAutoAcceptRestoreAsync(evt.NodeId.ToString());
                if (!autoAccept) continue; // manual-approval restore — admin will decide

                var payload = JsonSerializer.Deserialize<RestoreNetworkEventPayload>(evt.Payload, JsonOpts);
                if (payload == null) continue;

                _logger.LogInformation(
                    "Retrying auto-accept for previously-stuck restore {EventId} from {NodeId} (state={State})",
                    row.EventId, evt.NodeId, row.State);
                await AcceptRestoreAsync(row.EventId, payload, evt);
            }
            catch (InvalidOperationException)
            {
                _logger.LogDebug("Skipping {EventId}: another worker holds the lock", row.EventId);
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RetryPendingRestoresAsync failed for event {EventId}", row.EventId);
            }
        }
    }

    public async Task ContinueWithoutBackupAsync(string eventId, string masterPassword)
    {
        using var outerScope = _scopeFactory.CreateScope();
        var stateRepo = outerScope.ServiceProvider.GetRequiredService<IRestoreEventStateRepository>();

        var stateRow = await stateRepo.GetAsync(eventId);
        if (stateRow == null || stateRow.State != RestoreEventState.Failed || stateRow.ErrorMessage?.Contains("backup") != true)
            throw new InvalidOperationException("Event is not in a failed state due to backup.");

        if (!await _executeLock.WaitAsync(TimeSpan.Zero))
            throw new InvalidOperationException("Another restore is already in progress on this node.");

        // Re-enter maintenance mode for the apply phase. Mirrors AcceptRestoreAsync — the apply
        // here goes through the same destructive ApplyNetworkRestoreAsync path.
        _maintenance.Enter("Network-wide snapshot restore in progress (continue without backup)…");
        try
        {
            if (!await _sessionService.UnlockAsync(masterPassword))
                throw new UnauthorizedAccessException("Invalid master password.");

            // Stay unlocked through ExecuteDownloadAndApplyAsync — the apply path needs the
            // master DEK to decrypt the encrypted snapshot. Lock after apply succeeds (below).

        using var conn = _connFactory.CreateConnection();
        var rawEvent = await conn.QuerySingleOrDefaultAsync<SyncEvent>(
            "SELECT event_id AS EventId, node_id AS NodeId, lamport_ts AS LamportTs, event_type AS EventType, payload AS Payload, signature_b64 AS SignatureB64 FROM tbl_event WHERE event_id = @EventId",
            new { EventId = eventId });

        if (rawEvent == null)
            throw new InvalidOperationException("Event not found in database.");

        var payload = JsonSerializer.Deserialize<RestoreNetworkEventPayload>(rawEvent.Payload, JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize event payload.");

        if (!string.IsNullOrEmpty(payload.ExpiresAt)
            && DateTime.TryParse(payload.ExpiresAt, out var expiresAt)
            && DateTime.UtcNow > expiresAt)
            throw new InvalidOperationException($"Restore event {eventId} has expired at {payload.ExpiresAt}");

        var shieldThreshold = await conn.QuerySingleOrDefaultAsync<long?>(
            "SELECT MAX(ignore_events_before_lamport_ts) FROM tbl_restore_replay_shield WHERE peer_node_id = @NodeId",
            new { NodeId = rawEvent.NodeId.ToString() });
        if (shieldThreshold.HasValue && rawEvent.LamportTs <= shieldThreshold.Value)
            throw new InvalidOperationException($"Restore event {eventId} is a replay");

        _currentEventId = eventId;
        _errorMessage = null;

        var nodeRepo = outerScope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var whitelistRepo = outerScope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        try
        {
            await ExecuteDownloadAndApplyAsync(eventId, payload, rawEvent, stateRepo, nodeRepo, whitelistRepo);

            // Apply succeeded — restored DB has different key slots than the in-memory DEK.
            // Force re-unlock against the new slots.
            _sessionService.Lock();
        }
        catch (Exception ex)
        {
            if (ex is NeedsAdminDecisionException) throw;

            _currentStep = RestoreFlowStep.Failed;
            _errorMessage = ex.Message;
            _logger.LogWarning("Restore failed during ContinueWithoutBackup for event {EventId}: {ExceptionType}", eventId, ex.GetType().Name);
            await stateRepo.UpdateStateAsync(eventId, RestoreEventState.Failed, ex.Message);
            throw;
        }
        }
        finally
        {
            _maintenance.Exit();
            _executeLock.Release();
        }
    }

    public async Task CancelAsync(string eventId)
    {
        if (!await _executeLock.WaitAsync(TimeSpan.Zero))
            throw new InvalidOperationException("Cannot cancel — restore flow is actively executing. Wait for current step to complete.");

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var stateRepo = scope.ServiceProvider.GetRequiredService<IRestoreEventStateRepository>();
            await stateRepo.UpdateStateAsync(eventId, RestoreEventState.Cancelled);

            var pendingDir = Path.Combine(_dataPath, "snapshots", "restore-pending");
            var filePath = Path.Combine(pendingDir, $"{eventId}.bin");
            if (File.Exists(filePath))
            {
                try { File.Delete(filePath); } catch { /* ignore */ }
            }

            if (_currentEventId == eventId)
            {
                _currentStep = RestoreFlowStep.Idle;
                _currentEventId = null;
                _percentage = 0;
                _statusMessage = "Cancelled";
            }
        }
        finally
        {
            _executeLock.Release();
        }
    }

    private async Task ExecuteDownloadAndApplyAsync(
        string eventId, RestoreNetworkEventPayload payload, SyncEvent restoreEvent,
        IRestoreEventStateRepository stateRepo,
        INodeIdentityRepository nodeRepo,
        IWhitelistRepository whitelistRepo)
    {
        _currentStep = RestoreFlowStep.DownloadingSnapshot;
        _percentage = 30;
        _statusMessage = "Downloading snapshot...";
        await stateRepo.UpdateStateAsync(eventId, RestoreEventState.Downloading);

        var pendingDir = Path.Combine(_dataPath, "snapshots", "restore-pending");
        Directory.CreateDirectory(pendingDir);
        var filePath = Path.Combine(pendingDir, $"{eventId}.bin");

        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(pendingDir))!);
            if (driveInfo.AvailableFreeSpace < payload.FileSizeBytes)
            {
                await stateRepo.UpdateStateAsync(eventId, RestoreEventState.Failed, "Insufficient disk space for snapshot download");
                _currentStep = RestoreFlowStep.NeedsAdminDecision;
                _errorMessage = "Insufficient disk space for snapshot download";
                throw new NeedsAdminDecisionException(eventId, "Insufficient disk space for snapshot download");
            }
        }
        catch (ArgumentException) { } // DriveInfo can fail on unusual paths
        catch (DriveNotFoundException) { }

        var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException("Local node is not initialized.");

        // Self-originator path: when this node initiated the network restore, the
        // /restore-network endpoint already wrote the filtered snapshot to filePath before
        // triggering the background task. We don't need to download from ourselves, and the
        // whitelist lookup would fail because nodes don't store self-records.
        bool selfOriginator = identity.NodeId == restoreEvent.NodeId;
        if (selfOriginator)
        {
            if (!File.Exists(filePath))
                throw new InvalidOperationException(
                    "Self-restore: filtered snapshot missing at restore-pending. Was /restore-network called?");
            _logger.LogInformation("Self-originated restore: using local filtered file at {Path}, skipping download", filePath);
        }

        // SECURITY: never trust SourceUrl from the event payload as-is — the originator could
        // have signed a payload pointing to an attacker-controlled host (SSRF + signed-challenge
        // exfiltration). Resolve the originator's API address from our own whitelist instead and
        // use that as the canonical seeding URL. Fall back to the payload value only when the
        // whitelist entry has no api_address (legacy data) AND the URL host matches.
        WhitelistEntry? originator = null;
        if (!selfOriginator)
        {
            originator = await whitelistRepo.GetByNodeIdAsync(restoreEvent.NodeId);
            if (originator == null)
                throw new InvalidOperationException("Restore originator is not in the local whitelist.");
        }
        if (!selfOriginator)
        {
        // Build seeder candidate list: prefer originator (canonical source), then any other
        // whitelisted peer (distributed seeding — any peer that already accepted this restore
        // can re-serve the file). Without this fallback, a leaf node in A→B→C topology where
        // C has no direct route to A would fail even though B has the file.
        // SECURITY: each peer's URL must come from OUR whitelist (not from the event payload),
        // and the downloaded file's SHA256 is verified against payload.SnapshotHash (which is
        // signed inside the originator-signed RESTORE_NETWORK event), so a malicious seeder
        // can't substitute a different file.
        // Accept https://* unconditionally; allow http only for loopback/private LAN
        // ranges (operator's own network). Blocks an inadvertently-set whitelist URL
        // pointing at an external attacker host from being used as a signed-challenge
        // oracle. (Whitelist URLs are operator-trusted but worth validating in depth.)
        static bool IsAcceptableSeederUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return false;
            if (Uri.CheckHostName(u.Host) == UriHostNameType.IPv4 || Uri.CheckHostName(u.Host) == UriHostNameType.IPv6)
            {
                if (IPAddress.TryParse(u.Host, out var ip))
                    return IsAllowedPrivateIp(ip);
                return false;
            }
            if (u.Scheme == Uri.UriSchemeHttps) return true;
            return string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        var candidates = new List<(string url, BeeMemoryBank.Core.Models.WhitelistEntry peer)>();
        if (!string.IsNullOrWhiteSpace(originator!.ApiAddress) && IsAcceptableSeederUrl(originator.ApiAddress))
            candidates.Add((originator.ApiAddress.TrimEnd('/'), originator));
        else if (!string.IsNullOrWhiteSpace(originator.ApiAddress))
            _logger.LogWarning("Originator URL {Url} rejected (not https or private LAN); falling back to other seeders",
                originator.ApiAddress);
        foreach (var peer in await whitelistRepo.GetAllActiveAsync())
        {
            if (peer.NodeId == identity.NodeId) continue;
            if (peer.NodeId == originator.NodeId) continue;
            if (string.IsNullOrWhiteSpace(peer.ApiAddress)) continue;
            if (!IsAcceptableSeederUrl(peer.ApiAddress))
            {
                _logger.LogWarning("Peer seeder URL {Url} rejected (not https or private LAN)", peer.ApiAddress);
                continue;
            }
            candidates.Add((peer.ApiAddress.TrimEnd('/'), peer));
        }
        // Last-resort: payload.SourceUrl, with strict HTTPS+hostname check (anti-SSRF).
        if (Uri.TryCreate(payload.SourceUrl, UriKind.Absolute, out var pu)
            && pu.Scheme == Uri.UriSchemeHttps
            && Uri.CheckHostName(pu.Host) == UriHostNameType.Dns)
            candidates.Add((payload.SourceUrl.TrimEnd('/'), originator));

        if (candidates.Count == 0)
            throw new InvalidOperationException("No reachable seeders found for restore (originator unknown and no peers in whitelist with api_address).");

        using var client = _httpClientFactory.CreateClient("SyncClient");
        client.Timeout = TimeSpan.FromHours(1);

        Exception? lastError = null;
        bool downloaded = false;
        string? seederSigB64 = null;
        foreach (var (sourceUrl, seederPeer) in candidates)
        {
            try
            {
                var (resolvedUrl, originalHost) = await ResolveAndPinSeederAsync(sourceUrl);

                using var challengeReq = new HttpRequestMessage(HttpMethod.Post, $"{resolvedUrl}/api/sync/challenge");
                if (!string.IsNullOrEmpty(originalHost))
                    challengeReq.Headers.Host = originalHost;
                var challengeResp = await client.SendAsync(challengeReq);
                challengeResp.EnsureSuccessStatusCode();
                var challengeData = await challengeResp.Content.ReadFromJsonAsync<ChallengeDto>(JsonOpts)
                    ?? throw new InvalidDataException("Invalid challenge response.");

                var challengeBytes = Convert.FromBase64String(challengeData.Challenge);
                var domainTag = "BMB-CHALLENGE-V1\0"u8.ToArray();
                var challengePayload = domainTag.Concat(challengeBytes).ToArray();
                var signature = NodeIdentityCrypto.SignWithIdentityOrGetDek(
                    identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                    identity.NodeId, () => _sessionService.GetMasterDek(), challengePayload);

                using var authReq = new HttpRequestMessage(HttpMethod.Post, $"{resolvedUrl}/api/sync/authenticate")
                {
                    Content = JsonContent.Create(new
                    {
                        NodeId = identity.NodeId,
                        ChallengeB64 = challengeData.Challenge,
                        SignatureB64 = Convert.ToBase64String(signature)
                    })
                };
                if (!string.IsNullOrEmpty(originalHost))
                    authReq.Headers.Host = originalHost;
                var authResp = await client.SendAsync(authReq);
                authResp.EnsureSuccessStatusCode();
                var authData = await authResp.Content.ReadFromJsonAsync<AuthTokenDto>(JsonOpts)
                    ?? throw new InvalidDataException("Invalid authenticate response.");

                using var req = new HttpRequestMessage(HttpMethod.Get, $"{resolvedUrl}/api/snapshots/restore/{eventId}/file");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authData.Token);
                if (!string.IsNullOrEmpty(originalHost))
                    req.Headers.Host = originalHost;

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();

                seederSigB64 = resp.Headers.TryGetValues("X-BMB-Snapshot-Signature", out var sigVals)
                    ? sigVals.FirstOrDefault() : null;

                await using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var maxDownload = payload.FileSizeBytes + 1_048_576;
                    var cappedStream = new CappedStream(await resp.Content.ReadAsStreamAsync(), maxDownload);
                    await cappedStream.CopyToAsync(fs);
                }

                downloaded = true;
                _logger.LogInformation("Downloaded restore snapshot from seeder {Display} ({Url}) for event {EventId}",
                    seederPeer.DisplayName, sourceUrl, eventId);
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "Seeder {Url} failed; trying next", sourceUrl);
            }
        }

        if (!downloaded)
            throw new InvalidOperationException(
                $"All {candidates.Count} candidate seeders failed for restore. Last error: {lastError?.Message}", lastError);

        // Note: the X-BMB-Snapshot-Signature header is signed by the SEEDER's own key (see
        // SnapshotEndpoints.cs:289), but distributed seeding means the seeder may not be the
        // originator. Verifying against the seeder's pubkey would require trusting that any
        // whitelisted peer can swap files (defeats defense-in-depth). The originator-signed
        // payload.SnapshotHash check below (line ~591) is the canonical integrity proof:
        // it's signed inside the RESTORE_NETWORK event by the originator and binds the file
        // bytes to that immutable signature. The seeder-key signature is therefore redundant
        // and we drop the check — keeping it would require either trusting the seeder
        // unconditionally (security regression) OR distributing originator-signed signatures
        // through every relay hop (extra complexity for no gain over hash verify).
        } // end if (!selfOriginator)

        // Hash check runs for BOTH self-originator AND remote paths. Catches:
        //   (a) tampered-in-flight bytes from a remote seeder (download path)
        //   (b) tampered local file between /restore-network endpoint writing it and
        //       this background task picking it up (self-originator path) — closes the
        //       local-tamper window if another process briefly has write access to
        //       restore-pending/<eventId>.bin (uclaw isolation breach, container
        //       escape, etc.). Cheap defense (one SHA256 pass).
        var actualHash = await ComputeHashAsync(filePath);
        if (!string.Equals(actualHash, payload.SnapshotHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(filePath);

            var failCount = await IncrementHashFailCountAsync(eventId);
            if (failCount >= 5)
            {
                _currentStep = RestoreFlowStep.NeedsAdminDecision;
                _errorMessage = "Snapshot hash mismatch after 5 consecutive attempts — possible tampering or corruption.";
                await stateRepo.UpdateStateAsync(eventId, RestoreEventState.Failed, _errorMessage);
                throw new NeedsAdminDecisionException(eventId, _errorMessage);
            }

            throw new InvalidOperationException("Snapshot hash mismatch — the file content does not match the announced hash.");
        }

        await ResetHashFailCountAsync(eventId);

        _currentStep = RestoreFlowStep.ApplyingSnapshot;
        _percentage = 70;
        _statusMessage = "Applying snapshot (this may take a while)...";
        await stateRepo.UpdateStateAsync(eventId, RestoreEventState.Applying);

        await _snapshotService.ApplyNetworkRestoreAsync(filePath, payload, restoreEvent);

        _currentStep = RestoreFlowStep.Completed;
        _percentage = 100;
        _statusMessage = "Restore completed successfully.";
        
        // record applied_at
        using var conn = _connFactory.CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_restore_event_state SET applied_at = @Now WHERE event_id = @EventId",
            new { Now = DateTime.UtcNow.ToString("O"), EventId = eventId });
        
        await stateRepo.UpdateStateAsync(eventId, RestoreEventState.Applied);
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsAllowedPrivateIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = IPAddress.Parse(ip.ToString());

        var bytes = ip.GetAddressBytes();

        if (bytes.Length == 4)
        {
            if (bytes[0] == 169 && bytes[1] == 254) return false;
            if (bytes[0] == 100 && (bytes[1] & 0xC0) == 0x40) return false;
            if (bytes[0] == 0) return false;

            if (IPAddress.IsLoopback(ip)) return true;
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            return false;
        }

        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6SiteLocal || ip.IsIPv6LinkLocal) return true;
        if (bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC) return true;
        return false;
    }

    private static bool IsAcceptableResolvedIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = IPAddress.Parse(ip.ToString());

        var bytes = ip.GetAddressBytes();

        if (bytes.Length == 4)
        {
            if (bytes[0] == 169 && bytes[1] == 254) return false;
            if (bytes[0] == 100 && (bytes[1] & 0xC0) == 0x40) return false;
            if (bytes[0] == 0) return false;
        }
        else if (bytes.Length == 16)
        {
            if (IPAddress.IsLoopback(ip)) return false;
            if (ip.IsIPv6LinkLocal) return false;
            if (ip.IsIPv6SiteLocal) return false;
            if ((bytes[0] & 0xFE) == 0xFC) return false; // fc00::/7 ULA
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return false; // fe80::/10 link-local fallback
            // ::ffff:X.Y.Z.W mapped IPv4 (handled by IsIPv4MappedToIPv6 branch above; defensive)
        }

        return true;
    }

    private static async Task<(string resolvedUrl, string originalHost)> ResolveAndPinSeederAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            throw new InvalidOperationException($"Invalid seeder URL: {url}");

        var host = u.Host;

        if (IPAddress.TryParse(host, out _))
            return (url, host);

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"DNS resolution failed for seeder {host}: {ex.Message}", ex);
        }

        foreach (var addr in addresses)
        {
            if (IsAcceptableResolvedIp(addr))
            {
                var builder = new UriBuilder(u);
                var addrStr = addr.ToString();
                builder.Host = addrStr.Contains(':') ? $"[{addrStr}]" : addrStr;
                return (builder.Uri.ToString(), host);
            }
        }

        throw new InvalidOperationException($"No acceptable IP addresses resolved for seeder host {host}");
    }

    private sealed class CappedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _totalRead;

        public CappedStream(Stream inner, long maxBytes) { _inner = inner; _maxBytes = maxBytes; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _totalRead; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            _totalRead += read;
            if (_totalRead > _maxBytes) throw new InvalidOperationException("Download exceeded size cap");
            return read;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var read = await _inner.ReadAsync(buffer, offset, count, ct);
            _totalRead += read;
            if (_totalRead > _maxBytes) throw new InvalidOperationException("Download exceeded size cap");
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private async Task<int> IncrementHashFailCountAsync(string eventId)
    {
        using var conn = _connFactory.CreateConnection();
        var current = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT hash_fail_count FROM tbl_restore_event_state WHERE event_id = @EventId",
            new { EventId = eventId });
        var newCount = (current ?? 0) + 1;
        await conn.ExecuteAsync(
            "UPDATE tbl_restore_event_state SET hash_fail_count = @Count WHERE event_id = @EventId",
            new { Count = newCount, EventId = eventId });
        return newCount;
    }

    private async Task ResetHashFailCountAsync(string eventId)
    {
        using var conn = _connFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_restore_event_state SET hash_fail_count = 0 WHERE event_id = @EventId",
            new { EventId = eventId });
    }

    private sealed record ChallengeDto(string Challenge, Guid ServerNodeId);
    private sealed record AuthTokenDto(string Token);
}
