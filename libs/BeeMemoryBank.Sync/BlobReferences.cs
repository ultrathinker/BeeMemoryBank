using System.Text.Json;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Finds the blobs a batch of events depends on — the "ciphertext_sha256" values in article and
/// media payloads — so the transport can move those bytes before the events are applied.
/// </summary>
public static class BlobReferences
{
    private const string HashProperty = "ciphertext_sha256";

    /// <summary>
    /// Distinct blob hashes referenced by <paramref name="events"/>. Events that still embed their
    /// ciphertext inline (protocol 1) reference nothing; a payload that is not valid JSON is skipped
    /// here and left for EventApplier to reject with a proper error.
    /// </summary>
    public static HashSet<string> Collect(IEnumerable<SyncEvent> events)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evt in events)
        {
            if (!ReferencesBlob(evt.EventType) || string.IsNullOrEmpty(evt.Payload)) continue;
            try
            {
                using var doc = JsonDocument.Parse(evt.Payload);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(HashProperty, out var h)
                    && h.ValueKind == JsonValueKind.String
                    && IsWellFormedHash(h.GetString()))
                {
                    hashes.Add(h.GetString()!);
                }
            }
            catch (JsonException) { }
        }
        return hashes;
    }

    public static bool ReferencesBlob(string eventType) =>
        eventType is EventTypes.ArticleCreate or EventTypes.ArticleUpdate or EventTypes.MediaCreate;

    /// <summary>64 lowercase hex characters — the exact shape BlobHash.Compute produces.</summary>
    public static bool IsWellFormedHash(string? hash)
    {
        if (hash is null || hash.Length != 64) return false;
        foreach (var c in hash)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        return true;
    }
}
