using Dapper;

namespace BeeMemoryBank.Api.Dream;

/// <summary>One row of dream_rule with its payload opened.</summary>
public sealed record DreamRuleRecord
{
    public required Guid Id { get; init; }
    public required DreamRuleScope Scope { get; init; }
    public DreamAdviceKind? Kind { get; init; }
    public required DateTime CreatedAt { get; init; }
    public int? CreatedByUserId { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public required DreamRulePayload Payload { get; init; }

    public DreamRuleDto ToDto() => new()
    {
        Id = Id,
        Scope = Scope,
        Kind = Kind,
        CreatedAt = CreatedAt,
        CreatedByUserId = CreatedByUserId,
        ExpiresAt = ExpiresAt,
        Fingerprint = Payload.Fingerprint,
        ObjectId = Payload.ObjectId,
        Path = Payload.Path,
        TagName = Payload.TagName,
        Title = Payload.Title,
        Reason = Payload.Reason,
    };
}

// dream_rule: Resealed. The fingerprint, object id, folder path, tag name and the person's words are in the payload.
public sealed partial class DreamStore
{
    private const string RuleColumns =
        """
        id AS Id, scope AS Scope, kind AS Kind, created_at AS CreatedAt, created_by_user_id AS CreatedByUserId,
        expires_at AS ExpiresAt, payload_ciphertext AS Cipher, payload_iv AS Iv, payload_key_v AS KeyVersion
        """;

    public async Task CreateRuleAsync(DreamRuleRecord rule)
    {
        var (cipher, iv) = await SealAsync(rule.Payload, DreamAad.Rule(rule.Id));
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO dream_rule (id, scope, kind, created_at, created_by_user_id, expires_at, payload_ciphertext, payload_iv, payload_key_v)
            VALUES (@id, @scope, @kind, @createdAt, @createdBy, @expiresAt, @cipher, @iv, @keyVersion)
            """,
            new
            {
                id = Id(rule.Id),
                scope = DreamWire.ToWire(rule.Scope),
                kind = rule.Kind is { } kind ? DreamWire.ToWire(kind) : null,
                createdAt = Time(rule.CreatedAt),
                createdBy = rule.CreatedByUserId,
                expiresAt = Time(rule.ExpiresAt),
                cipher,
                iv,
                keyVersion = Services.ChatDataProtector.ChatKeyVersion,
            });
    }

    /// <summary>Every rule, newest first (the list on the settings page).</summary>
    public async Task<IReadOnlyList<DreamRuleRecord>> ListRulesAsync()
    {
        using var lease = await protector.TryAcquireForReadAsync();
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<RuleRow>($"SELECT {RuleColumns} FROM dream_rule ORDER BY created_at DESC, id");
        return rows.Select(r => OpenRule(lease, r)).OfType<DreamRuleRecord>().ToList();
    }

    /// <summary>The rules in force now: what the engine loads into memory at the start of a run.</summary>
    public async Task<IReadOnlyList<DreamRuleRecord>> LoadActiveRulesAsync(DateTime nowUtc)
    {
        using var lease = await protector.AcquireAsync();
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<RuleRow>(
            $"SELECT {RuleColumns} FROM dream_rule WHERE expires_at IS NULL OR expires_at > @now", new { now = Time(nowUtc) });
        return rows.Select(r => OpenRule(lease, r)).OfType<DreamRuleRecord>().ToList();
    }

    public async Task<bool> DeleteRuleAsync(Guid id)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync("DELETE FROM dream_rule WHERE id = @id", new { id = Id(id) }) > 0;
    }

    private DreamRuleRecord? OpenRule(Services.ChatKeyLease? lease, RuleRow row)
    {
        var id = Guid.Parse(row.Id);
        var payload = Open<DreamRulePayload>(lease, row.Cipher, row.Iv, row.KeyVersion, DreamAad.Rule(id));
        return payload is null ? null : new DreamRuleRecord
        {
            Id = id,
            Scope = DreamWire.Parse<DreamRuleScope>(row.Scope),
            Kind = row.Kind is null ? null : DreamWire.Parse<DreamAdviceKind>(row.Kind),
            CreatedAt = ParseTime(row.CreatedAt),
            CreatedByUserId = (int?)row.CreatedByUserId,
            ExpiresAt = ParseTimeOrNull(row.ExpiresAt),
            Payload = payload,
        };
    }

    private sealed class RuleRow
    {
        public string Id { get; set; } = "";
        public string Scope { get; set; } = "";
        public string? Kind { get; set; }
        public string CreatedAt { get; set; } = "";
        public long? CreatedByUserId { get; set; }
        public string? ExpiresAt { get; set; }
        public byte[] Cipher { get; set; } = [];
        public byte[] Iv { get; set; } = [];
        public long? KeyVersion { get; set; }
    }
}
