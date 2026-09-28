using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.Rekey;

/// <summary>
/// <c>rekey-report.json</c> (rekey-offline.md §8.4): what the verb did, written into the new vault (or into
/// <c>D.rekey-new</c> when it stops before the swap), and read back by the report page. The verb writes it, the
/// page only reads it; both go through <see cref="Write"/> and <see cref="TryRead"/> so the shape is spelled once.
/// </summary>
/// <param name="Result">"done", "preflight-refused", "failed" or "swap-pending" (exit codes 0, 2, 3, 4).</param>
/// <param name="OldVault">The old vault's folder, <c>D.pre-rekey-&lt;ts&gt;</c>, after the swap; null before it.</param>
public sealed record RekeyReport(
    string Result,
    DateTime StartedAt,
    DateTime? FinishedAt,
    RekeyPreflightReport? Preflight,
    IReadOnlyList<RekeyStepResult> Steps,
    IReadOnlyList<string> RevokedPeers,
    IReadOnlyList<string> ClearedSlots,
    IReadOnlyList<string> ClearedAgents,
    string? OldVault,
    string? Error = null)
{
    public const string FileName = "rekey-report.json";

    public const string Done = "done", PreflightRefused = "preflight-refused", Failed = "failed", SwapPending = "swap-pending";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void Write(string dir, RekeyReport report)
    {
        var path = Path.Combine(dir, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(report, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The report in <paramref name="dir"/>; null when there is none. A file that does not parse throws:
    /// the page says so rather than showing nothing.</summary>
    public static RekeyReport? TryRead(string dir)
    {
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<RekeyReport>(File.ReadAllText(path), Json)
               ?? throw new JsonException($"{path} is empty");
    }
}
