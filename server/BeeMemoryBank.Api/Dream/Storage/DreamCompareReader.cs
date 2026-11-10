using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using Dapper;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// What <c>GET /api/dream/compare</c> reads from chat.db: the run rows (plaintext) and the <c>candidate</c> events of the
/// journal (sealed under the chat key, AAD <see cref="DreamAad.Event"/>). Read-only. A candidate event that does not open (a key
/// replaced after damage, a restored old file) or does not parse is skipped: the sweep deletes unreadable rows, and a table
/// with a hole in it is better than no table. Made by the endpoint from the container's services, so it needs no registration.
/// </summary>
public sealed class DreamCompareReader(ChatDbConnectionFactory factory, ChatDataProtector protector) : ChatRepositoryBase(factory)
{
    /// <summary>The runs found among <paramref name="ids"/>, with the id of the user who started each (for the name).</summary>
    public async Task<IReadOnlyDictionary<Guid, (DreamRunSummary Run, int? StartedByUserId)>> GetRunsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var found = new Dictionary<Guid, (DreamRunSummary, int?)>();
        if (ids.Count == 0) return found;
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<RunRow>(new CommandDefinition(
            @"SELECT id AS Id, trigger AS Trigger, mode AS Mode, state AS State, outcome AS Outcome, phase AS Phase,
                     started_by_user_id AS StartedByUserId, created_at AS CreatedAt, started_at AS StartedAt, finished_at AS FinishedAt,
                     use_model AS UseModel, use_rules AS UseRules, model_label AS ModelLabel,
                     max_advice AS MaxAdvice, max_minutes AS MaxMinutes, max_model_calls AS MaxModelCalls, max_tokens AS MaxTokens,
                     articles_in_scope AS ArticlesInScope, articles_sent AS ArticlesSent, candidates_examined AS CandidatesExamined,
                     advice_created AS AdviceCreated, advice_suppressed AS AdviceSuppressed, model_calls AS ModelCalls,
                     prompt_tokens AS PromptTokens, completion_tokens AS CompletionTokens, errors AS Errors,
                     error_code AS ErrorCode, error_text AS ErrorText
              FROM dream_run WHERE id IN @ids",
            new { ids = ids.Select(i => i.ToString("D")).ToList() }, cancellationToken: ct));
        foreach (var row in rows)
            if (row.ToSummary() is { } run)
                found[run.Id] = (run, row.StartedByUserId is { } user ? (int)user : null);
        return found;
    }

    /// <summary>The <c>candidate</c> events of a run, in the order they were written.</summary>
    public async Task<IReadOnlyList<DreamCandidateRecord>> GetCandidatesAsync(Guid runId, CancellationToken ct)
    {
        using var conn = OpenConnection();
        var rows = (await conn.QueryAsync<EventRow>(new CommandDefinition(
            @"SELECT id AS Id, seq AS Seq, payload_ciphertext AS Cipher, payload_iv AS Iv, payload_key_v AS KeyVersion
              FROM dream_run_event WHERE run_id = @runId AND kind = 'candidate' ORDER BY seq",
            new { runId = runId.ToString("D") }, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return [];

        var records = new List<DreamCandidateRecord>(rows.Count);
        using var key = await protector.AcquireAsync(ct);
        foreach (var row in rows)
        {
            if (!Guid.TryParse(row.Id, out var id) || row.KeyVersion != ChatDataProtector.ChatKeyVersion) continue;
            try
            {
                var payload = DreamJson.Deserialize<DreamEventPayload>(key.DecryptText(row.Cipher, row.Iv, DreamAad.Event(id)));
                var data = payload?.Data == null ? null : payload.Data.Deserialize<DreamCandidateEventData>(DreamJson.Options);
                if (data == null || string.IsNullOrEmpty(data.CandidateKey)) continue;
                records.Add(new DreamCandidateRecord(runId, row.Seq, data));
            }
            catch (Exception e) when (e is CryptographicException or JsonException or FormatException or ArgumentException)
            {
                // an event that does not open or does not parse: left out of the table
            }
        }
        return records;
    }

    private static DateTime? Time(string? text) =>
        !string.IsNullOrEmpty(text) && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t.ToUniversalTime()
            : null;

    private sealed class EventRow
    {
        public string Id { get; set; } = "";
        public long Seq { get; set; }
        public byte[] Cipher { get; set; } = [];
        public byte[] Iv { get; set; } = [];
        public int? KeyVersion { get; set; }
    }

    private sealed class RunRow
    {
        public string Id { get; set; } = "";
        public string Trigger { get; set; } = "";
        public string Mode { get; set; } = "";
        public string State { get; set; } = "";
        public string? Outcome { get; set; }
        public string? Phase { get; set; }
        public long? StartedByUserId { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? StartedAt { get; set; }
        public string? FinishedAt { get; set; }
        public long UseModel { get; set; }
        public long UseRules { get; set; } = 1;
        public string? ModelLabel { get; set; }
        public int MaxAdvice { get; set; }
        public int MaxMinutes { get; set; }
        public int MaxModelCalls { get; set; }
        public long MaxTokens { get; set; }
        public int ArticlesInScope { get; set; }
        public int ArticlesSent { get; set; }
        public int CandidatesExamined { get; set; }
        public int AdviceCreated { get; set; }
        public int AdviceSuppressed { get; set; }
        public int ModelCalls { get; set; }
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public int Errors { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorText { get; set; }

        /// <summary>The summary, or null for a row this build cannot read (an id or a wire name it does not know).</summary>
        public DreamRunSummary? ToSummary()
        {
            if (!Guid.TryParse(Id, out var id)
                || !DreamWire.TryParse<DreamRunTrigger>(Trigger, out var trigger)
                || !DreamWire.TryParse<DreamRunMode>(Mode, out var mode)
                || !DreamWire.TryParse<DreamRunState>(State, out var state))
                return null;
            DreamRunOutcome? outcome = DreamWire.TryParse<DreamRunOutcome>(Outcome, out var o) ? o : null;
            DreamPhase? phase = DreamWire.TryParse<DreamPhase>(Phase, out var p) ? p : null;
            return new DreamRunSummary
            {
                Id = id,
                Trigger = trigger,
                Mode = mode,
                State = state,
                Outcome = outcome,
                Phase = phase,
                CreatedAt = Time(CreatedAt) ?? default,
                StartedAt = Time(StartedAt),
                FinishedAt = Time(FinishedAt),
                UseModel = UseModel != 0,
                UseRules = UseRules != 0,
                ModelLabel = ModelLabel,
                Counters = new DreamRunCounters
                {
                    ArticlesInScope = ArticlesInScope,
                    ArticlesSent = ArticlesSent,
                    CandidatesExamined = CandidatesExamined,
                    AdviceCreated = AdviceCreated,
                    AdviceSuppressed = AdviceSuppressed,
                    ModelCalls = ModelCalls,
                    PromptTokens = PromptTokens,
                    CompletionTokens = CompletionTokens,
                    Errors = Errors,
                },
                Limits = new DreamRunLimits(MaxAdvice, MaxMinutes, MaxModelCalls, MaxTokens),
                ErrorCode = ErrorCode,
                ErrorText = ErrorText,
            };
        }
    }
}
