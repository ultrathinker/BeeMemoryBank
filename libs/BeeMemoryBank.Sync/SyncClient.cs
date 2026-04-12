using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Performs bidirectional synchronization with a single remote node over HTTP.
/// Pull: downloads events from the remote node and applies them.
/// Push: sends local events to the remote node.
/// </summary>
public class SyncClient(
    INodeIdentityRepository nodeRepo,
    IEventLogRepository eventLogRepo,
    ISyncPositionRepository syncPositionRepo,
    ISyncPushPositionRepository pushPositionRepo,
    EventApplier eventApplier,
    SessionService sessionService,
    ILogger<SyncClient> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Synchronizes with a remote node. Returns the number of new events applied locally.
    /// </summary>
    public async Task<int> SyncWithAsync(HttpClient http, string remoteApiBase, CancellationToken ct = default)
    {
        var identity = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Local node is not initialized.");

        // 0. Verify DEK compatibility via sentinel
        await VerifyRemoteSentinelAsync(http, remoteApiBase, identity, ct);

        // 1. Get remote node identity
        var remoteIdentity = await GetRemoteIdentityAsync(http, remoteApiBase, ct);
        logger.LogDebug("Synchronizing with {NodeId} ({Base})", remoteIdentity.NodeId, remoteApiBase);

        // 2. Authentication
        var token = await AuthenticateAsync(http, remoteApiBase, identity, ct);

        // 3. Pull: download new events from the remote node
        var position = await syncPositionRepo.GetAsync(remoteIdentity.NodeId);
        var afterSeq = position?.LastSequenceNum ?? 0;

        var remoteEvents = await PullEventsAsync(http, remoteApiBase, token, afterSeq, ct);
        logger.LogDebug("Received {Count} events from {NodeId}", remoteEvents.Count, remoteIdentity.NodeId);

        long lastApplied = afterSeq;
        int appliedCount = 0;
        foreach (var evt in remoteEvents)
        {
            try
            {
                await eventApplier.ApplyAsync(evt);
                lastApplied = evt.SequenceNum;
                appliedCount++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply event {Seq} from remote, stopping sync. Will retry from this position.", evt.SequenceNum);
                break;
            }
        }

        if (remoteEvents.Count > 0)
        {
            await syncPositionRepo.UpsertAsync(new SyncPosition
            {
                RemoteNodeId = remoteIdentity.NodeId,
                LastSequenceNum = lastApplied,
                UpdatedAt = DateTime.UtcNow
            });
            logger.LogInformation("Pull: applied {Applied}. Position: {Seq}",
                appliedCount, lastApplied);

            // Report our progress back to the server so it knows we are synced
            await ReportPositionAsync(http, remoteApiBase, token, lastApplied, ct);
        }

        // 4. Push: relay all events to the remote node (excluding its own events)
        const int PushBatchSize = 500;
        var pushPosition = await pushPositionRepo.GetAsync(remoteIdentity.NodeId);
        long pushAfter = pushPosition?.LastPushedSeq ?? 0;
        int totalApplied = 0, totalSkipped = 0;
        while (true)
        {
            var batch = await eventLogRepo.GetEventsToRelayAsync(remoteIdentity.NodeId, pushAfter, PushBatchSize);
            if (batch.Count == 0) break;

            var result = await PushEventsAsync(http, remoteApiBase, token, batch, ct);
            totalApplied += result.Applied;
            totalSkipped += result.Skipped;
            pushAfter = batch[^1].SequenceNum;

            if (batch.Count < PushBatchSize) break;
        }

        if (totalApplied + totalSkipped > 0)
        {
            await pushPositionRepo.UpsertAsync(new SyncPushPosition
            {
                RemoteNodeId = remoteIdentity.NodeId,
                LastPushedSeq = pushAfter,
                PushedAt = DateTime.UtcNow
            });
            logger.LogInformation("Push: applied {Applied}, skipped {Skipped} on {NodeId}",
                totalApplied, totalSkipped, remoteIdentity.NodeId);
        }

        return appliedCount;
    }

    private async Task VerifyRemoteSentinelAsync(
        HttpClient http, string baseUrl, NodeIdentity identity, CancellationToken ct)
    {
        try
        {
            var resp = await http.GetAsync($"{baseUrl}/api/sync/sentinel", ct);
            if (!resp.IsSuccessStatusCode) return; // server without sentinel — skip check

            var dto = await resp.Content.ReadFromJsonAsync<SentinelDto>(JsonOpts, ct);
            if (dto?.SentinelB64 == null) return;

            var remoteSentinel = Convert.FromBase64String(dto.SentinelB64);
            var localDek = sessionService.GetMasterDek();
            try
            {
                if (!MasterKeyManager.VerifySentinel(remoteSentinel, localDek))
                    throw new InvalidOperationException(
                        $"DEK mismatch with {baseUrl}: encryption keys are incompatible. " +
                        "Reset this node and rejoin the network via Setup.");
            }
            finally { Array.Clear(localDek); }
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sentinel check failed for {Base} — skipping", baseUrl);
        }
    }

    private static async Task<RemoteIdentityDto> GetRemoteIdentityAsync(
        HttpClient http, string baseUrl, CancellationToken ct)
    {
        var resp = await http.GetAsync($"{baseUrl}/api/sync/identity", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<RemoteIdentityDto>(JsonOpts, ct)
            ?? throw new InvalidDataException("Invalid identity response.");
    }

    private static async Task<string> AuthenticateAsync(
        HttpClient http, string baseUrl, NodeIdentity identity, CancellationToken ct)
    {
        // Get challenge
        var challengeResp = await http.PostAsync($"{baseUrl}/api/sync/challenge", null, ct);
        challengeResp.EnsureSuccessStatusCode();
        var challengeData = await challengeResp.Content.ReadFromJsonAsync<ChallengeDto>(JsonOpts, ct)
            ?? throw new InvalidDataException("Invalid challenge response.");

        // Sign challenge
        var challengeBytes = Convert.FromBase64String(challengeData.Challenge);
        var signature = Ed25519Signer.Sign(identity.Ed25519PrivateKey, challengeBytes);

        // Authenticate
        var authResp = await http.PostAsJsonAsync($"{baseUrl}/api/sync/authenticate", new
        {
            NodeId = identity.NodeId,
            ChallengeB64 = challengeData.Challenge,
            SignatureB64 = Convert.ToBase64String(signature)
        }, ct);
        authResp.EnsureSuccessStatusCode();
        var authData = await authResp.Content.ReadFromJsonAsync<AuthTokenDto>(JsonOpts, ct)
            ?? throw new InvalidDataException("Invalid authenticate response.");

        return authData.Token;
    }

    private static async Task<List<SyncEvent>> PullEventsAsync(
        HttpClient http, string baseUrl, string token, long afterSequence, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl}/api/sync/events?afterSequence={afterSequence}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<SyncEvent>>(JsonOpts, ct)
            ?? [];
    }

    private async Task ReportPositionAsync(
        HttpClient http, string baseUrl, string token, long sequence, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, 
                $"{baseUrl}/api/sync/report-position?sequence={sequence}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            // Non-critical, don't fail the whole sync
            logger.LogWarning(ex, "Failed to report position to {Base}", baseUrl);
        }
    }

    private static async Task<ApplyResultDto> PushEventsAsync(
        HttpClient http, string baseUrl, string token, List<SyncEvent> events, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/sync/events");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(events, options: JsonOpts);

        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ApplyResultDto>(JsonOpts, ct)
            ?? new ApplyResultDto(0, 0);
    }

    // Local DTOs for remote API responses
    private sealed record SentinelDto(string? SentinelB64);
    private sealed record RemoteIdentityDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64);
    private sealed record ChallengeDto(string Challenge, Guid ServerNodeId);
    private sealed record AuthTokenDto(string Token);
    private sealed record ApplyResultDto(int Applied, int Skipped);
}
