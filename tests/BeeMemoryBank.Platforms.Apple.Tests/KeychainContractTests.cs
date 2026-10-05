using BeeMemoryBank.Platforms.Apple.Interop;
using BeeMemoryBank.Platforms.Apple.Keychain;

namespace BeeMemoryBank.Platforms.Apple.Tests;

/// <summary>
/// What every user of the Keychain layer relies on and that needs no Mac: the status values it compares against (Security.framework's
/// own OSStatus numbers), the library loading and refusing clearly on any other operating system, and a backend that can be replaced by
/// a fake because it is an interface.
/// </summary>
public class KeychainContractTests
{
    [Fact]
    public void TheStatusValues_AreSecurityFrameworksOwn()
    {
        // errSecSuccess, userCanceledErr, errSecParam, errSecAuthFailed, errSecNoSuchKeychain, errSecInvalidKeychain, errSecDuplicateItem,
        // errSecItemNotFound, errSecInteractionNotAllowed: a store that compares with a wrong number mistakes one failure for another.
        KeychainStatus.Success.Should().Be(0);
        KeychainStatus.UserCanceled.Should().Be(-128);
        KeychainStatus.Param.Should().Be(-50);
        KeychainStatus.AuthFailed.Should().Be(-25293);
        KeychainStatus.NoSuchKeychain.Should().Be(-25294);
        KeychainStatus.InvalidKeychain.Should().Be(-25295);
        KeychainStatus.DuplicateItem.Should().Be(-25299);
        KeychainStatus.ItemNotFound.Should().Be(-25300);
        KeychainStatus.InteractionNotAllowed.Should().Be(-25308);
    }

    [Fact]
    public void TheStatusValues_AreAllDifferent()
    {
        var values = typeof(KeychainStatus).GetFields().Select(f => (int)f.GetRawConstantValue()!).ToList();

        values.Should().OnlyHaveUniqueItems().And.HaveCount(9);
    }

    [NotMacFact]
    public void OffAMac_TheNativeEntryPoints_SayThatTheyNeedMacOS_InsteadOfDllNotFound()
    {
        ((Action)NativeLibraries.RequireMacOS).Should().Throw<PlatformNotSupportedException>();
        ((Action)(() => new SecurityFrameworkKeychain())).Should().Throw<PlatformNotSupportedException>();
        ((Action)(() => new SecurityFrameworkKeychain("/tmp/x.keychain", allowUserInteraction: false))).Should().Throw<PlatformNotSupportedException>();
    }

    [NotMacFact]
    public void OffAMac_DescribingAStatus_IsEmptyText_NotAnException()
    {
        Sec.Describe(KeychainStatus.ItemNotFound).Should().BeEmpty();
        Sec.Describe(KeychainStatus.Success).Should().BeEmpty();
    }

    [Fact]
    public void ACPath_IsNulTerminatedUtf8()
    {
        Sec.CPath("/a/b").Should().Equal(0x2F, 0x61, 0x2F, 0x62, 0x00);
        Sec.CPath("caf" + (char)0xE9).Should().Equal(0x63, 0x61, 0x66, 0xC3, 0xA9, 0x00);
    }

    [Fact]
    public void TheNativeLibraryNames_AreTheSystemFrameworkPaths()
    {
        NativeLibraries.Security.Should().Be("/System/Library/Frameworks/Security.framework/Security");
        NativeLibraries.CoreFoundation.Should().Be("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");
        NativeLibraries.IOKit.Should().Be("/System/Library/Frameworks/IOKit.framework/IOKit");
        NativeLibraries.LibC.Should().Be("libc");
    }

    [Fact]
    public void ABackend_IsAnInterface_SoAStoreCanBeTestedWithAFake()
    {
        typeof(IKeychainBackend).IsInterface.Should().BeTrue();
        typeof(IKeychainBackend).GetMethods().Select(m => m.Name).Should().BeEquivalentTo(["Add", "Update", "Copy", "Delete", "Describe"]);
        typeof(SecurityFrameworkKeychain).Should().Implement<IKeychainBackend>();
        typeof(SecurityFrameworkKeychain).Should().Implement<IDisposable>();
    }
}
