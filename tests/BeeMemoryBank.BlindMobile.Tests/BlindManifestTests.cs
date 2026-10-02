using System.Xml.Linq;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// What the Android build really put into the app's manifest (the merged file the last build left in obj/):
/// the blind app calls out over the network, runs a foreground service and posts a notification, and its
/// data must not go to an automatic cloud backup. Without <c>SingleProject</c> the build ignored
/// Platforms/Android/AndroidManifest.xml altogether and the first Release APK had none of this — no INTERNET.
/// Like the assembly guard, it reads the output of the last build, so CI runs it after the APK is built.
/// </summary>
public sealed class BlindManifestTests
{
    private static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";

    [Theory]
    [InlineData("android.permission.INTERNET")]
    [InlineData("android.permission.ACCESS_NETWORK_STATE")]
    [InlineData("android.permission.FOREGROUND_SERVICE")]
    [InlineData("android.permission.FOREGROUND_SERVICE_DATA_SYNC")]
    [InlineData("android.permission.POST_NOTIFICATIONS")]
    [InlineData("android.permission.RECEIVE_BOOT_COMPLETED")]
    public void TheBuiltManifest_AsksForWhatTheAppNeeds(string permission)
    {
        var (manifest, path) = LastBuiltManifest();

        manifest.Root!.Elements("uses-permission").Select(e => (string?)e.Attribute(Android + "name"))
            .Should().Contain(permission, $"the app needs it and {path} is what the last build produced");
    }

    /// <summary>
    /// Nothing in the blind app asks the user to exempt it from battery optimisation (searched: no use of
    /// ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS), so the permission is dead weight copied from the ordinary app.
    /// WorkManager's constraints (Wi-Fi, charger) are what lets the jobs run.
    /// </summary>
    [Fact]
    public void TheBuiltManifest_AsksForNothingTheAppNeverUses()
    {
        var (manifest, path) = LastBuiltManifest();

        manifest.Root!.Elements("uses-permission").Select(e => (string?)e.Attribute(Android + "name"))
            .Should().NotContain("android.permission.REQUEST_IGNORE_BATTERY_OPTIMIZATIONS", $"it is declared in {path} but never requested");
    }

    [Fact]
    public void TheBuiltManifest_KeepsTheDataOutOfAutomaticBackups()
    {
        var (manifest, path) = LastBuiltManifest();
        var application = manifest.Root!.Element("application")!;

        ((string?)application.Attribute(Android + "allowBackup")).Should().Be("false", $"in {path}");
    }

    [Fact]
    public void TheBuiltManifest_DeclaresTheBackupServiceAsADataSyncForegroundService()
    {
        var (manifest, path) = LastBuiltManifest();

        var service = manifest.Root!.Element("application")!.Elements("service")
            .Single(s => ((string?)s.Attribute(Android + "name"))!.EndsWith(".BlindBackupService", StringComparison.Ordinal));
        ((string?)service.Attribute(Android + "foregroundServiceType")).Should().Be("dataSync", $"in {path}");
    }

    /// <summary>
    /// The managed types declare their own manifest entries ([Activity], [BroadcastReceiver], [Service]) under a
    /// generated Java name (<c>crc64…</c>). A hand-written entry named after the app's package ("<c>.MainActivity</c>")
    /// is a class that does not exist: the launcher offered a second, dead start icon and the boot receiver never ran.
    /// </summary>
    [Fact]
    public void TheBuiltManifest_NamesNoComponentClassTheAppDoesNotHave()
    {
        var (manifest, path) = LastBuiltManifest();
        var application = manifest.Root!.Element("application")!;
        var package = (string)manifest.Root.Attribute("package")!;

        var invented = application.Elements()
            .Where(e => e.Name.LocalName is "activity" or "service" or "receiver")
            .Select(e => (string?)e.Attribute(Android + "name"))
            .Where(n => n != null && n.StartsWith(package + ".", StringComparison.Ordinal))
            .ToList();

        invented.Should().BeEmpty($"{path} declares classes under the application id that the managed build does not generate");
    }

    [Fact]
    public void TheBuiltManifest_HasExactlyOneLauncherActivity()
    {
        var (manifest, path) = LastBuiltManifest();

        var launchers = manifest.Root!.Element("application")!.Elements("activity")
            .Where(a => a.Elements("intent-filter").Any(f =>
                f.Elements("category").Any(c => (string?)c.Attribute(Android + "name") == "android.intent.category.LAUNCHER")))
            .Select(a => (string?)a.Attribute(Android + "name"))
            .ToList();

        launchers.Should().HaveCount(1, $"{path} must offer one way to start the app, found: {string.Join(", ", launchers)}");
    }

    private static (XDocument Manifest, string Path) LastBuiltManifest()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "BeeMemoryBank.slnx"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the repository root holds BeeMemoryBank.slnx");
        var project = Path.Combine(dir!, "mobile", "BeeMemoryBank.BlindMobile", "obj");
        var newest = new[] { "Debug", "Release" }
            .Select(c => Path.Combine(project, c, "net10.0-android", "android", "AndroidManifest.xml"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        newest.Should().NotBeNull("build the mobile project first: this reads the manifest its build merged");
        return (XDocument.Load(newest!), newest!);
    }
}
