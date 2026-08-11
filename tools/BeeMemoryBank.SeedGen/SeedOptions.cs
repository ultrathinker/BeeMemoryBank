namespace BeeMemoryBank.SeedGen;

/// <summary>Parsed command-line options for <c>bmb-seedgen</c>.</summary>
public sealed record SeedOptions(
    string DataPath,
    int Articles,
    int Folders,
    int Seed,
    IReadOnlyList<string> Locales,
    string Password,
    bool Force)
{
    public static readonly IReadOnlyList<string> SupportedLocales = ["en", "ru"];
}
