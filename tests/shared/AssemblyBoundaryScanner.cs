using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace BeeMemoryBank.Boundary;

/// <summary>
/// The assembly-level proof behind "a blind node does not contain the vault" (BMB-99). It reads the metadata of the application
/// assemblies of a build or publish output - it never loads or runs them - and reports every place where an assembly
///   * DEFINES a forbidden type (the TypeDef table), or a method / field with a forbidden member name,
///   * REFERENCES a forbidden type (the TypeRef table, which lists every type from another assembly that the code mentions),
///     or a forbidden member name (the MemberRef table: every call and field access into another assembly),
///   * REFERENCES a forbidden assembly (the AssemblyRef table).
/// Together these cover a type that was linked in by mistake, a call into a vault type, and a reference to the vault assembly.
/// What it cannot see is a type chosen at run time from a string. The scanner is deliberately tiny and has controls (see the tests that
/// use it): it must find a name that is really there, and it must scan the assemblies it was told to scan - an empty scan fails.
/// This file is linked into several test projects (it has no dependencies of its own).
/// </summary>
internal static class AssemblyBoundaryScanner
{
    internal sealed record Finding(string Assembly, string Kind, string Detail)
    {
        public override string ToString() => $"{Assembly}: {Kind} {Detail}";
    }

    internal sealed record Forbidden(IReadOnlySet<string> Types, IReadOnlySet<string> Members, IReadOnlySet<string> Assemblies);

    /// <summary>Scans each assembly file; throws when none was given or a file is not a managed assembly (nothing is silently skipped).</summary>
    public static IReadOnlyList<Finding> Scan(IEnumerable<string> assemblyPaths, Forbidden forbidden)
    {
        var paths = assemblyPaths.ToList();
        if (paths.Count == 0) throw new InvalidOperationException("The scan was given no assembly: it would pass vacuously.");
        var findings = new List<Finding>();
        foreach (var path in paths)
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) throw new InvalidOperationException(path + " has no metadata: not a managed assembly.");
            var md = pe.GetMetadataReader();
            var assembly = md.GetString(md.GetAssemblyDefinition().Name);

            foreach (var handle in md.TypeDefinitions)
            {
                var name = FullName(md, handle);
                if (forbidden.Types.Contains(name)) findings.Add(new Finding(assembly, "defines type", name));
                var type = md.GetTypeDefinition(handle);
                foreach (var m in type.GetMethods())
                {
                    var member = md.GetString(md.GetMethodDefinition(m).Name);
                    if (forbidden.Members.Contains(member)) findings.Add(new Finding(assembly, "defines member", name + "." + member));
                }
                foreach (var f in type.GetFields())
                {
                    var member = md.GetString(md.GetFieldDefinition(f).Name);
                    if (forbidden.Members.Contains(member)) findings.Add(new Finding(assembly, "defines member", name + "." + member));
                }
            }

            foreach (var handle in md.TypeReferences)
            {
                var name = FullName(md, handle);
                if (forbidden.Types.Contains(name)) findings.Add(new Finding(assembly, "references type", name));
            }

            foreach (var handle in md.MemberReferences)
            {
                var member = md.GetString(md.GetMemberReference(handle).Name);
                if (forbidden.Members.Contains(member)) findings.Add(new Finding(assembly, "references member", member));
            }

            foreach (var handle in md.AssemblyReferences)
            {
                var name = md.GetString(md.GetAssemblyReference(handle).Name);
                if (forbidden.Assemblies.Contains(name)) findings.Add(new Finding(assembly, "references assembly", name));
            }
        }
        return findings.Distinct().ToList();
    }

    /// <summary>The names of the types an assembly defines (top-level and nested; compiler-generated ones left out).</summary>
    public static IReadOnlySet<string> DefinedTypes(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in md.TypeDefinitions)
        {
            var name = FullName(md, handle);
            if (!IsCompilerGenerated(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>Type names defined by more than one of the given assemblies - the same type twice in one publish.</summary>
    public static IReadOnlyList<string> DuplicatedTypes(IEnumerable<string> assemblyPaths)
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in assemblyPaths)
        {
            var assembly = Path.GetFileNameWithoutExtension(path);
            foreach (var name in DefinedTypes(path))
            {
                if (name == "Program" || name == "<Module>") continue;   // each host has its own top-level Program
                if (!owners.TryGetValue(name, out var list)) owners[name] = list = [];
                list.Add(assembly);
            }
        }
        return owners.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key + " in " + string.Join(", ", kv.Value)).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>The application assemblies of an output folder: BeeMemoryBank.*.dll, and nothing else (third-party code is not ours to classify).</summary>
    public static IReadOnlyList<string> ApplicationAssemblies(string directory) =>
        Directory.GetFiles(directory, "BeeMemoryBank.*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    private static bool IsCompilerGenerated(string name) =>
        name.Contains('<') || name.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal) ||
        name.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal) || name.StartsWith("System.Diagnostics.CodeAnalysis.", StringComparison.Ordinal);

    private static string FullName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        var name = md.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return FullName(md, declaring) + "+" + name;
        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static string FullName(MetadataReader md, TypeReferenceHandle handle)
    {
        var type = md.GetTypeReference(handle);
        var name = md.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return FullName(md, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;
        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }
}
