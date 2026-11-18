using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Api.Services.Offboarding;

/// <summary>The steps of a removal (plan E4): S0 lock out, S1 identity re-key, S2 shared nodes, S3 devices and planted ways back, S4 rotate, S5 close.</summary>
public static class OffboardingSteps
{
    public const int LockOut = 0, Rekey = 1, SharedNodes = 2, Review = 3, Rotate = 4, Close = 5, Closed = 6;

    /// <summary>How many steps the page counts ("step k of n").</summary>
    public const int Count = 6;

    public static readonly string[] Titles =
    [
        "Lock out", "New identity keys", "Shared nodes", "Devices and planted ways back", "New master key", "Close",
    ];
}

/// <summary>What a process has done so far, step by step (<c>tbl_offboarding.state_json</c>). Every step is resumable from what is written here.</summary>
public sealed class OffboardingProgress
{
    /// <summary>S0: the transaction committed; <see cref="S0Done"/> when the work after it (devices, events, boxes) is done too.</summary>
    public bool S0Done { get; set; }
    public Guid TombstoneEventId { get; set; }
    public DateTime S0At { get; set; }

    public bool S1Started { get; set; }
    public bool S1Done { get; set; }

    public bool S2Started { get; set; }
    /// <summary>The doors list without the person went out (once there is a shared full node to reach; a node that appears later gets it by sync).</summary>
    public bool S2Published { get; set; }
    public long S2ListVersion { get; set; }
    public bool S2Done { get; set; }

    public bool S3Done { get; set; }
    /// <summary>When S3 was done: a rotation proposed after it (for any removal) also ends this one's S4 - never an earlier one, which would have
    /// re-sealed a door S3 removed.</summary>
    public DateTime? S3DoneAt { get; set; }

    /// <summary>What the review was last built from: devices, accounts, the accounts node, slots, agents, invitations.</summary>
    public string? ReviewBasis { get; set; }

    public string? S4CommitId { get; set; }
    public string? S4NewDekFp { get; set; }
    public bool S4Done { get; set; }

    public bool S5Done { get; set; }
    public List<string> Bridges { get; set; } = [];

    /// <summary>The step the page shows: the lowest one not done.</summary>
    [JsonIgnore]
    public int CurrentStep =>
        !S0Done ? OffboardingSteps.LockOut
        : !S1Done ? OffboardingSteps.Rekey
        : !S2Done ? OffboardingSteps.SharedNodes
        : !S3Done ? OffboardingSteps.Review
        : !S4Done ? OffboardingSteps.Rotate
        : !S5Done ? OffboardingSteps.Close
        : OffboardingSteps.Closed;
}

/// <summary>One thing S3 asks the administrator to decide (plan E4 S3).</summary>
public sealed class OffboardingReviewItem
{
    /// <summary><c>kind:id</c>, e.g. <c>device:&lt;node id&gt;</c>, <c>superadmin:12</c>, <c>slot:7</c>, <c>agent:3</c>, <c>invite:&lt;id&gt;</c>, <c>user:9</c>.</summary>
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Why it is on the list, in plain words.</summary>
    public string Why { get; set; } = "";
    /// <summary>Pre-selected "remove": unknown-owner items and what the person created.</summary>
    public bool DefaultRemove { get; set; }
    /// <summary><c>remove</c> | <c>keep</c>, or null while undecided.</summary>
    public string? Decision { get; set; }
    /// <summary>The typed reason of a "keep" of a pre-selected item (stored here and in the audit).</summary>
    public string? Reason { get; set; }
    /// <summary>True once a "remove" was carried out (or there was nothing left to remove).</summary>
    public bool Applied { get; set; }
    /// <summary>
    /// A device this review revoked. It stays set when the device comes back active and is listed again, undecided: the close
    /// revokes such a device once more unless it was kept since.
    /// </summary>
    public bool RemovedOnce { get; set; }
    public string? DecidedBy { get; set; }
}

/// <summary>One row of <c>tbl_offboarding</c>.</summary>
public sealed class OffboardingRecord
{
    public Guid ProcessId { get; set; }
    public Guid PersonId { get; set; }
    public int? UserId { get; set; }
    public Guid AccountsNodeId { get; set; }
    public Guid DriverNodeId { get; set; }
    public string Target { get; set; } = "delete";
    public string? TargetRole { get; set; }
    public string? TargetRulesJson { get; set; }
    public bool AllowOpenRole { get; set; }
    public int Step { get; set; }
    public OffboardingProgress Progress { get; set; } = new();
    public List<OffboardingReviewItem>? Review { get; set; }
    public string StartedBy { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? Outcome { get; set; }
    public long Version { get; set; }
    public string Origin { get; set; } = "local";

    public bool IsOpen => ClosedAt is null;
    public bool IsDelete => Target == "delete";
    public string OutcomeWord => IsDelete ? "deleted" : "demoted";
}

/// <summary>One row of <c>tbl_offboarding_node</c>: what a step waits for on one node.</summary>
public sealed record OffboardingNodeRow(Guid ProcessId, Guid NodeId, int Step, string State, string? Baseline, DateTime? ConfirmedAt);

/// <summary>Reads and writes the process tables. Ids are stored as lowercase canonical text.</summary>
public sealed class OffboardingStore(IDbConnectionFactory db)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Columns =
        @"process_id AS ProcessId, person_id AS PersonId, user_id AS UserId, accounts_node_id AS AccountsNodeId, driver_node_id AS DriverNodeId,
          target AS Target, target_role AS TargetRole, target_rules_json AS TargetRulesJson, allow_open_role AS AllowOpenRole, step AS Step,
          state_json AS StateJson, review_json AS ReviewJson, started_by AS StartedBy, started_at AS StartedAt, updated_at AS UpdatedAt,
          closed_at AS ClosedAt, outcome AS Outcome, version AS Version, origin AS Origin";

    public static string Id(Guid value) => value.ToString("D");
    public static string Time(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public async Task<OffboardingRecord?> GetAsync(Guid processId)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<Row>($"SELECT {Columns} FROM tbl_offboarding WHERE process_id = @Id COLLATE NOCASE", new { Id = Id(processId) });
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<OffboardingRecord>> ListAsync(bool openOnly = false)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<Row>(
            $"SELECT {Columns} FROM tbl_offboarding {(openOnly ? "WHERE closed_at IS NULL" : "")} ORDER BY closed_at IS NOT NULL, started_at DESC");
        return rows.Select(Map).ToList();
    }

    public async Task<OffboardingRecord?> OpenForPersonAsync(Guid personId)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<Row>(
            $"SELECT {Columns} FROM tbl_offboarding WHERE person_id = @P COLLATE NOCASE AND closed_at IS NULL ORDER BY started_at LIMIT 1",
            new { P = Id(personId) });
        return row is null ? null : Map(row);
    }

    /// <summary>Inserts a new local process, inside the S0 transaction.</summary>
    public static Task InsertAsync(IDbConnection conn, IDbTransaction tx, OffboardingRecord r) =>
        conn.ExecuteAsync(
            @"INSERT INTO tbl_offboarding
                  (process_id, person_id, user_id, accounts_node_id, driver_node_id, target, target_role, target_rules_json, allow_open_role, step,
                   state_json, review_json, started_by, started_at, updated_at, closed_at, outcome, version, origin)
              VALUES (@ProcessId, @PersonId, @UserId, @AccountsNodeId, @DriverNodeId, @Target, @TargetRole, @TargetRulesJson, @AllowOpenRole, @Step,
                   @StateJson, @ReviewJson, @StartedBy, @StartedAt, @UpdatedAt, NULL, NULL, 0, 'local')",
            Params(r), tx);

    /// <summary>Writes the mutable part of a process (step, progress, review, driver, outcome, version).</summary>
    public async Task SaveAsync(OffboardingRecord r)
    {
        r.UpdatedAt = DateTime.UtcNow;
        r.Step = r.ClosedAt is null ? r.Progress.CurrentStep : OffboardingSteps.Closed;
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_offboarding SET user_id = @UserId, driver_node_id = @DriverNodeId, step = @Step, state_json = @StateJson, review_json = @ReviewJson,
                  updated_at = @UpdatedAt, closed_at = @ClosedAt, outcome = @Outcome, version = @Version, origin = @Origin
              WHERE process_id = @ProcessId",
            Params(r));
    }

    public async Task<IReadOnlyList<OffboardingNodeRow>> NodesAsync(Guid processId)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<(string ProcessId, string NodeId, long Step, string State, string? Baseline, string? ConfirmedAt)>(
            "SELECT process_id, node_id, step, state, baseline, confirmed_at FROM tbl_offboarding_node WHERE process_id = @Id COLLATE NOCASE ORDER BY step, node_id",
            new { Id = Id(processId) });
        return rows.Select(r => new OffboardingNodeRow(Guid.Parse(r.ProcessId), Guid.Parse(r.NodeId), (int)r.Step, r.State, r.Baseline,
            r.ConfirmedAt is null ? null : DateTime.Parse(r.ConfirmedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))).ToList();
    }

    /// <summary>Adds the node to the step's wait list if it is not there (the baseline is kept from the first time).</summary>
    public async Task EnsureNodeAsync(Guid processId, Guid nodeId, int step, string? baseline)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_offboarding_node (process_id, node_id, step, state, baseline, confirmed_at)
              VALUES (@P, @N, @S, 'waiting', @B, NULL)",
            new { P = Id(processId), N = Id(nodeId), S = step, B = baseline });
    }

    public async Task SetNodeStateAsync(Guid processId, Guid nodeId, int step, string state)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_offboarding_node SET state = @state, confirmed_at = CASE WHEN @state = 'waiting' THEN NULL ELSE @now END
              WHERE process_id = @P AND node_id = @N AND step = @S AND state <> @state",
            new { P = Id(processId), N = Id(nodeId), S = step, state, now = Time(DateTime.UtcNow) });
    }

    private static object Params(OffboardingRecord r) => new
    {
        ProcessId = Id(r.ProcessId), PersonId = Id(r.PersonId), r.UserId, AccountsNodeId = Id(r.AccountsNodeId), DriverNodeId = Id(r.DriverNodeId),
        r.Target, r.TargetRole, r.TargetRulesJson, AllowOpenRole = r.AllowOpenRole ? 1 : 0, r.Step,
        StateJson = JsonSerializer.Serialize(r.Progress, Json),
        ReviewJson = r.Review is null ? null : JsonSerializer.Serialize(r.Review, Json),
        r.StartedBy, StartedAt = Time(r.StartedAt), UpdatedAt = Time(r.UpdatedAt),
        ClosedAt = r.ClosedAt is { } c ? Time(c) : null, r.Outcome, r.Version, r.Origin,
    };

    private static OffboardingRecord Map(Row r) => new()
    {
        ProcessId = Guid.Parse(r.ProcessId), PersonId = Guid.Parse(r.PersonId), UserId = r.UserId is { } u ? (int)u : null,
        AccountsNodeId = Guid.Parse(r.AccountsNodeId), DriverNodeId = Guid.Parse(r.DriverNodeId), Target = r.Target,
        TargetRole = r.TargetRole, TargetRulesJson = r.TargetRulesJson, AllowOpenRole = r.AllowOpenRole != 0, Step = (int)r.Step,
        Progress = string.IsNullOrWhiteSpace(r.StateJson) ? new() : JsonSerializer.Deserialize<OffboardingProgress>(r.StateJson, Json) ?? new(),
        Review = string.IsNullOrWhiteSpace(r.ReviewJson) ? null : JsonSerializer.Deserialize<List<OffboardingReviewItem>>(r.ReviewJson, Json),
        StartedBy = r.StartedBy ?? "", StartedAt = ParseTime(r.StartedAt), UpdatedAt = ParseTime(r.UpdatedAt),
        ClosedAt = r.ClosedAt is null ? null : ParseTime(r.ClosedAt), Outcome = r.Outcome, Version = r.Version, Origin = r.Origin,
    };

    private static DateTime ParseTime(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : DateTime.UnixEpoch;

    private sealed class Row
    {
        public string ProcessId { get; set; } = "";
        public string PersonId { get; set; } = "";
        public long? UserId { get; set; }
        public string AccountsNodeId { get; set; } = "";
        public string DriverNodeId { get; set; } = "";
        public string Target { get; set; } = "";
        public string? TargetRole { get; set; }
        public string? TargetRulesJson { get; set; }
        public long AllowOpenRole { get; set; }
        public long Step { get; set; }
        public string StateJson { get; set; } = "{}";
        public string? ReviewJson { get; set; }
        public string? StartedBy { get; set; }
        public string StartedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string? ClosedAt { get; set; }
        public string? Outcome { get; set; }
        public long Version { get; set; }
        public string Origin { get; set; } = "local";
    }
}
