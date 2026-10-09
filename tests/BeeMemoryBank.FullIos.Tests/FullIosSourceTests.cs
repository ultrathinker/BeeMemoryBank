using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>What the iPhone app declares to iOS, and two rules of its sources that no unit test can reach on Windows.</summary>
public class FullIosSourceTests
{
    private static string App(params string[] parts) => Path.Combine([FullIosBoundaryTests.RepoRoot(), "mobile", "BeeMemoryBank.FullIos", .. parts]);

    private static Dictionary<string, XElement> Plist(string file)
    {
        var dict = XDocument.Load(file).Root!.Element("dict")!.Elements().ToList();
        var map = new Dictionary<string, XElement>();
        for (var i = 0; i + 1 < dict.Count; i += 2) map[dict[i].Value] = dict[i + 1];
        return map;
    }

    [Fact]
    public void InfoPlist_ExplainsTheLocalNetworkAndFaceId_FollowsTheSystemsAppearance_AndAsksForNoBackgroundMode()
    {
        var plist = Plist(App("Platforms", "iOS", "Info.plist"));

        plist["NSLocalNetworkUsageDescription"].Value.Should().Contain("home network");
        plist["NSFaceIDUsageDescription"].Value.Should().Contain("never stored");
        plist.Should().NotContainKey("UIUserInterfaceStyle", "light and dark follow the system");
        plist.Should().NotContainKey("UIBackgroundModes", "the app runs only the seconds iOS grants a leaving app; it keeps nothing open");
        plist.Should().NotContainKey("NSAppTransportSecurity", "the managed handler pins keys itself; ATS is not loosened");
        plist["CFBundleURLTypes"].Descendants("string").Select(s => s.Value).Should().Contain("bmb-join").And.NotContain("bmb-blind-call");
    }

    [Fact]
    public void Entitlements_AreTheAppsOwnKeychainGroupOnly()
    {
        var plist = Plist(App("Platforms", "iOS", "Entitlements.plist"));
        plist.Keys.Should().Equal("keychain-access-groups");
        plist["keychain-access-groups"].Elements("string").Select(s => s.Value).Should().Equal("$(AppIdentifierPrefix)$(CFBundleIdentifier)");
    }

    [Fact]
    public void ThePrivacyManifest_DeclaresNoTrackingAndNoCollectedData()
    {
        var plist = Plist(App("Platforms", "iOS", "PrivacyInfo.xcprivacy"));
        plist["NSPrivacyTracking"].Name.LocalName.Should().Be("false");
        plist["NSPrivacyCollectedDataTypes"].Elements().Should().BeEmpty();
    }

    [Fact]
    public void TheQuickUnlockKey_IsBehindTheCurrentBiometry_OnThisDeviceOnly()
    {
        var store = File.ReadAllText(App("Platforms", "iOS", "KeychainUnlockKeyStore.cs"));
        store.Should().Contain("new SecAccessControl(SecAccessible.WhenPasscodeSetThisDeviceOnly, SecAccessControlCreateFlags.BiometryCurrentSet)");
        store.Should().Contain("Accessible = SecAccessible.WhenUnlockedThisDeviceOnly");
        Regex.Matches(store, "Synchronizable = false").Count.Should().BeGreaterThanOrEqualTo(3, "no item may go to the iCloud Keychain");
        store.Should().NotContain("AfterFirstUnlock").And.NotContain("Always");
    }

    // The join by code recording the code's pin on the node's row is tested by running the join: JoinByCodePinTests.

    [Fact]
    public void EveryPageThatReadsAPasswordField_EmptiesItThroughSecretFields_NotOnlyOnSuccess()
    {
        // SecretFieldsTests holds what the helper does on every way out; this holds that the screens go through it (the controls are
        // iOS-only, so the pages themselves cannot run here). A page that read a field and cleared it by hand on its success path is the defect.
        var read = 0;
        foreach (var file in Directory.GetFiles(App("Pages"), "*.xaml.cs"))
        {
            var source = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            foreach (var field in new[] { "PasswordEntry", "PassphraseEntry", "RepeatEntry" })
            {
                if (!Regex.IsMatch(source, field + @"\.Text\s*(\?\?|,|\))|=\s*" + field + @"\.Text")) continue;
                read++;
                source.Should().Contain("SecretFields.RunAsync", $"{name} reads {field}");
                source.Should().Contain(field + @".Text = """"", $"{name} empties {field}");
                // Not as a statement of its own after the awaited work, where an exception skips it: only as the helper's clear action.
                Regex.IsMatch(source, @"^\s*" + field + @"\.Text = (\w+\.Text = )?"""";", RegexOptions.Multiline)
                    .Should().BeFalse($"{name} empties {field} as a statement of its own, which an exception skips");
            }
        }
        read.Should().BeGreaterThanOrEqualTo(4, "the join, create, unlock and protected-note screens");
    }

    [Fact]
    public void NoNotePage_LoadsAnythingFromTheNetwork()
    {
        // The note view gets its HTML from NoteHtml (no script, CSP default-src 'none') and nothing else: no page sets a URL source.
        foreach (var file in Directory.GetFiles(App("Pages"), "*.cs"))
            File.ReadAllText(file).Should().NotContain("UrlWebViewSource", Path.GetFileName(file));
        File.ReadAllText(App("Pages", "NotePage.xaml.cs")).Should().Contain("new HtmlWebViewSource { Html = html }");
    }
}
