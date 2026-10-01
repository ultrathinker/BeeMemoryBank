using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// Pull-only protocol-3 sync for a blind phone. It deliberately has no push path: the phone has no
/// authoring services and only advances its receive cursor after EventApplier accepts an event.
/// </summary>
public sealed class BlindPhonePullClient(
    IServiceScopeFactory scopes, INodeAuthSigner signer, ILogger<BlindPhonePullClient> logger) : IBlindPhonePullClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SyncOnceAsync(HttpClient http, BlindCallCode target, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var identity = await services.GetRequiredService<INodeIdentityRepository>().GetAsync()
            ?? throw new InvalidOperationException("The blind phone identity has not been recorded.");
        if (identity.Ed25519PrivateKeyV != NodeIdentityCrypto.ExternalKeyVersion)
            throw new InvalidOperationException("The blind phone requires its v=2 external-key identity before sync.");

        var token = await PeerAuthenticator.AuthenticateAsync(signer, http, target.Address, identity, target.NodeId, ct);
        var positions = services.GetRequiredService<ISyncPositionRepository>();
        var position = await positions.GetAsync(target.NodeId);
        var after = position?.LastSequenceNum ?? 0;
        var events = await PullAsync(http, target.Address, token, after, ct);
        await BlobTransport.FetchForAsync(http, target.Address, token, events,
            services.GetRequiredService<IBlobRepository>(), logger, ct);

        var applier = services.GetRequiredService<EventApplier>();
        var eventLog = services.GetRequiredService<IEventLogRepository>();
        var quarantine = services.GetRequiredService<ISyncQuarantineRepository>();
        var last = after;
        foreach (var evt in events)
        {
            if (evt.NodeId == identity.NodeId && await IsProvenOwnAsync(eventLog, identity, evt))
            {
                last = evt.SequenceNum;
                continue;
            }

            try
            {
                await applier.ApplyAsync(evt);
                await SyncEventQuarantine.ClearFailureAsync(quarantine, evt.EventId);
                last = evt.SequenceNum;
            }
            catch (Exception ex)
            {
                if (await SyncEventQuarantine.RecordFailureAsync(quarantine, evt.EventId, evt.EventType, evt.NodeId, ex))
                {
                    logger.LogError(ex, "Blind phone quarantined event {EventId}; advancing receive cursor.", evt.EventId);
                    last = evt.SequenceNum;
                    continue;
                }
                logger.LogWarning(ex, "Blind phone stopped at event {EventId}; it will retry from the same cursor.", evt.EventId);
                break;
            }
        }

        await positions.UpsertAsync(new SyncPosition
        {
            RemoteNodeId = target.NodeId,
            LastSequenceNum = last,
            UpdatedAt = DateTime.UtcNow
        });
        await ReportPositionAsync(http, target.Address, token, last, ct);
    }

    private static async Task<List<SyncEvent>> PullAsync(HttpClient http, string address, string token, long after, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{address.TrimEnd('/')}/api/sync/events?afterSequence={after}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Gone)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new SnapshotRequiredException(address, after, 0,
                string.IsNullOrWhiteSpace(body) ? "The remote node compacted this phone's receive position." : body,
                SnapshotRequiredException.SequenceTooOldCode, headReported: false);
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<SyncEvent>>(Json, ct) ?? [];
    }

    private static async Task<bool> IsProvenOwnAsync(IEventLogRepository log, NodeIdentity identity, SyncEvent evt) =>
        await log.ExistsAsync(evt.EventId) || (identity.Ed25519PublicKey is { Length: > 0 }
            && Ed25519Signer.Verify(identity.Ed25519PublicKey, EventSignature.BuildPayload(evt), evt.Signature));

    private async Task ReportPositionAsync(HttpClient http, string address, string token, long sequence, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{address.TrimEnd('/')}/api/sync/report-position?sequence={sequence}&protocolVersion={SyncProtocolVersion.Current}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Blind phone could not report receive position to {Address}", address);
        }
    }
}
