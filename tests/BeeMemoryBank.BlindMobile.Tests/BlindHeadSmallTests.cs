using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>What the phone says about its network and power: the two probes behind the adapter, with the answers a phone really gives (and the one it can fail to give).</summary>
public sealed class BlindDeviceConditionsProbeTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AMeteredNetwork_IsOneWithoutTheNotMeteredCapability(bool notMetered, bool expectedMetered)
    {
        new ProbedBlindDeviceConditions(() => notMetered, () => null).IsMetered.Should().Be(expectedMetered);
    }

    [Fact]
    public void NoActiveNetworkOrNoCapabilities_IsUnknown_NotUnmetered()
    {
        new ProbedBlindDeviceConditions(() => null, () => null).IsMetered.Should().BeNull("an account with 'only on unmetered networks' on then waits");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void BatterySaver_IsWhatThePowerManagerSays(bool? saving)
    {
        new ProbedBlindDeviceConditions(() => null, () => saving).IsPowerSaving.Should().Be(saving);
    }

    [Fact]
    public void AProbeThatThrows_IsUnknown_NeverAnException_NeverAGuess()
    {
        var conditions = new ProbedBlindDeviceConditions(() => throw new InvalidOperationException("no service"), () => throw new UnauthorizedAccessException("no permission"));

        conditions.IsMetered.Should().BeNull();
        conditions.IsPowerSaving.Should().BeNull();
    }

    [Fact]
    public void TheProbesAreAskedEveryTime_BecauseTheNetworkChanges()
    {
        var unmetered = true;
        var conditions = new ProbedBlindDeviceConditions(() => unmetered, () => false);

        conditions.IsMetered.Should().BeFalse();
        unmetered = false;
        conditions.IsMetered.Should().BeTrue("the phone moved from Wi-Fi to mobile data");
    }
}

/// <summary>The hand-over of a <c>bmb-invite:</c> link from the system to the Add screen: only this app's links, only once, never logged or kept.</summary>
public sealed class BlindInviteLinksTests : IDisposable
{
    public BlindInviteLinksTests() => BlindInviteLinks.Take();

    public void Dispose() => BlindInviteLinks.Take();

    [Theory]
    [InlineData("bmb-invite:?v=1&a=https%3A%2F%2Fexample.org")]
    [InlineData("BMB-INVITE:?v=1")]
    public void ALinkOfThisApp_IsKeptForTheAddScreen_AndTakenOnce(string link)
    {
        BlindInviteLinks.Offer(link).Should().BeTrue();

        BlindInviteLinks.Take().Should().Be(link);
        BlindInviteLinks.Take().Should().BeNull("a link is a one-time capability: it does not stay in memory after the screen has it");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.org/?bmb-invite:")]
    [InlineData("bmb-blind-call:?a=https%3A%2F%2Fexample.org")]
    [InlineData("content://media/external/file/1")]
    [InlineData(" bmb-invite:?v=1")]
    public void AnythingThatIsNotALinkOfThisApp_IsDropped(string? text)
    {
        BlindInviteLinks.Offer(text).Should().BeFalse();

        BlindInviteLinks.Take().Should().BeNull();
    }

    [Fact]
    public void ATextLongerThanAnInvitationCanBe_IsDropped()
    {
        var tooLong = BlindInviteLinks.Scheme + new string('x', BlindInvitation.MaxTextLength);

        BlindInviteLinks.Offer(tooLong).Should().BeFalse("another app cannot make this one hold an arbitrary string");
        BlindInviteLinks.Offer(tooLong[..BlindInvitation.MaxTextLength]).Should().BeTrue("the longest an invitation can be is accepted");
    }

    [Fact]
    public void TheScreenIsToldWhenALinkWaits_AndAScreenThatThrows_DoesNotBreakTheSystemsHandOver()
    {
        var told = 0;
        void Handler() => told++;
        void Thrower() => throw new InvalidOperationException("the screen is gone");
        BlindInviteLinks.Offered += Handler;
        BlindInviteLinks.Offered += Thrower;
        try
        {
            BlindInviteLinks.Offer("bmb-invite:?v=1").Should().BeTrue();

            told.Should().Be(1);
            BlindInviteLinks.Take().Should().NotBeNull();
        }
        finally
        {
            BlindInviteLinks.Offered -= Handler;
            BlindInviteLinks.Offered -= Thrower;
        }
    }
}

/// <summary>
/// In-app QR scanning (slice 11) is built only with <c>-p:BmbQrScanner=true</c>: its binding drags ML Kit's common library, Firebase's component registry and Google's DataTransport into an app whose
/// PRIVACY.md says "no telemetry", so the owner decides, after seeing on a phone what (if anything) leaves it. These tests read the project and the sources: the default build carries none of it.
/// </summary>
public sealed class BlindQrScannerSwitchTests
{
    private static readonly string AppFolder = Path.Combine(BeeMemoryBank.Boundary.VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindMobile");

    private static string Text(params string[] relative) => File.ReadAllText(Path.Combine([AppFolder, .. relative])).ReplaceLineEndings("\n");

    [Fact]
    public void TheScannerIsOffUnlessTheBuildAsksForIt_AndThenAloneBringsItsPackages()
    {
        var project = Text("BeeMemoryBank.BlindMobile.csproj");

        project.Should().Contain("<BmbQrScanner Condition=\"'$(BmbQrScanner)' == ''\">false</BmbQrScanner>");
        // every reference to the scanner or its pins sits inside the conditional group
        var group = project[project.IndexOf("<ItemGroup Condition=\"'$(BmbQrScanner)' == 'true'\">", StringComparison.Ordinal)..];
        group = group[..group.IndexOf("</ItemGroup>", StringComparison.Ordinal)];
        group.Should().Contain("Xamarin.GooglePlayServices.Code.Scanner").And.Contain("Xamarin.AndroidX.Fragment.Ktx").And.Contain("Xamarin.AndroidX.Tracing.Tracing.Ktx");
        project.Replace(group, "").Should().NotContain("Code.Scanner", "no unconditional reference to the scanner");
        // the project file writes its paths with backslashes on every OS
        project.Should().MatchRegex("<Compile Remove=\"Platforms.Android.AndroidQrScanner\\.cs\" />", "without the switch the scanner's source is not even compiled");
    }

    [Fact]
    public void TheScannerIsRegisteredOnlyInTheBuildThatHasIt_AndTheAddPageOffersItOnlyWhenThereIsOne()
    {
        var program = Text("MauiProgram.cs");
        program.Should().Contain("#if BMB_QR_SCANNER");
        program.IndexOf("AndroidQrScanner", StringComparison.Ordinal).Should().BeGreaterThan(program.IndexOf("#if BMB_QR_SCANNER", StringComparison.Ordinal));
        Text("Pages", "BlindAddPage.xaml.cs").Should().Contain("ScanButton.IsVisible = _scanner is not null;");
    }

    [Fact]
    public void TheScanner_ReturnsTextAndNothingMore_ItAddsNothing_AndAsksForNoCameraPermission()
    {
        var scanner = Text("Platforms", "Android", "AndroidQrScanner.cs");

        scanner.Should().Contain("Task<string?> ScanAsync(");
        scanner.Should().NotContain("AddFromInvitationAsync").And.NotContain("AddAsync").And.NotContain("RequestPermissions").And.NotContain("Permission.Camera");
        scanner.Should().Contain("SetBarcodeFormats(Barcode.FormatQrCode)", "only QR codes are looked for");
        Text("Pages", "BlindAddPage.xaml.cs").Should().Contain("TakeIntoBox(await BlindInviteInput.FromScannerAsync(_scanner));", "what the scanner read goes through the length gate into the box and gets the usual preview");
    }
}
