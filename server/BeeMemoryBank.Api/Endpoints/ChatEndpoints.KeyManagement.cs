using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace BeeMemoryBank.Api.Endpoints;

public static partial class ChatEndpoints
{
    // ── multi-key failover ────────────────────────────────────────────
    //
    // The chat egress path no longer pins one "highest-priority enabled key". Instead it decrypts
    // every AVAILABLE key (enabled AND not currently in a cooldown window) and tries them in priority
    // order. A key-specific HTTP failure triggers a per-key circuit breaker; a transient failure just
    // advances to the next key. When every key is exhausted, a clear AllKeysExhaustedException bubbles
    // up (→ 502 JSON for the non-streaming endpoints, → an `event: error` SSE frame for the streaming
    // loop). Per-key circuit breaker: 401→session-disable, 402/429→cooldown,
    // 5xx→retry-next; structured event: error when exhausted.

    /// <summary>A decrypted egress key held transiently for one chat turn. The plaintext lives only in
    /// memory for the duration of the failover attempt(s); the caller nulls the list reference when the
    /// turn ends (same posture as the prior single-key path — string memory is GC-managed).</summary>
    private sealed record KeyMaterial(Guid Id, string PlaintextKey);

    /// <summary>Every available key was tried and failed. Surfaced as a 502 (JSON endpoints) or an
    /// <c>event: error</c> SSE frame (streaming loop). Deliberately NOT derived from
    /// <see cref="InvalidOperationException"/> so it is distinguishable from a malformed-but-200
    /// upstream response (which has nothing to do with a bad key and must not be failovered).</summary>
    private sealed class AllKeysExhaustedException : Exception
    {
        public AllKeysExhaustedException(string message) : base(message) { }
    }

    private enum KeyFailureKind { Disable, Cooldown, Transient }

    // Cooldown applied on 402 (insufficient credits) / 429 (rate limit): the key is retried
    // automatically once this elapses (ListAvailableOrderedAsync re-admits it once disabled_until
    // is past). 401 (unauthorized/revoked) disables the row until an admin re-enables it.
    private static readonly TimeSpan KeyCooldownWindow = TimeSpan.FromMinutes(5);

    private static KeyFailureKind ClassifyKeyFailure(int statusCode) => statusCode switch
    {
        401 => KeyFailureKind.Disable,
        402 or 429 => KeyFailureKind.Cooldown,
        _ => KeyFailureKind.Transient
    };

    private static async Task RecordKeyOutcomeAsync(
        ChatSettingsRepository repo, Guid keyId, KeyFailureKind kind, string lastError)
    {
        switch (kind)
        {
            case KeyFailureKind.Disable:
                await repo.RecordKeyFailureAsync(keyId, disable: true, disabledUntil: null, lastError);
                break;
            case KeyFailureKind.Cooldown:
                await repo.RecordKeyFailureAsync(keyId, disable: false,
                    disabledUntil: DateTime.UtcNow + KeyCooldownWindow, lastError);
                break;
            default:
                await repo.RecordKeyFailureAsync(keyId, disable: false, disabledUntil: null, lastError);
                break;
        }
    }

    /// <summary>Decrypts every key eligible for egress right now (enabled, not cooling down), in
    /// priority order. An undecryptable key (sealed under a key this node no longer has) is skipped
    /// with a recorded note rather than failing the whole turn — it simply won't be tried. Returns an
    /// empty list only if nothing is available, which the caller maps to a clear "no key / all
    /// cooling down" error. Decryption needs an unlocked vault, so callers gate on
    /// <c>session.IsUnlocked</c> first.</summary>
    private static async Task<List<KeyMaterial>> DecryptAvailableKeysAsync(
        ChatSettingsRepository repo, SessionService session)
    {
        var rows = await repo.ListAvailableOrderedAsync();
        var secrets = await repo.OpenSecretsAsync(rows);
        var result = new List<KeyMaterial>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            if (secrets[i] is { } secret)
            {
                result.Add(new KeyMaterial(rows[i].Id, secret));
                continue;
            }
            // Skip — this key can't be used until it is re-entered. Note it for admin.
            await repo.RecordUsageAsync(rows[i].Id, "decrypt failed");
        }
        return result;
    }

    /// <summary>Runs <paramref name="attempt"/> against the ordered key list, retrying on a
    /// key-specific HTTP failure (401/402/429 → disable/cooldown) or a transient transport error
    /// (<see cref="HttpRequestException"/> → retry-next, no cooldown). Records success on the winning
    /// key. Throws <see cref="AllKeysExhaustedException"/> if every key failed. Used by the
    /// non-streaming endpoints; the streaming path has its own (<see cref="StreamWithFailoverAsync"/>)
    /// that additionally enforces "failover only before the first byte".</summary>
    private static async Task<T> RunWithFailoverAsync<T>(
        ChatSettingsRepository repo, IReadOnlyList<KeyMaterial> keys,
        Func<string, CancellationToken, Task<T>> attempt, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await attempt(key.PlaintextKey, ct);
                await repo.RecordKeySuccessAsync(key.Id);
                return result;
            }
            catch (OpenRouterHttpException ex)
            {
                await RecordKeyOutcomeAsync(repo, key.Id, ClassifyKeyFailure(ex.StatusCode), ex.Message);
                last = ex;
                continue;
            }
            catch (HttpRequestException ex)
            {
                // Transport/timeout — not the key's fault: try the next key, leave this one available.
                await repo.RecordUsageAsync(key.Id, ex.Message);
                last = ex;
                continue;
            }
        }
        throw new AllKeysExhaustedException(last?.Message ?? "All configured API keys failed.");
    }

    // Short display fragment (never the full secret). Mirrors AgentKeyHelper.GetKeyPrefix length.
    private static string ComputeKeyPrefix(string apiKey)
        => apiKey.Length > 12 ? string.Concat(apiKey.AsSpan(0, 12), "…") : apiKey;
}
