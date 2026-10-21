using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

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

    /// <summary>
    /// Every entry point that reads or writes a database waits for it to be open: the app's start-up and the three background entry points (a worker can be
    /// the first thing to run after a reboot or an update) go through <c>BlindDevice</c>, whose <c>StartAsync</c> is the account host's start: it opens every account's
    /// database (<c>BlindStartup</c>, in each account's own composition) and makes the identity of one that has none. Read from the built assembly; a type that names
    /// <c>BlindDevice</c> is one that asks it (the source tests hold that the workers call <c>device.StartAsync(token)</c> before the schedulers). The page shows ONE account through the shared
    /// <c>IBlindAppController</c> of the host, which started it (the host's own tests, <c>BlindAccountHostTests</c>).
    /// </summary>
    [Theory]
    [InlineData("BeeMemoryBank.BlindMobile.App")]
    [InlineData("BeeMemoryBank.BlindMobile.Pages.BlindHomePage")]
    [InlineData("BeeMemoryBank.BlindMobile.Platforms.Android.BlindSyncWorker")]
    [InlineData("BeeMemoryBank.BlindMobile.Platforms.Android.BlindHeavyWorker")]
    [InlineData("BeeMemoryBank.BlindMobile.Platforms.Android.BlindBackupService")]
    public void EveryEntryPointThatTouchesTheDatabase_WaitsForItToBeOpen(string entryPoint)
    {
        var awaited = entryPoint == "BeeMemoryBank.BlindMobile.Pages.BlindHomePage"
            ? "BeeMemoryBank.BlindMobile.Services.Blind.IBlindAppController"
            : "BeeMemoryBank.BlindMobile.Services.Blind.BlindDevice";
        var startup = new HashSet<string>(StringComparer.Ordinal) { awaited };

        var mentions = BoundaryScanner.Scan(FindAppDll(), owner => owner == entryPoint, startup);

        mentions.Should().NotBeEmpty($"{entryPoint} must go through {awaited.Split('.')[^1]} (which has the accounts' databases opened) before it uses a database");
    }

    /// <summary>
    /// Stage 5, "Disconnect and wipe" on the phone: the wipe cleared the state through MAUI Preferences, which on Android
    /// writes with <c>apply()</c> (asynchronously), and the process was killed at once - the app came back still "paired",
    /// "first load done", with the old node id, over an empty database. The blind app's own state must be written with
    /// <c>commit()</c>, which returns after the file is written.
    /// </summary>
    [Fact]
    public void TheBlindStateStore_WritesWithCommit_NeverWithMauiPreferencesOrApply()
    {
        var calls = MemberCalls(FindAppDll(), "BeeMemoryBank.BlindMobile.Services.Blind.PreferencesBlindStore");

        calls.Should().NotContain(c => c.StartsWith("Microsoft.Maui.Storage.", StringComparison.Ordinal),
            "MAUI Preferences writes asynchronously on Android and a kill right after loses the write");
        calls.Should().Contain("Android.Content.ISharedPreferencesEditor.Commit");
        calls.Should().NotContain("Android.Content.ISharedPreferencesEditor.Apply");
    }

    /// <summary>
    /// A wipe once started the launcher activity and killed its own process, which also hosted the new activity: the app simply vanished. The restart goes through
    /// a helper activity in another process, which kills the old process and starts the app again. With several accounts nothing restarts the process any more (a removal
    /// is the account host's, and the other accounts keep running), so the helper has no caller; it stays, and this guard with it, because the manifest guard names it.
    /// </summary>
    [Fact]
    public void TheRestartHelper_StartsAHelperActivityInAnotherProcess_NotTheLauncherItselfFromTheProcessItKills()
    {
        var calls = MemberCalls(FindAppDll(), "BeeMemoryBank.BlindMobile.Platforms.Android.ProcessRestart");

        calls.Should().Contain(c => c.EndsWith(".StartActivity", StringComparison.Ordinal));
        calls.Should().NotContain(c => c.EndsWith(".GetLaunchIntentForPackage", StringComparison.Ordinal),
            "starting the launcher from the process that is about to be killed loses the new activity with it");
    }

    /// <summary>"Namespace.Type.Member" of every member reference in the method bodies of a type (nested types included).</summary>
    private static HashSet<string> MemberCalls(string assemblyPath, string typeName)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = pe.GetMetadataReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        string TypeOf(EntityHandle h) => h.Kind switch
        {
            HandleKind.TypeReference => Qualified(md, (TypeReferenceHandle)h),
            HandleKind.TypeDefinition => Qualified(md, (TypeDefinitionHandle)h),
            _ => "?",
        };
        foreach (var handle in md.TypeDefinitions)
        {
            var qualified = Qualified(md, handle);
            if (qualified != typeName && !qualified.StartsWith(typeName + "+", StringComparison.Ordinal)) continue;
            foreach (var methodHandle in md.GetTypeDefinition(handle).GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
                for (var i = 0; i + 4 < il.Length; i++)
                {
                    // call (0x28) / callvirt (0x6F) / newobj (0x73) followed by a method token; a false positive only adds a name.
                    if (il[i] is not (0x28 or 0x6F or 0x73)) continue;
                    var token = BitConverter.ToInt32(il, i + 1);
                    // A byte pattern that only looks like a call (the Release build's IL has plenty) can name a row that does not exist.
                    var row = token & 0xFFFFFF;
                    if (row == 0 || row > md.GetTableRowCount(token >> 24 == 0x0A ? TableIndex.MemberRef : TableIndex.MethodDef)) continue;
                    switch (token >> 24)
                    {
                        case 0x0A: // member reference (another assembly)
                            var reference = md.GetMemberReference(MetadataTokens.MemberReferenceHandle(token & 0xFFFFFF));
                            names.Add(TypeOf(reference.Parent) + "." + md.GetString(reference.Name));
                            break;
                        case 0x06: // method definition (this assembly)
                            var target = md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(token & 0xFFFFFF));
                            names.Add(Qualified(md, target.GetDeclaringType()) + "." + md.GetString(target.Name));
                            break;
                    }
                }
            }
        }
        return names;
    }

    private static string Qualified(MetadataReader md, TypeReferenceHandle h)
    {
        var t = md.GetTypeReference(h);
        return md.GetString(t.Namespace) is { Length: > 0 } ns ? ns + "." + md.GetString(t.Name) : md.GetString(t.Name);
    }

    private static string Qualified(MetadataReader md, TypeDefinitionHandle h)
    {
        var t = md.GetTypeDefinition(h);
        var name = md.GetString(t.Name);
        if (!t.GetDeclaringType().IsNil) return Qualified(md, t.GetDeclaringType()) + "+" + name;
        return md.GetString(t.Namespace) is { Length: > 0 } ns ? ns + "." + name : name;
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
