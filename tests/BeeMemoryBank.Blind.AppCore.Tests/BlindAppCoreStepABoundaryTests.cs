using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Step A boundary of the AppCore blind copies (docs/design/step-a-spec.md 16.3 item 10): this test project references AppCore
/// alone, so its output folder is AppCore's assembly closure. No Vault assembly is in it, and no BeeMemoryBank assembly in it
/// defines a type that could open a sealed event or a baseline. The scan reads type definitions from the files without
/// loading them; the control proves it sees the types that are there.
/// </summary>
public sealed class BlindAppCoreStepABoundaryTests
{
    private static readonly string[] Forbidden =
    [
        "BeeMemoryBank.Vault.Metadata.IMetadataKeyRing",
        "BeeMemoryBank.Vault.Metadata.MetadataKeyRing",
        "BeeMemoryBank.Vault.Metadata.MetadataKeyCrypto",
        "BeeMemoryBank.Vault.Metadata.VaultEnvelopeOpener",
        "BeeMemoryBank.Vault.Metadata.MetadataEnvelope",
        "BeeMemoryBank.Vault.Metadata.ChunkedContainer",
    ];

    [Fact]
    public void TheAppCoreClosure_HasNoVaultAssembly_AndNoTypeThatOpensMetadata()
    {
        File.Exists(Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.Vault.dll")).Should().BeFalse("AppCore never references the Vault");
        var types = DefinedTypes();
        types.Should().Contain("BeeMemoryBank.Sync.Metadata.RelayEnvelopeOpener", "control: the scan sees the relay opener AppCore does contain");
        types.Should().Contain("BeeMemoryBank.Sync.Blind.BaselineStore", "control: and the blind store");
        types.Intersect(Forbidden).Should().BeEmpty();
    }

    private static HashSet<string> DefinedTypes()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "BeeMemoryBank*.dll"))
        {
            using var stream = File.OpenRead(dll);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            foreach (var handle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(handle);
                var ns = md.GetString(type.Namespace);
                names.Add(ns.Length == 0 ? md.GetString(type.Name) : ns + "." + md.GetString(type.Name));
            }
        }
        return names;
    }
}
