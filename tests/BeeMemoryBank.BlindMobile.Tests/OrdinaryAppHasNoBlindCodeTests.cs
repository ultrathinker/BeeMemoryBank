using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// The blind node is its own app now, so the ORDINARY Android app (<c>mobile/BeeMemoryBank.Mobile</c>) must not carry
/// the blind-mode code any more: its files were removed from the project and nothing may bring them back. These tests
/// read what the last Release build of the ordinary app produced — the compiled assembly, the
/// trimmed one that goes into the APK, the merged manifest and the APK itself — so CI runs them after it has built the
/// ordinary APK. They never load or run the app.
/// </summary>
public sealed class OrdinaryAppHasNoBlindCodeTests
{
    private const string AppNamespace = "BeeMemoryBank.Mobile";

    // The blind-mode types of the ordinary app besides the ones that have "Blind" in the name.
    private static readonly string[] BlindOnlyByName =
    [
        "BeeMemoryBank.Mobile.Pages.ModeChoicePage",       // the first-start choice between an ordinary and a blind copy
        "BeeMemoryBank.Mobile.Platforms.Android.SafExport", // "Save to..." of a blind backup
    ];

    // Types the ordinary app must still have: without them an empty or wrong file would pass the tests below.
    private static readonly string[] StillThere =
    [
        "BeeMemoryBank.Mobile.Pages.ArticlesPage",
        "BeeMemoryBank.Mobile.Pages.UnlockPage",
        "BeeMemoryBank.Mobile.Services.SyncStatusService",
        "BeeMemoryBank.Mobile.Platforms.Android.SyncForegroundService",
        "BeeMemoryBank.Mobile.Platforms.Android.BootReceiver",
    ];

    public static TheoryData<string> BuiltAssemblies => new()
    {
        "obj/Release/net10.0-android/BeeMemoryBank.Mobile.dll",
        "obj/Release/net10.0-android/android-arm64/linked/BeeMemoryBank.Mobile.dll",
    };

    [Theory]
    [MemberData(nameof(BuiltAssemblies))]
    public void TheOrdinaryAppAssembly_DefinesNoTypeOfTheBlindMode(string relativePath)
    {
        var path = OrdinaryAppFile(relativePath);

        var blind = TypesDefinedBy(path).Where(IsBlindModeType).ToList();

        blind.Should().BeEmpty($"{path} is what the last Release build of the ordinary app compiled; the blind node is a separate app");
    }

    [Theory]
    [MemberData(nameof(BuiltAssemblies))]
    public void TheOrdinaryAppAssembly_StillHasItsOwnPagesAndServices(string relativePath)
    {
        var path = OrdinaryAppFile(relativePath);

        TypesDefinedBy(path).Should().Contain(StillThere, $"{path} must still be the ordinary app");
    }

    /// <summary>
    /// The shared blind-phone code lives in Core (<c>BlindPhone</c> namespace) and is used by the blind app and the server;
    /// the ordinary app must not name any of it. (The pinned TLS handler in <c>BeeMemoryBank.Sync.Blind</c> is the sync
    /// library's own and serves every sync call of the ordinary app, so that namespace is not checked.)
    /// </summary>
    [Theory]
    [MemberData(nameof(BuiltAssemblies))]
    public void TheOrdinaryAppAssembly_ReferencesNoBlindPhoneType(string relativePath)
    {
        var path = OrdinaryAppFile(relativePath);

        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var referenced = md.TypeReferences
            .Select(h => md.GetTypeReference(h))
            .Where(t => md.GetString(t.Namespace) == "BeeMemoryBank.Core.Services.BlindPhone")
            .Select(t => md.GetString(t.Name))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        referenced.Should().BeEmpty($"{path} must not use the blind phone services of Core");
    }

    [Fact]
    public void TheMergedManifest_DeclaresNoBlindComponent()
    {
        var path = OrdinaryAppFile("obj/Release/net10.0-android/AndroidManifest.xml");
        var manifest = XDocument.Load(path);
        var android = (XNamespace)"http://schemas.android.com/apk/res/android";

        var named = manifest.Root!.Element("application")!.Elements()
            .Select(e => (string?)e.Attribute(android + "name") ?? "")
            .Where(n => n.Contains("Blind", StringComparison.OrdinalIgnoreCase))
            .ToList();

        named.Should().BeEmpty($"{path} is the manifest the build merged for the ordinary app");
    }

    [Fact]
    public void TheOrdinaryApk_ContainsNoBlindEntry_AndItsManifestNamesNoBlindComponent()
    {
        var apk = OrdinaryApk();

        using var zip = ZipFile.OpenRead(apk);
        zip.Entries.Select(e => e.FullName).Where(n => n.Contains("blind", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty($"{apk} is the ordinary app");

        // The binary manifest keeps its names in a string pool: UTF-16 (the usual form) or UTF-8.
        var entry = zip.GetEntry("AndroidManifest.xml");
        entry.Should().NotBeNull();
        using var raw = entry!.Open();
        using var buffer = new MemoryStream();
        raw.CopyTo(buffer);
        var bytes = buffer.ToArray();
        ContainsBytes(bytes, Encoding.Unicode.GetBytes("Blind")).Should().BeFalse($"{apk}: AndroidManifest.xml (UTF-16 strings)");
        ContainsBytes(bytes, Encoding.UTF8.GetBytes("Blind")).Should().BeFalse($"{apk}: AndroidManifest.xml (UTF-8 strings)");
    }

    /// <summary>
    /// The blind-mode files are gone from the ordinary app, so its project needs no <c>Remove</c> item for them. A leftover
    /// one would suggest the files are still in the tree and would silently hide them if they came back — the build
    /// guards above are what catches that, not an exclusion. Every <c>Remove</c> item must still point at something.
    /// </summary>
    [Fact]
    public void TheOrdinaryAppProject_HasNoRemoveItemThatPointsAtNothing()
    {
        var directory = OrdinaryAppDirectory();
        var project = XDocument.Load(Path.Combine(directory, "BeeMemoryBank.Mobile.csproj"));

        var dead = project.Descendants()
            .Select(item => (string?)item.Attribute("Remove"))
            .Where(remove => remove != null)
            .SelectMany(remove => remove!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(pattern => !PatternMatchesSomething(directory, pattern))
            .ToList();

        dead.Should().BeEmpty("the project must not exclude files that are not there any more");
    }

    // What the project holds today (see StillThere): a pattern that names a folder that exists but no file in it must not count.
    [Theory]
    [InlineData(@"Pages\ArticlesPage.xaml", true)]
    [InlineData(@"Pages\ModeChoicePage.xaml", false)]
    [InlineData(@"Pages\*.xaml.cs", true)]
    [InlineData(@"Pages\*.nothing", false)]
    [InlineData(@"Pages\Mode*.cs", false)]
    [InlineData(@"Pages\Un?ockPage.xaml", true)]
    [InlineData(@"Pages\Blind?ome*.xaml", false)]
    [InlineData(@"Platforms\Android\Blind*.cs", false)]
    [InlineData(@"Platforms\Android\*Blind*.cs", false)]
    [InlineData(@"Pages\*Mode*.xaml", false)]
    [InlineData(@"Platforms\Android\**", true)]
    [InlineData(@"Platforms\**\*.xml", true)]
    [InlineData(@"Services\Blind\**", false)]
    [InlineData(@"Services\*.cs", true)]
    [InlineData(@"Gone\**", false)]
    public void APatternMatchesOnlyWhenAFileOfTheProjectMatchesIt(string pattern, bool expected)
    {
        PatternMatchesSomething(OrdinaryAppDirectory(), pattern).Should().Be(expected, pattern);
    }

    [Theory]
    [InlineData(@"Pages\*.cs", "Pages/TreePage.xaml.cs", true)]
    [InlineData(@"Pages\*.cs", "Pages/Sub/TreePage.xaml.cs", false)]
    [InlineData(@"Pages\*.cs", "Other/TreePage.xaml.cs", false)]
    [InlineData(@"Pages\*.cs", "Pages/TreePage.xaml", false)]
    [InlineData(@"Pages\**", "Pages/Sub/TreePage.xaml.cs", true)]
    [InlineData(@"Pages\**\*.cs", "Pages/TreePage.xaml.cs", true)]
    [InlineData(@"Pages\**\*.cs", "Pages/Sub/Deeper/TreePage.xaml.cs", true)]
    [InlineData(@"Pages\Tree?age.xaml", "Pages/TreePage.xaml", true)]
    [InlineData(@"Pages\Tree?age.xaml", "Pages/Tree/age.xaml", false)]
    [InlineData(@"Pages\a.b+c(d).cs", "Pages/aXb+c(d).cs", false)]
    [InlineData(@"Pages\a.b+c(d).cs", "Pages/a.b+c(d).cs", true)]
    public void AGlobMatchesLikeMsBuildDoes(string pattern, string relativePath, bool expected)
    {
        GlobMatches(pattern, relativePath).Should().Be(expected, $"{pattern} against {relativePath}");
    }

    /// <summary>MSBuild's file globs: <c>*</c> and <c>?</c> stay inside one folder, <c>**</c> crosses folders.</summary>
    internal static bool GlobMatches(string pattern, string relativePath)
    {
        var glob = pattern.Replace('\\', '/');
        var regex = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; regex.Append("(?:.*/)?"); }
                else regex.Append(".*");
            }
            else if (glob[i] == '*') regex.Append("[^/]*");
            else if (glob[i] == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(glob[i].ToString()));
        }
        return Regex.IsMatch(relativePath.Replace('\\', '/'), regex.Append(@"\z").ToString(), RegexOptions.CultureInvariant);
    }

    /// <summary>True when a file of the project matches the pattern (a plain path: when that file exists).</summary>
    internal static bool PatternMatchesSomething(string directory, string pattern)
    {
        var path = pattern.Replace('\\', '/');
        var wildcard = path.IndexOfAny(['*', '?']);
        if (wildcard < 0) return File.Exists(Path.Combine(directory, path));
        // Only the folder that holds the fixed start of the pattern is searched, so "Pages/*.cs" never walks bin/ or obj/.
        var folder = Path.Combine(directory, path[..(path.LastIndexOf('/', wildcard) + 1)]);
        return Directory.Exists(folder)
            && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Any(file => GlobMatches(path, Path.GetRelativePath(directory, file)));
    }

    private static bool IsBlindModeType(string fullName)
    {
        // A nested or compiler-generated type (a lambda's closure class, an async state machine) follows its owner.
        var owner = fullName.Split('+')[0];
        if (BlindOnlyByName.Contains(owner, StringComparer.Ordinal)) return true;
        if (!owner.StartsWith(AppNamespace + ".", StringComparison.Ordinal)) return false;
        return owner.Contains("Blind", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> TypesDefinedBy(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        return md.TypeDefinitions.Select(h => FullName(md, h)).ToList();
    }

    private static string FullName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var type = md.GetTypeDefinition(handle);
        var name = md.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return FullName(md, declaring) + "+" + name;
        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }

    private static string OrdinaryAppFile(string relativePath)
    {
        var path = Path.Combine(OrdinaryAppDirectory(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"build the ordinary app in Release first (dotnet publish mobile/BeeMemoryBank.Mobile/BeeMemoryBank.Mobile.csproj -f net10.0-android -c Release): {path}");
        return path;
    }

    private static string OrdinaryApk()
    {
        var dir = Path.Combine(OrdinaryAppDirectory(), "bin", "Release", "net10.0-android");
        Directory.Exists(dir).Should().BeTrue($"build the ordinary app in Release first: {dir}");
        var apk = Directory.EnumerateFiles(dir, "*-Signed.apk", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        apk.Should().NotBeNull($"the Release publish leaves a signed APK under {dir}");
        return apk!;
    }

    private static string OrdinaryAppDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "BeeMemoryBank.slnx"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the repository root holds BeeMemoryBank.slnx");
        return Path.Combine(dir!, "mobile", "BeeMemoryBank.Mobile");
    }
}
