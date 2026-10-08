using System.Xml;
using System.Xml.Linq;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>
/// What the app declares to iOS (Platforms/iOS/Info.plist, Entitlements.plist, PrivacyInfo.xcprivacy): the two background tasks and no
/// other background mode, nothing that loosens App Transport Security, the local-network reason, the call-code link, and a Keychain group
/// of its own.
/// </summary>
public class IosPlistTests
{
    private static string PlatformFile(string name) =>
        Path.Combine(VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindIos", "Platforms", "iOS", name);

    private static XElement Dict(string file)
    {
        using var reader = XmlReader.Create(PlatformFile(file), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        return XDocument.Load(reader).Root!.Element("dict")!;
    }

    /// <summary>The value element that follows &lt;key&gt;name&lt;/key&gt; in a plist dict, or null.</summary>
    private static XElement? Value(XElement dict, string key) =>
        dict.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().First();

    private static List<string> Strings(XElement? array) => array?.Elements("string").Select(s => s.Value).ToList() ?? [];

    [Fact]
    public void TheBackgroundTasks_AreExactlyTheTwoTheAppRegisters_AndNoOtherBackgroundModeIsDeclared()
    {
        var info = Dict("Info.plist");
        Strings(Value(info, "BGTaskSchedulerPermittedIdentifiers"))
            .Should().BeEquivalentTo(IosBackgroundRounds.SyncTaskId, IosBackgroundRounds.WorkTaskId);
        // No audio, VoIP, location or remote-notification tricks to stay alive: a blind copy keeps no socket and listens on no port.
        Strings(Value(info, "UIBackgroundModes")).Should().BeEquivalentTo("fetch", "processing");
    }

    [Fact]
    public void AppTransportSecurity_IsNotLoosened_AndTheLocalNetworkReasonIsGiven()
    {
        var info = Dict("Info.plist");
        // The node is reached through the managed handler and its pin; nothing here opens plain http or arbitrary loads.
        Value(info, "NSAppTransportSecurity").Should().BeNull();
        File.ReadAllText(PlatformFile("Info.plist")).Should().NotContain("NSAllowsArbitraryLoads").And.NotContain("NSExceptionDomains");
        Value(info, "NSLocalNetworkUsageDescription")!.Value.Should().Contain("node");
    }

    [Fact]
    public void TheCallCodeLink_OpensTheApp()
    {
        var types = Value(Dict("Info.plist"), "CFBundleURLTypes")!;
        types.Descendants("string").Select(s => s.Value).Should().Contain("bmb-blind-call");
    }

    [Fact]
    public void TheBundleVersion_IsDerivedFromTheRepositoryVersion_AndNotHardCodedInThePlist()
    {
        var project = File.ReadAllText(Path.Combine(VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindIos", "BeeMemoryBank.BlindIos.csproj"));
        project.Should().Contain("<ApplicationDisplayVersion>$(_BmbCoreVersion)</ApplicationDisplayVersion>")
            .And.Contain("<ApplicationVersion>$([MSBuild]::Add(");
        var plist = File.ReadAllText(PlatformFile("Info.plist"));
        plist.Should().NotContain("CFBundleShortVersionString").And.NotContain("CFBundleVersion");
    }

    [Fact]
    public void TheOnlyEntitlement_IsTheAppsOwnKeychainGroup()
    {
        var entitlements = Dict("Entitlements.plist");
        entitlements.Elements("key").Select(k => k.Value).Should().Equal("keychain-access-groups");
        // Its own group (team prefix + its bundle id), never a group shared with another app, no iCloud.
        Strings(Value(entitlements, "keychain-access-groups")).Should().Equal("$(AppIdentifierPrefix)$(CFBundleIdentifier)");
    }

    [Fact]
    public void ThePrivacyManifest_DeclaresNoTracking_AndTheReasonsOfTheApisTheRuntimeUses()
    {
        var privacy = Dict("PrivacyInfo.xcprivacy");
        Value(privacy, "NSPrivacyTracking")!.Name.LocalName.Should().Be("false");
        var categories = Value(privacy, "NSPrivacyAccessedAPITypes")!.Elements("dict").Select(d => Value(d, "NSPrivacyAccessedAPIType")!.Value);
        categories.Should().BeEquivalentTo("NSPrivacyAccessedAPICategoryFileTimestamp", "NSPrivacyAccessedAPICategorySystemBootTime",
            "NSPrivacyAccessedAPICategoryDiskSpace", "NSPrivacyAccessedAPICategoryUserDefaults");
    }
}
