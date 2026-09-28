namespace BeeMemoryBank.Rekey;

/// <summary>
/// The fate of every table of the copy (rekey-offline.md §8.3). The orchestrator refuses to run on a database with a
/// table that is not listed here, so a new migration cannot slip a table past the re-key.
/// <para>SKELETON (L, seams commit): R1 fills <see cref="Main"/> from the live design's CampaignTables lists, R2 fills
/// <see cref="Chat"/>. Until then both are empty and the orchestrator's check fails closed.</para>
/// </summary>
public static class RekeyTables
{
    /// <summary>The main database. Owned by R1.</summary>
    public static IReadOnlyDictionary<string, TableFate> Main { get; } =
        new Dictionary<string, TableFate>(StringComparer.OrdinalIgnoreCase);

    /// <summary>chat.db. Owned by R2.</summary>
    public static IReadOnlyDictionary<string, TableFate> Chat { get; } =
        new Dictionary<string, TableFate>(StringComparer.OrdinalIgnoreCase);
}
