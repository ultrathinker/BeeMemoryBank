using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Sync.DekRotation;

/// <summary>What <c>POST /api/sync/dek-envelope/{commitEventId}</c> answers: one envelope for the caller's current key (ADR 0006).</summary>
public sealed record DekEnvelopeAnswer(
    [property: JsonPropertyName("ephemeral_pub")] string EphemeralPublicKeyB64,
    [property: JsonPropertyName("wrapped")] string WrappedB64,
    [property: JsonPropertyName("nonce")] string NonceB64);

/// <summary>
/// The re-fetch of a rotation envelope after this node re-keyed (plan WS-D D4): the request ("this commit needs a fresh envelope from that
/// node"), the fetched envelope, both node-local markers in <c>tbl_migration_marker</c>. The sync client fetches what is requested from the node
/// that rotated on its next sync with it; the retried rotation opens the fetched envelope. No new event type.
/// </summary>
public static class DekEnvelopeRefresh
{
    public const string RequestPrefix = "dek-envelope-needed:";
    public const string FetchedPrefix = "dek-envelope:";

    public static Task RequestAsync(IDbConnectionFactory db, Guid commitEventId, Guid initiator)
    {
        using var conn = db.CreateConnection();
        return conn.ExecuteAsync(
            "INSERT OR REPLACE INTO tbl_migration_marker (key, value, set_at) VALUES (@k, @v, @t)",
            new { k = RequestPrefix + commitEventId.ToString("D"), v = initiator.ToString("D"), t = DateTime.UtcNow.ToString("O") });
    }

    /// <summary>The commits whose fresh envelope is to be fetched from <paramref name="peer"/>.</summary>
    public static async Task<IReadOnlyList<Guid>> RequestedFromAsync(IDbConnectionFactory db, Guid peer)
    {
        using var conn = db.CreateConnection();
        var keys = await conn.QueryAsync<string>("SELECT key FROM tbl_migration_marker WHERE key LIKE @p AND value = @peer COLLATE NOCASE",
            new { p = RequestPrefix + "%", peer = peer.ToString("D") });
        return keys.Select(k => Guid.TryParse(k[RequestPrefix.Length..], out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
    }

    public static Task StoreAsync(IDbConnectionFactory db, Guid commitEventId, DekEnvelopeAnswer answer)
    {
        using var conn = db.CreateConnection();
        return conn.ExecuteAsync(
            @"INSERT OR REPLACE INTO tbl_migration_marker (key, value, set_at) VALUES (@k, @v, @t);
              DELETE FROM tbl_migration_marker WHERE key = @r",
            new
            {
                k = FetchedPrefix + commitEventId.ToString("D"), v = JsonSerializer.Serialize(answer), t = DateTime.UtcNow.ToString("O"),
                r = RequestPrefix + commitEventId.ToString("D"),
            });
    }

    public static async Task<DekEnvelopeAnswer?> FetchedAsync(IDbConnectionFactory db, Guid commitEventId)
    {
        using var conn = db.CreateConnection();
        var json = await conn.ExecuteScalarAsync<string?>("SELECT value FROM tbl_migration_marker WHERE key = @k",
            new { k = FetchedPrefix + commitEventId.ToString("D") });
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<DekEnvelopeAnswer>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Forgets both markers of a commit once it is applied.</summary>
    public static Task ForgetAsync(IDbConnectionFactory db, Guid commitEventId)
    {
        using var conn = db.CreateConnection();
        return conn.ExecuteAsync("DELETE FROM tbl_migration_marker WHERE key IN (@a, @b)",
            new { a = FetchedPrefix + commitEventId.ToString("D"), b = RequestPrefix + commitEventId.ToString("D") });
    }
}
