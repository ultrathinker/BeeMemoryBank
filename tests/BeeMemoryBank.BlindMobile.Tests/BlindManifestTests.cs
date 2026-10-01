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
