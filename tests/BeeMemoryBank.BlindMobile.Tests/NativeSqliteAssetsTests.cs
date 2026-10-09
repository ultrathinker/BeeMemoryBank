using System.Diagnostics;
using System.Text.Json;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// What NuGet resolves as the native SQLite of the two Android apps. Since 2.5.3 it is SQLite3MC.PCLRaw.bundle, whose package carries
/// libsqlite3mc.so for every Android ABI. Before, the engine package (SQLitePCLRaw.lib.e_sqlite3) had desktop RIDs only, the RID graph
/// let android-arm64 fall back to linux-arm64, and the app got a glibc library that dies on bionic at the first query: the reason the
/// two projects excluded the package's native assets. The apps no longer exclude anything, so this resolved graph is what guards them
/// (the APK itself is only built by a release; its content was checked once by hand for 2.5.3).
///
/// The test makes its own input: it runs <c>dotnet restore</c> of each app project with the project's obj folder redirected to a temp
/// folder (<c>MSBuildProjectExtensionsPath</c>), so it needs neither a build of the apps nor their obj folders, and touches neither. That
/// matters because the CI jobs restore only one of the two apps each (build-blind: the blind app; build: the ordinary one). It needs what
/// any build of these projects needs: the maui-android workload and a NuGet source (or a filled cache).
/// </summary>
public class NativeSqliteAssetsTests
{
    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "BeeMemoryBank.slnx"))) return current;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("Could not locate repository root from " + AppContext.BaseDirectory);
    }

    [Theory]
    [InlineData("BeeMemoryBank.BlindMobile")]
    [InlineData("BeeMemoryBank.Mobile")]
    public void The_android_app_resolves_libsqlite3mc_for_each_ABI_and_no_other_SQLite_engine(string project)
    {
        using var doc = JsonDocument.Parse(RestoreToTemp(project));
        var root = doc.RootElement;

        // The package list: the managed API and the SQLite3 Multiple Ciphers bundle; neither the full Microsoft.Data.Sqlite (a second
        // bundle) nor the stock engine's packages.
        var libraries = root.GetProperty("libraries").EnumerateObject().Select(l => l.Name).ToList();
        libraries.Should().Contain(l => l.StartsWith("SQLite3MC.PCLRaw.lib/", StringComparison.Ordinal));
        libraries.Should().Contain(l => l.StartsWith("SQLite3MC.PCLRaw.provider/", StringComparison.Ordinal));
        libraries.Should().Contain(l => l.StartsWith("Microsoft.Data.Sqlite.Core/", StringComparison.Ordinal));
        libraries.Should().NotContain(l => l.StartsWith("Microsoft.Data.Sqlite/", StringComparison.Ordinal),
            "the full package brings a second SQLitePCLRaw bundle");
        libraries.Should().NotContain(l => l.Contains("e_sqlite3", StringComparison.OrdinalIgnoreCase) || l.Contains("e_sqlcipher", StringComparison.OrdinalIgnoreCase),
            "the stock engine's packages must not come back (their desktop libraries are what the old exclusion kept out of the APK)");

        // For each ABI the app is built for, NuGet picks the library of exactly that Android RID, not a Linux one by the RID fallback.
        var ridTargets = root.GetProperty("targets").EnumerateObject().Where(t => t.Name.Contains('/', StringComparison.Ordinal)).ToList();
        ridTargets.Select(t => t.Name.Split('/')[1]).Should().Contain(["android-arm64", "android-x64"]);
        foreach (var target in ridTargets)
        {
            var rid = target.Name.Split('/')[1];
            var lib = target.Value.EnumerateObject().Single(p => p.Name.StartsWith("SQLite3MC.PCLRaw.lib/", StringComparison.Ordinal)).Value;
            var native = lib.GetProperty("native").EnumerateObject().Select(n => n.Name).ToList();
            native.Should().Equal([$"runtimes/{rid}/native/libsqlite3mc.so"], $"for {target.Name} the app needs the bionic library of its own ABI");
        }
    }

    /// <summary>Restores the app project with its obj folder redirected to a new temp folder and returns the text of the project.assets.json it wrote.</summary>
    private static string RestoreToTemp(string project)
    {
        var repoRoot = FindRepoRoot();
        var csproj = Path.Combine(repoRoot, "mobile", project, project + ".csproj");
        File.Exists(csproj).Should().BeTrue("the app project must exist");
        var obj = Path.Combine(Path.GetTempPath(), "bmb_native_assets_" + project + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(obj);
        try
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("restore");
            start.ArgumentList.Add(csproj);
            // Only the app itself: a global property reaches every project of the graph, and the libraries it references would write their
            // own assets file over this one. The packages they bring are still in the app's graph.
            start.ArgumentList.Add("--no-dependencies");
            start.ArgumentList.Add("-p:MSBuildProjectExtensionsPath=" + obj + Path.DirectorySeparatorChar);
            start.ArgumentList.Add("-nodeReuse:false");
            start.ArgumentList.Add("-v:q");
            // The test host may run inside a dotnet-test session whose MSBuild locations are set for another process.
            foreach (var name in new[] { "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath" }) start.Environment.Remove(name);
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(8)))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(10));
                throw new TimeoutException($"dotnet restore of {project} did not finish in 8 minutes");
            }
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"dotnet restore of {project} exited with {process.ExitCode} (it needs the maui-android workload and NuGet): {Tail(output.Result + error.Result)}");

            var assets = Path.Combine(obj, "project.assets.json");
            File.Exists(assets).Should().BeTrue($"the restore must write {assets}");
            return File.ReadAllText(assets);
        }
        finally
        {
            try { Directory.Delete(obj, true); } catch { }
        }
    }

    private static string Tail(string text) => text.Length <= 1500 ? text : text[^1500..];
}
