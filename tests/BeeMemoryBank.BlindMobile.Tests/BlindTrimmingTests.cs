using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// The first real backup on a phone stopped with a NullReferenceException inside
/// <c>Dapper.SqlMapper.GenerateValueTupleDeserializer</c> (<c>ILGenerator.Emit</c> got a null constructor): the Release
/// build trims the runtime, Dapper reaches <c>ValueTuple&lt;...&gt;</c>'s constructors only by reflection, and nothing in the
/// code names them, so the trimmer removed them. The unit tests run on the full runtime and never see it. This reads the
/// trimmed <c>System.Private.CoreLib.dll</c> the Release build produced (the one that goes into the APK).
/// </summary>
public sealed class BlindTrimmingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ValueTuples_KeepTheirConstructors_InTheTrimmedRuntime(int arity)
    {
        var path = TrimmedCoreLib();
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var tuple = md.TypeDefinitions
            .Select(h => md.GetTypeDefinition(h))
            .Single(t => md.GetString(t.Namespace) == "System" && md.GetString(t.Name) == $"ValueTuple`{arity}");
        var constructors = tuple.GetMethods()
            .Select(h => md.GetMethodDefinition(h))
            .Where(m => md.GetString(m.Name) == ".ctor")
            .ToList();

        constructors.Should().NotBeEmpty(
            $"Dapper builds ValueTuple`{arity} rows through its constructor by reflection; {path} is the runtime the APK ships");
    }

    private static string TrimmedCoreLib()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "BeeMemoryBank.slnx"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the repository root holds BeeMemoryBank.slnx");
        var linked = Path.Combine(dir!, "mobile", "BeeMemoryBank.BlindMobile", "obj", "Release", "net10.0-android", "android-arm64", "linked");
        var path = Path.Combine(linked, "System.Private.CoreLib.dll");
        File.Exists(path).Should().BeTrue($"build the Release APK first (dotnet publish ... -c Release): the trimmer's output is {path}");
        return path;
    }
}
