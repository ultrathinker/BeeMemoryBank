using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// <see cref="IRemoteSentinelVerifier"/> of a node that holds the master DEK: compares the peer's sentinel with the one
/// derived from the session's DEK. This is the block that used to sit inside <c>SyncClient</c>, moved here unchanged so that
/// the shared sync client no longer touches the session or the master key. Vault code: a blind node does not contain it.
/// </summary>
public sealed class RemoteSentinelVerifier(SessionService sessionService, ILogger<RemoteSentinelVerifier> logger) : IRemoteSentinelVerifier
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task VerifyAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        // Sentinel verification needs the master DEK. On the locked background backup path
        // (mobile ingest signs via Keystore, vault stays locked) the DEK isn't available —
        // skip this best-effort sanity check. The pull/apply path stores ciphertext and
        // never needs the DEK, so backup-sync proceeds safely without it.
        if (!sessionService.IsUnlocked) return;
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
                {
                    // Do not throw on a sentinel mismatch: that would block pulling the
                    // DEK_ROTATION_COMMIT events that catch us up (a peer that joined before a
                    // rotation could never receive it). Under the peer-acceptance model an honest
                    // peer that rotated its DEK looks like a sentinel mismatch UNTIL we apply its
                    // COMMIT. So log a warning and proceed; the pull delivers the rotation event,
                    // which is auto-accepted (per whitelist flag) or queued for manual accept. If
                    // we still mismatch after the pull, the next cycle repeats the warning.
                    logger.LogWarning(
                        "DEK sentinel mismatch with {BaseUrl}; proceeding with event pull anyway — peer may have a pending DEK rotation we need to apply.",
                        baseUrl);
                }
            }
            finally { Array.Clear(localDek); }
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sentinel check failed for {Base} — skipping", baseUrl);
        }
    }

    private sealed record SentinelDto(string? SentinelB64);
}
