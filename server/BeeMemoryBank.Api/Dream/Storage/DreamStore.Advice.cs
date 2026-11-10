using Dapper;

namespace BeeMemoryBank.Api.Dream;

/// <summary>One row of dream_advice with its payload opened.</summary>
public sealed record DreamAdviceRecord
{
    public required Guid Id { get; init; }
    public required Guid RunId { get; init; }
    public long Seq { get; init; }
    public required DreamAdviceKind Kind { get; init; }
    public required DreamAdviceSource Source { get; init; }
    public required DreamApplyMode ApplyMode { get; init; }
    public required int Score { get; init; }
    public DreamAdviceStatus Status { get; init; } = DreamAdviceStatus.New;
    public DateTime? StatusAt { get; init; }
    public int? StatusByUserId { get; init; }
    public string? StatusNote { get; init; }
    public bool Trial { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DreamAdvicePayload Payload { get; init; }

    /// <summary>The API form. Which buttons a card has follows from its status and apply mode (plan 3.4).</summary>
    public DreamAdviceDto ToDto()
    {
        var open = Status is DreamAdviceStatus.New or DreamAdviceStatus.InChat;
        var fresh = Status == DreamAdviceStatus.New;
        return new DreamAdviceDto
        {
            Id = Id,
            RunId = RunId,
            Seq = Seq,
            Kind = Kind,
            Source = Source,
            Score = Score,
            Band = DreamScore.BandOf(Score),
            Status = Status,
            StatusAt = StatusAt,
            // While applying, the note is the attempt's token (DreamStore.NewApplyAttempt), not something to show.
            StatusNote = DreamStore.IsApplyAttempt(StatusNote) ? null : StatusNote,
            ApplyMode = ApplyMode,
            Trial = Trial,
            CreatedAt = CreatedAt,
            Title = Payload.Title,
            Summary = Payload.Summary,
            Evidence = Payload.Evidence,
            Objects = Payload.Objects,
            Action = Payload.Action,
            References = Payload.References,
            Model = Payload.Model is { } model ? new DreamAdviceModelLabel(model.Label, model.PromptVersion) : null,
            Applied = Payload.Applied,
            LastError = Payload.LastError,
            ExpiredReason = Status == DreamAdviceStatus.Expired ? StatusNote : null,
            Can = new DreamAdviceCan(
                Apply: fresh && ApplyMode == DreamApplyMode.Code,
                // The built-in chat has tools for notes only: a tag or folder change is Apply or the prompt.
                Chat: fresh && ApplyMode != DreamApplyMode.Prompt && DreamPromptBuilder.ChatCanDo(Payload.Action.Type),
                // An applied or dismissed advice keeps its prompt for a personal agent, for reference (GET /advice/{id}/prompt says
                // so); an expired one does not: it no longer fits the notes.
                Prompt: Status != DreamAdviceStatus.Expired,
                Dismiss: open,
                MarkDone: open),
        };
    }
}

/// <summary>What <c>GET /advice</c> asks for. <see cref="Statuses"/> holds one status, or new and in_chat for status=open.</summary>
public sealed record DreamAdviceQuery(
    IReadOnlyList<DreamAdviceStatus> Statuses,
    DreamAdviceKind? Kind,
    int? MinScore,
    DreamAdviceSort Sort,
    int Page,
    int PageSize);

/// <summary>What the engine needs to know about an open advice: its fingerprint for repeats, its family for supersession.</summary>
public sealed record DreamOpenAdviceEntry(Guid Id, DreamAdviceKind Kind, DreamAdviceSource Source, string Fingerprint, string? FamilyKey);

// dream_advice: Resealed. Everything that names an article, a folder or a tag is in the payload.
public sealed partial class DreamStore
{
    private const string AdviceColumns =
        """
        id AS Id, run_id AS RunId, seq AS Seq, kind AS Kind, source AS Source, apply_mode AS ApplyMode, score AS Score,
        status AS Status, status_at AS StatusAt, status_by_user_id AS StatusByUserId, status_note AS StatusNote,
        trial AS Trial, created_at AS CreatedAt, payload_ciphertext AS Cipher, payload_iv AS Iv, payload_key_v AS KeyVersion
        """;

    private static readonly string[] OpenStatuses = ["new", "in_chat"];

    /// <summary>Writes the advice sealed and returns its seq (MAX(seq)+1 inside the insert: the active run is the only writer).</summary>
    public async Task<long> InsertAdviceAsync(DreamAdviceRecord advice)
    {
        var (cipher, iv) = await SealAsync(advice.Payload, DreamAad.Advice(advice.Id));
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO dream_advice
                (id, run_id, seq, kind, source, apply_mode, score, status, status_at, status_by_user_id, status_note, trial, created_at,
                 payload_ciphertext, payload_iv, payload_key_v)
            SELECT @Id, @RunId, COALESCE(MAX(seq), 0) + 1, @Kind, @Source, @ApplyMode, @Score, @Status, @StatusAt, @StatusByUserId, @StatusNote,
                   @Trial, @CreatedAt, @cipher, @iv, @keyVersion
            FROM dream_advice
            RETURNING seq
            """,
            new
            {
                Id = Id(advice.Id),
                RunId = Id(advice.RunId),
                Kind = DreamWire.ToWire(advice.Kind),
                Source = DreamWire.ToWire(advice.Source),
                ApplyMode = DreamWire.ToWire(advice.ApplyMode),
                advice.Score,
                Status = DreamWire.ToWire(advice.Status),
                StatusAt = Time(advice.StatusAt),
                advice.StatusByUserId,
                advice.StatusNote,
                Trial = advice.Trial ? 1 : 0,
                CreatedAt = Time(advice.CreatedAt),
                cipher,
                iv,
                keyVersion = Services.ChatDataProtector.ChatKeyVersion,
            });
    }

    public async Task<DreamAdviceRecord?> GetAdviceAsync(Guid id)
    {
        using var lease = await protector.TryAcquireForReadAsync();
        using var conn = OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<AdviceRow>($"SELECT {AdviceColumns} FROM dream_advice WHERE id = @id", new { id = Id(id) });
        return row is null ? null : OpenAdvice(lease, row);
    }

    /// <summary>The list of <c>GET /advice</c>: never trial advice. Only the page returned is opened.</summary>
    public async Task<(int Total, IReadOnlyList<DreamAdviceRecord> Items)> ListAdviceAsync(DreamAdviceQuery query)
    {
        var where = "trial = 0 AND status IN @statuses";
        if (query.Kind is not null) where += " AND kind = @kind";
        if (query.MinScore is not null) where += " AND score >= @minScore";
        var order = query.Sort == DreamAdviceSort.Date ? "seq DESC" : "score DESC, seq DESC";
        var args = new
        {
            statuses = query.Statuses.Select(DreamWire.ToWire).ToArray(),
            kind = query.Kind is { } kind ? DreamWire.ToWire(kind) : null,
            minScore = query.MinScore,
            pageSize = query.PageSize,
            offset = (query.Page - 1) * query.PageSize,
        };

        using var lease = await protector.TryAcquireForReadAsync();
        using var conn = OpenConnection();
        var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM dream_advice WHERE {where}", args);
        var rows = await conn.QueryAsync<AdviceRow>(
            $"SELECT {AdviceColumns} FROM dream_advice WHERE {where} ORDER BY {order} LIMIT @pageSize OFFSET @offset", args);
        return (total, OpenAll(lease, rows));
    }

    /// <summary>The advice of one run after <paramref name="afterSeq"/>, oldest first: the live feed (trial advice included).</summary>
    public async Task<IReadOnlyList<DreamAdviceRecord>> ListRunAdviceAsync(Guid runId, long afterSeq, int limit)
    {
        using var lease = await protector.TryAcquireForReadAsync();
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<AdviceRow>(
            $"SELECT {AdviceColumns} FROM dream_advice WHERE run_id = @runId AND seq > @afterSeq ORDER BY seq LIMIT @limit",
            new { runId = Id(runId), afterSeq, limit });
        return OpenAll(lease, rows);
    }

    /// <summary>Open (new, in_chat) advice by band; trial advice excluded. Plaintext columns only: no key needed.</summary>
    public async Task<DreamOpenAdviceCounts> CountOpenAdviceAsync()
    {
        using var conn = OpenConnection();
        var row = await conn.QuerySingleAsync<(long Total, long High, long Medium, long Low)>(
            $"""
            SELECT COUNT(*),
                   COALESCE(SUM(score >= {DreamThresholds.HighBandMin}), 0),
                   COALESCE(SUM(score >= {DreamThresholds.MediumBandMin} AND score < {DreamThresholds.HighBandMin}), 0),
                   COALESCE(SUM(score < {DreamThresholds.MediumBandMin}), 0)
            FROM dream_advice WHERE trial = 0 AND status IN @open
            """,
            new { open = OpenStatuses });
        return new DreamOpenAdviceCounts((int)row.Total, (int)row.High, (int)row.Medium, (int)row.Low);
    }

    /// <summary>How the advice of a run ended up. Applying is still open and counts as new.</summary>
    public async Task<DreamAdviceCounts> CountRunAdviceAsync(Guid runId)
    {
        using var conn = OpenConnection();
        var rows = (await conn.QueryAsync<(string Status, long Count)>(
            "SELECT status, COUNT(*) FROM dream_advice WHERE run_id = @runId GROUP BY status", new { runId = Id(runId) }))
            .ToDictionary(r => r.Status, r => (int)r.Count, StringComparer.Ordinal);
        int Of(string status) => rows.GetValueOrDefault(status);
        return new DreamAdviceCounts(Of("new") + Of("applying"), Of("in_chat"), Of("applied"), Of("dismissed"), Of("expired"));
    }

    /// <summary>The open non-trial advice the engine compares new drafts with. Rows that do not open are left to the sweep.</summary>
    public async Task<IReadOnlyList<DreamOpenAdviceEntry>> LoadOpenAdviceIndexAsync()
    {
        using var lease = await protector.AcquireAsync();
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<AdviceRow>(
            $"SELECT {AdviceColumns} FROM dream_advice WHERE trial = 0 AND status IN @open", new { open = OpenStatuses });
        return OpenAll(lease, rows)
            .Select(a => new DreamOpenAdviceEntry(a.Id, a.Kind, a.Source, a.Payload.Fingerprint, a.Payload.FamilyKey))
            .ToList();
    }

    /// <summary>
    /// The one way a status changes: conditional on the status still being one of <paramref name="from"/>, so that two
    /// clicks, or a click and the sweep, cannot both win. False: it was not in one of those.
    /// </summary>
    public async Task<bool> TrySetAdviceStatusAsync(Guid id, IReadOnlyList<DreamAdviceStatus> from, DreamAdviceStatus to,
        string? note, int? userId, DateTime nowUtc)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            """
            UPDATE dream_advice SET status = @to, status_at = @now, status_by_user_id = @userId, status_note = @note
            WHERE id = @id AND status IN @from
            """,
            new
            {
                id = Id(id),
                from = from.Select(DreamWire.ToWire).ToArray(),
                to = DreamWire.ToWire(to),
                now = Time(nowUtc),
                userId,
                note,
            }) > 0;
    }

    /// <summary>Re-seals the payload of an existing advice (applied.result, lastError).</summary>
    public async Task UpdateAdvicePayloadAsync(Guid id, DreamAdvicePayload payload)
    {
        var (cipher, iv) = await SealAsync(payload, DreamAad.Advice(id));
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE dream_advice SET payload_ciphertext = @cipher, payload_iv = @iv, payload_key_v = @keyVersion WHERE id = @id",
            new { id = Id(id), cipher, iv, keyVersion = Services.ChatDataProtector.ChatKeyVersion });
    }

    private List<DreamAdviceRecord> OpenAll(Services.ChatKeyLease? lease, IEnumerable<AdviceRow> rows) =>
        rows.Select(r => OpenAdvice(lease, r)).OfType<DreamAdviceRecord>().ToList();

    private DreamAdviceRecord? OpenAdvice(Services.ChatKeyLease? lease, AdviceRow row)
    {
        var id = Guid.Parse(row.Id);
        var payload = Open<DreamAdvicePayload>(lease, row.Cipher, row.Iv, row.KeyVersion, DreamAad.Advice(id));
        return payload is null ? null : new DreamAdviceRecord
        {
            Id = id,
            RunId = Guid.Parse(row.RunId),
            Seq = row.Seq,
            Kind = DreamWire.Parse<DreamAdviceKind>(row.Kind),
            Source = DreamWire.Parse<DreamAdviceSource>(row.Source),
            ApplyMode = DreamWire.Parse<DreamApplyMode>(row.ApplyMode),
            Score = (int)row.Score,
            Status = DreamWire.Parse<DreamAdviceStatus>(row.Status),
            StatusAt = ParseTimeOrNull(row.StatusAt),
            StatusByUserId = (int?)row.StatusByUserId,
            StatusNote = row.StatusNote,
            Trial = row.Trial != 0,
            CreatedAt = ParseTime(row.CreatedAt),
            Payload = payload,
        };
    }

    private sealed class AdviceRow
    {
        public string Id { get; set; } = "";
        public string RunId { get; set; } = "";
        public long Seq { get; set; }
        public string Kind { get; set; } = "";
        public string Source { get; set; } = "";
        public string ApplyMode { get; set; } = "";
        public long Score { get; set; }
        public string Status { get; set; } = "";
        public string? StatusAt { get; set; }
        public long? StatusByUserId { get; set; }
        public string? StatusNote { get; set; }
        public long Trial { get; set; }
        public string CreatedAt { get; set; } = "";
        public byte[] Cipher { get; set; } = [];
        public byte[] Iv { get; set; } = [];
        public long? KeyVersion { get; set; }
    }
}
