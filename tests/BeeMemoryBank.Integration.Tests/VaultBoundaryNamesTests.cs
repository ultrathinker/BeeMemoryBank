using System.Reflection;
using System.Text.RegularExpressions;
using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A guard that names a type which does not exist guards nothing and stays green forever (two such names - the namespace of
/// BlindNodeManager / BlindPreflight - passed unnoticed). This project references every assembly of the product, so it is where
/// the names the blind guards use are checked against real types.
/// </summary>
public class VaultBoundaryNamesTests
{
    private static readonly Assembly[] Everything =
    [
        typeof(BeeMemoryBank.Api.Services.SnapshotService).Assembly,
        typeof(BeeMemoryBank.Core.Models.NodeIdentity).Assembly,
        typeof(BeeMemoryBank.Crypto.Ed25519Signer).Assembly,
        typeof(BeeMemoryBank.Storage.Sqlite.MigrationRunner).Assembly,
        typeof(BeeMemoryBank.Sync.EventApplier).Assembly,
        typeof(BeeMemoryBank.Search.DefaultTokenizer).Assembly,
        typeof(BeeMemoryBank.Core.Services.SessionService).Assembly,
        typeof(BeeMemoryBank.Core.Services.BlindPhone.BlindPhoneState).Assembly,
        typeof(BeeMemoryBank.Embeddings.PendingEmbeddingProcessor).Assembly,
    ];

    private static bool Exists(string fullName) => Everything.Any(a => a.GetType(fullName, throwOnError: false) is not null);

    [Fact]
    public void EveryExplicitForbiddenTypeIsARealType()
    {
        var missing = VaultBoundary.ExplicitTypes.Where(n => !Exists(n)).ToList();
        missing.Should().BeEmpty("a forbidden-type name that resolves to nothing protects nothing");
    }

    [Fact]
    public void EveryListedVaultTypeIsARealType()
    {
        VaultBoundary.ListedVaultTypes().Where(n => !Exists(n)).Should().BeEmpty();
    }

    [Fact]
    public void EveryTypeNamedByTheBlindCompositionGuardsIsARealTypeOfSomeAssembly()
    {
        // BlindNodeCompositionTests.TheBlindCodeDoesNotContain: [InlineData("<full type name>", "<why>")] - two arguments. A name that exists nowhere
        // makes FindType() return null for every host and the test green for the wrong reason. (Types that are meant to be absent from the
        // BLIND host exist in the full product, which is what this project holds.)
        var source = File.ReadAllText(Path.Combine(DiSnapshot.RepoRoot(), "tests", "BeeMemoryBank.BlindNode.Tests", "BlindNodeCompositionTests.cs"));
        var names = Regex.Matches(source, "\\[InlineData\\(\"(BeeMemoryBank\\.[A-Za-z0-9_.+]+)\", \"").Select(m => m.Groups[1].Value).ToList();
        names.Count.Should().BeGreaterThan(40, "the scan must find the theory data");
        var missing = names.Where(n => !Exists(n)).ToList();
        missing.Should().BeEmpty("every name must be a real type, or the check proves nothing");
    }
}
