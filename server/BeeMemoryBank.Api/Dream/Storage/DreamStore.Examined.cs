using Dapper;

namespace BeeMemoryBank.Api.Dream;

// dream_examined: Plaintext, "already examined, do not pay twice". A trial run neither reads nor writes it.
public sealed partial class DreamStore
{
    /// <summary>
    /// True when this candidate was already examined with the same content marker and the same prompt version. The
    /// model in the record is there for information and does not affect the skip (plan 3.5).
    /// </summary>
    public async Task<bool> WasExaminedAsync(string candidateKey, string contentMarker, string promptVersion)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM dream_examined
            WHERE candidate_key = @candidateKey AND content_marker = @contentMarker AND prompt_version = @promptVersion
            """,
            new { candidateKey, contentMarker, promptVersion }) > 0;
    }

    /// <summary>Records an examination, replacing an older one of the same candidate.</summary>
    public async Task RecordExaminedAsync(string candidateKey, string task, string contentMarker, string? modelId, string promptVersion,
        DreamExaminedVerdict verdict, Guid runId, DateTime nowUtc)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO dream_examined (candidate_key, task, content_marker, model_id, prompt_version, verdict, examined_at, run_id)
            VALUES (@candidateKey, @task, @contentMarker, @modelId, @promptVersion, @verdict, @now, @runId)
            ON CONFLICT(candidate_key) DO UPDATE SET
                task = excluded.task, content_marker = excluded.content_marker, model_id = excluded.model_id,
                prompt_version = excluded.prompt_version, verdict = excluded.verdict, examined_at = excluded.examined_at, run_id = excluded.run_id
            """,
            new
            {
                candidateKey, task, contentMarker, modelId, promptVersion,
                verdict = DreamWire.ToWire(verdict),
                now = Time(nowUtc),
                runId = Id(runId),
            });
    }

    /// <summary>When a task last examined anything (tree_review runs at most once in seven days).</summary>
    public async Task<DateTime?> GetLastExaminedAtAsync(string task)
    {
        using var conn = OpenConnection();
        var text = await conn.ExecuteScalarAsync<string?>("SELECT MAX(examined_at) FROM dream_examined WHERE task = @task", new { task });
        return ParseTimeOrNull(text);
    }
}
