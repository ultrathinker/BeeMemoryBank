using Dapper;

namespace BeeMemoryBank.Api.Dream;

// What acting on advice needs from dream_advice beyond the engine's own reads and writes (package A1).
public sealed partial class DreamStore
{
    private const string AttemptPrefix = "attempt:";

    /// <summary>
    /// A new Apply attempt's token. While an advice is <c>applying</c> its status_note holds the token of the attempt that
    /// took it; every write that ends the attempt names the token, so an attempt the ten-minute sweep gave up on can
    /// neither go on nor settle a newer one. Not a secret: it fences, it does not authorise.
    /// </summary>
    public static string NewApplyAttempt() => AttemptPrefix + Guid.NewGuid().ToString("N");

    /// <summary>True for a status_note that is an Apply attempt's token (never shown as a note).</summary>
    public static bool IsApplyAttempt(string? note) => note is not null && note.StartsWith(AttemptPrefix, StringComparison.Ordinal);

    /// <summary>Takes a new advice into <c>applying</c> for <paramref name="attempt"/>. False when it was not new any more
    /// (another press won, or something else changed it).</summary>
    public async Task<bool> TryClaimApplyingAsync(Guid id, string attempt, int? userId, DateTime nowUtc)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            """
            UPDATE dream_advice SET status = 'applying', status_at = @now, status_by_user_id = @userId, status_note = @attempt
            WHERE id = @id AND status = 'new'
            """,
            new { id = Id(id), attempt, userId, now = Time(nowUtc) }) > 0;
    }

    /// <summary>True while the advice is still <c>applying</c> for <paramref name="attempt"/>: asked before every change an
    /// Apply makes, so an attempt that was given up on stops at its next step.</summary>
    public async Task<bool> StillApplyingAsync(Guid id, string attempt)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM dream_advice WHERE id = @id AND status = 'applying' AND status_note = @attempt",
            new { id = Id(id), attempt }) > 0;
    }

    /// <summary>
    /// Ends an attempt in one conditional write: the status (with its note), and optionally the re-sealed payload and the
    /// apply mode, change only while the advice is still <c>applying</c> for <paramref name="attempt"/>. False: the
    /// attempt was given up on (and perhaps a newer one holds the advice), and nothing was written.
    /// </summary>
    public async Task<bool> TryEndApplyingAsync(Guid id, string attempt, DreamAdviceStatus to, string? note, int? userId, DateTime nowUtc,
        DreamAdvicePayload? payload = null, DreamApplyMode? applyMode = null)
    {
        byte[]? cipher = null, iv = null;
        if (payload is not null) (cipher, iv) = await SealAsync(payload, DreamAad.Advice(id));

        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            """
            UPDATE dream_advice
            SET status = @to, status_at = @now, status_by_user_id = @userId, status_note = @note,
                payload_ciphertext = COALESCE(@cipher, payload_ciphertext), payload_iv = COALESCE(@iv, payload_iv),
                payload_key_v = CASE WHEN @cipher IS NULL THEN payload_key_v ELSE @keyVersion END,
                apply_mode = COALESCE(@mode, apply_mode)
            WHERE id = @id AND status = 'applying' AND status_note = @attempt
            """,
            new
            {
                id = Id(id),
                attempt,
                to = DreamWire.ToWire(to),
                note,
                userId,
                now = Time(nowUtc),
                cipher,
                iv,
                keyVersion = Services.ChatDataProtector.ChatKeyVersion,
                mode = applyMode is { } m ? DreamWire.ToWire(m) : null,
            }) > 0;
    }

    /// <summary>
    /// One page of the open advice (new and in_chat, trial runs' included), in id order after <paramref name="afterId"/>:
    /// what the sweep checks against the notes. Rows that do not open are left to the unreadable-row part of the sweep.
    /// </summary>
    public async Task<(IReadOnlyList<DreamAdviceRecord> Items, string? LastId)> ListOpenAdvicePageAsync(string afterId, int limit)
    {
        using var lease = await protector.AcquireAsync();
        using var conn = OpenConnection();
        var rows = (await conn.QueryAsync<AdviceRow>(
            $"SELECT {AdviceColumns} FROM dream_advice WHERE status IN @open AND id > @afterId ORDER BY id LIMIT @limit",
            new { open = OpenStatuses, afterId, limit })).ToList();
        return (OpenAll(lease, rows), rows.Count == limit ? rows[^1].Id : null);
    }
}
