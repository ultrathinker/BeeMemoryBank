using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Rekey;

/// <summary>
/// Every key the old vault opens with, and the new ones (rekey-offline.md §8.2). Built by the orchestrator from the
/// owner's password and wiped when the run ends; steps only read it.
/// </summary>
public sealed class RekeyKeys : IDisposable
{
    public RekeyKeys(byte[] predecessor, IReadOnlyList<byte[]> retired, byte[] campaignDek, byte[] chatKey)
    {
        Predecessor = predecessor;
        OldCandidates = [predecessor, .. retired];
        CampaignDek = campaignDek;
        ChatKey = chatKey;
    }

    /// <summary>D2: the key the owner's slot opens, the vault's current key before the re-key.</summary>
    public byte[] Predecessor { get; }

    /// <summary>D2 first, then every retired DEK that D2 opens. A row may be sealed under any of them.</summary>
    public IReadOnlyList<byte[]> OldCandidates { get; }

    /// <summary>D_c: the fresh master DEK of the new vault.</summary>
    public byte[] CampaignDek { get; }

    /// <summary>The fresh chat key of the new vault; the chat step wraps it under D_c.</summary>
    public byte[] ChatKey { get; }

    public void Dispose()
    {
        foreach (var key in OldCandidates) CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(CampaignDek);
        CryptographicOperations.ZeroMemory(ChatKey);
    }
}

/// <summary>
/// What a step works on (§8.2). <see cref="Main"/> and <see cref="Chat"/> are the COPIES in <see cref="WorkDir"/>,
/// opened exclusively by the orchestrator; <see cref="SourceDir"/> is the old vault, never written.
/// </summary>
public sealed record RekeyContext(
    string SourceDir, string WorkDir,
    SqliteConnection Main, SqliteConnection? Chat,
    RekeyKeys Keys, Guid NodeId, IRekeyProgress Progress, CancellationToken Ct);

public interface IRekeyProgress
{
    void Report(string step, long done, long total, string? note = null);
}

/// <summary>
/// One step on the copy, run once per attempt in <see cref="RekeyPlan"/>'s order. A failed attempt throws the whole
/// copy away, so a step needs no resume logic.
/// </summary>
public interface IRekeyStep
{
    string Name { get; }

    Task<RekeyStepResult> RunAsync(RekeyContext ctx);

    /// <summary>Every row the step owns opens under the new key, and none opens under any of
    /// <see cref="RekeyKeys.OldCandidates"/>. Empty when nothing is wrong.</summary>
    Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx);
}

public sealed record RekeyStepResult(string Name, IReadOnlyDictionary<string, long> Counts, IReadOnlyList<string> Notes);

public sealed record RekeyProblem(string Table, string RowKey, string Problem);

/// <summary>The pre-flight: read-only on the LIVE database and chat.db, with the old keys. Any blocking problem aborts
/// the run before anything is created.</summary>
public interface IRekeyPreflight
{
    Task<RekeyPreflightReport> RunAsync(string sourceDir, SqliteConnection liveMain, SqliteConnection? liveChat,
        RekeyKeys keys, CancellationToken ct);
}

public sealed record RekeyPreflightReport(IReadOnlyList<RekeyProblem> Blocking, IReadOnlyList<string> Warnings, long BytesNeeded);

/// <summary>What happens to a table of the copy (§8.3).</summary>
public enum TableFate
{
    /// <summary>Its sealed columns are re-sealed under the new keys.</summary>
    Resealed,
    /// <summary>Emptied: derived or old-key material the new vault must not carry.</summary>
    Cleared,
    /// <summary>Carried as it is: nothing in it is sealed under a DEK.</summary>
    Plaintext,
    /// <summary>Key slots, wrapped keys and chain material: rewritten or removed by the key-material step.</summary>
    KeyMaterial,
}
