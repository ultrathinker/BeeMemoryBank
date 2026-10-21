using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Api.Services;

/// <summary>Where a blind node's join stands (<see cref="BlindJoinRecord.State"/>): the words are the hub's (<c>BlindInviteStates</c>) plus the node's own.</summary>
public static class BlindJoinStates
{
    /// <summary>
    /// The redemption is on its way or its answer has not arrived: the record is written BEFORE the request leaves, because the full node commits a redemption before it answers
    /// and an answer can be lost (or the node can die) after that. The node keeps the secret it gave the full node and goes on from here: the same invitation again, a question
    /// to the full node, or the seed itself.
    /// </summary>
    public const string Redeeming = "redeeming";

    /// <summary>Redeemed; the owner of the full node has not answered yet.</summary>
    public const string Awaiting = "awaiting";

    /// <summary>The owner confirmed and the full node seeded this node.</summary>
    public const string Confirmed = "confirmed";

    public const string Refused = "refused";
    public const string Expired = "expired";

    /// <summary>The operator gave the join up (<c>bmb blind join --cancel</c>) or no join was ever started.</summary>
    public const string None = "none";
}

/// <summary>
/// The join this blind node made with a full node's invitation, kept in the node's data folder (<see cref="BlindJoinStore"/>): which invitation on which hub, the code both
/// sides read, when the owner's time runs out, where it stands. Nothing secret: the invitation's secret is spent by the redemption, and the pair code's secret is the pairing one.
/// </summary>
public sealed record BlindJoinRecord(
    string InviteId, string Address, Guid HubNodeId, string Trust, string Pin, string SourceName, string MatchCode, long ConfirmByUnix, string State, long StartedAtUnix)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>How long after the hub's own limit a join still counts as running: the owner's decision and the seed take a moment, and the clocks differ.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static BlindJoinRecord? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<BlindJoinRecord>(json, Json); }
        catch (JsonException) { return null; }
    }

    public DateTime ConfirmBy => DateTimeOffset.FromUnixTimeSeconds(ConfirmByUnix).UtcDateTime;

    /// <summary>True while the join waits for the owner (or for the answer to its redemption) and the owner's time (with a margin) has not run out.</summary>
    public bool IsInProgress(DateTimeOffset now) =>
        State is BlindJoinStates.Awaiting or BlindJoinStates.Redeeming && now.ToUnixTimeSeconds() < ConfirmByUnix + (long)Grace.TotalSeconds;

    /// <summary>
    /// The join in progress, or null (none, ended, or run out). It also needs the secret this node gave the full node: a node that was wiped or whose secret ran out can receive
    /// nothing, so nothing waits - a record left behind by a wipe must not keep the pair code away.
    /// </summary>
    public static async Task<BlindJoinRecord?> InProgressAsync(BlindJoinStore store, BlindState state, TimeProvider time)
    {
        if (store.Read() is not { } record || !record.IsInProgress(time.GetUtcNow())) return null;
        return await state.GetPairingSecretAsync() is { } secret && secret.ExpiresAt > time.GetUtcNow().UtcDateTime ? record : null;
    }
}

/// <summary>
/// Where the join is kept: a small file <c>blind/join.json</c> in the node's data folder, NOT the database. The seed that ends a successful join replaces the node's whole
/// database with the full node's package, and a record kept in it would be gone at the moment it has to say "confirmed". Nothing secret is in the file, but it is written like the
/// node's other state files: owner-only from the first byte, flushed to the disk before it takes the name (<see cref="OwnerOnlyFile.ReplaceAsync"/>), so a crash leaves the old
/// record or the new one and never half of one.
/// </summary>
public sealed class BlindJoinStore(string dataPath)
{
    private string FilePath => Path.Combine(dataPath, "blind", "join.json");

    /// <summary>The record, or null when there is none or it cannot be read (a damaged file is no join).</summary>
    public BlindJoinRecord? Read()
    {
        try { return BlindJoinRecord.Parse(File.ReadAllText(FilePath)); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Written whole, by an owner-only temporary file that is flushed and then renamed over the old record.</summary>
    public async Task WriteAsync(BlindJoinRecord record, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var bytes = Encoding.UTF8.GetBytes(record.ToJson());
        await OwnerOnlyFile.ReplaceAsync(FilePath, bytes, ct);
    }

    public void Clear()
    {
        try { File.Delete(FilePath); }
        catch (Exception ex) when (ex is DirectoryNotFoundException) { }
    }

    /// <summary>
    /// Removes the temporary files a write that died left beside the record (<c>join.json.tmp</c> of the earlier build, <c>join.json.&lt;hex&gt;.tmp</c> of
    /// <see cref="OwnerOnlyFile.ReplaceAsync"/>). They are the node's own and hold only what the record holds; the record itself is never touched. Run at the start.
    /// </summary>
    public static void RemoveOrphans(string dataPath)
    {
        var dir = Path.Combine(dataPath, "blind");
        if (!Directory.Exists(dir)) return;
        foreach (var orphan in Directory.EnumerateFiles(dir, "join.json*.tmp"))
        {
            try { File.Delete(orphan); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use by a write of this start; the next start sweeps it */ }
        }
    }

    /// <summary>The path of the record (for the permission repair at the start).</summary>
    public static string FileIn(string dataPath) => Path.Combine(dataPath, "blind", "join.json");
}

/// <summary>The old pairing is refused while a join waits for the full node's owner: the node is added in one way at a time. A 409 for the local console and the CLI.</summary>
public sealed class BlindJoinInProgressException(BlindJoinRecord join) : InvalidOperationException(
    $"This node has joined {join.SourceName}'s full node with an invitation and waits for the owner's answer; its pair code is not shown meanwhile. " +
    "Wait for the answer, or give the join up with: bmb blind join --cancel")
{
    public BlindJoinRecord Join { get; } = join;
}
