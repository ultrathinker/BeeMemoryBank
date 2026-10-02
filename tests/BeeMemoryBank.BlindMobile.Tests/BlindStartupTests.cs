using System.Reflection.Metadata;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// Things only a real start of the app showed (stage 5, first run on a phone): the Release APK died in
/// <c>MauiApplication.OnCreate</c> with "StaticResource not found for key BackgroundColor". Reads the
/// assembly the last Android build produced, as metadata, so no device is needed.
/// </summary>
public sealed class BlindStartupTests
{
    private const string AppType = "BeeMemoryBank.BlindMobile.App";
    private const string PagesNamespace = "BeeMemoryBank.BlindMobile.Pages.";

    /// <summary>
    /// A page named in <c>App</c>'s constructor is built by the container BEFORE the constructor body runs
    /// <c>InitializeComponent()</c>, i.e. before <c>Application.Resources</c> exist, and its XAML then fails on
    /// the first <c>{StaticResource ...}</c>. Pages must be created after the resources are loaded.
    /// </summary>
    [Fact]
    public void App_DoesNotTakeAPageInItsConstructor()
    {
        var pages = new HashSet<string>(PageTypeNames(), StringComparer.Ordinal);
        pages.Should().NotBeEmpty("the app has at least the blind home page");

        var findings = BoundaryScanner.Scan(FindAppDll(), owner => owner == AppType, pages)
            .Where(f => f.Where == "constructor parameter")
            .ToList();

        findings.Should().BeEmpty(
            "a page injected into App is constructed before InitializeComponent() loads App.xaml's resources:\n" +
            string.Join("\n", findings));
    }

    private static IEnumerable<string> PageTypeNames()
    {
        using var stream = File.OpenRead(FindAppDll());
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = pe.GetMetadataReader();
        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            var ns = md.GetString(type.Namespace);
            if (ns.StartsWith(PagesNamespace.TrimEnd('.'), StringComparison.Ordinal) && type.GetDeclaringType().IsNil)
                yield return ns + "." + md.GetString(type.Name);
        }
    }

    internal static string FindAppDll()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "BeeMemoryBank.slnx"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the repository root holds BeeMemoryBank.slnx");
        var project = Path.Combine(dir!, "mobile", "BeeMemoryBank.BlindMobile", "bin");
        var newest = new[] { "Debug", "Release" }
            .Select(c => Path.Combine(project, c, "net10.0-android", "BeeMemoryBank.BlindMobile.dll"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        newest.Should().NotBeNull("build the mobile project first: this reads the assembly its build produced");
        return newest!;
    }
}
