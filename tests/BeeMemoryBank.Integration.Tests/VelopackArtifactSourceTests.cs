using System.IO;
using BeeMemoryBank.Api.Services;
using FluentAssertions;
using Velopack.Locators;

namespace BeeMemoryBank.Integration.Tests;

// VelopackArtifactSource.GetPackagesDirOrThrow — the locator/PackagesDir resolution used after a
// successful Velopack download. Outside a Velopack deployment the default locator or its
// PackagesDir is null; before the guard this surfaced as a NullReferenceException (null locator,
// unsupported platform) or an ArgumentNullException from Path.Combine (null PackagesDir, the
// normal dev-run case on win/mac/linux). These tests are deployment-independent: no test runner
// executes inside a Velopack deployment, so the guard must fire for the default locator and must
// not fire for an explicit locator with a real packages dir.
public class VelopackArtifactSourceTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"bmb_velo_pkgs_{Guid.NewGuid():N}");

    public void Dispose()
    {
        // Created only for the explicit-locator test; deleting the temp fixture mirrors the
        // pattern used by every other test class here.
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GetPackagesDirOrThrow_NoLocatorOutsideDeployment_ThrowsWithClearMessage()
    {
        var act = () => VelopackArtifactSource.GetPackagesDirOrThrow(locator: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not running inside a Velopack deployment*");
    }

    [Fact]
    public void GetPackagesDirOrThrow_ExplicitLocator_ReturnsItsPackagesDir()
    {
        Directory.CreateDirectory(_tempDir);
        var locator = new TestVelopackLocator("TestApp", "1.0.0", _tempDir);

        VelopackArtifactSource.GetPackagesDirOrThrow(locator).Should().Be(_tempDir);
    }
}
