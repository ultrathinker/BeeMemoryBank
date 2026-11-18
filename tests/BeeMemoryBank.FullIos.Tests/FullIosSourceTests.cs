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

        plist["NSLocalNetworkUsageDescription"].Value.Should().Contain("sync directly with your computers on this Wi-Fi");
        plist["NSBonjourServices"].Elements("string").Select(s => s.Value).Should().Equal("_beememorybank._tcp");
        plist["NSFaceIDUsageDescription"].Value.Should().Contain("never stored");
        plist.Should().NotContainKey("UIUserInterfaceStyle", "light and dark follow the system");
        plist.Should().NotContainKey("UIBackgroundModes", "the app runs only the seconds iOS grants a leaving app; it keeps nothing open");
        plist.Should().NotContainKey("NSAppTransportSecurity", "the managed handler pins keys itself; ATS is not loosened");
        plist["CFBundleURLTypes"].Descendants("string").Select(s => s.Value).Should().Contain("bmb-join").And.NotContain("bmb-blind-call");
    }

    /// <summary>
    /// What runs before the unlock on an encrypted phone reads nothing from the closed database. The unlock page asks FullVault
    /// (which reads the boot store's mirror while closed) and resolves no repository itself; and the async void events of the cold start, the
    /// join link and the unlock page catch what they throw - an exception there ended the app before the password could be typed.
    /// </summary>
    [Fact]
    public void Nothing_before_the_unlock_reads_the_database_itself_and_its_async_void_events_catch()
    {
        var unlock = File.ReadAllText(App("Pages", "UnlockPage.xaml.cs"));
        Regex.IsMatch(unlock, @"Get(Required)?Service<I\w+Repository>").Should().BeFalse("the unlock page reads through FullVault, never a repository");

        foreach (var (file, method) in new[] { (App("Pages", "UnlockPage.xaml.cs"), "OnAppearing"), (App("App.xaml.cs"), "OnStart"), (App("App.xaml.cs"), "ShowJoinCode") })
        {
            var source = File.ReadAllText(file);
            var head = Regex.Match(source, @"async void " + method + @"\([^)]*\)\s*\{");
            head.Success.Should().BeTrue($"{method} is in {Path.GetFileName(file)}");
            var depth = 1;
            var i = head.Index + head.Length;
            for (; i < source.Length && depth > 0; i++) depth += source[i] == '{' ? 1 : source[i] == '}' ? -1 : 0;
            source[(head.Index + head.Length)..i].Should().Contain("catch (Exception", $"{method} catches: an async void that throws ends the app");
        }
    }

    /// <summary>
    /// the page reads inside the unlocked shell catch what they throw, and look again after their awaits whether the vault
    /// is still unlocked. A lock that lands mid-read (the idle timer, the background lock, Lock now) closes an encrypted database under the read;
    /// an async void page event that rethrows it ends the app - the cold-start crash, one timing step removed.
    /// </summary>
    [Fact]
    public void The_page_reads_inside_the_shell_survive_a_lock_that_lands_mid_read()
    {
        foreach (var (page, method) in GuardedPageReads)
        {
            var body = MethodBody(File.ReadAllText(App("Pages", page)), method);
            body.Should().Contain("catch (Exception", $"{page} {method} catches: a lock mid-read must not end the app");
            body.Should().Contain("IsUnlocked", $"{page} {method} looks again whether the vault is still unlocked");
        }
    }

    private static readonly (string Page, string Method)[] GuardedPageReads =
    [
        ("SyncPage.xaml.cs", "RefreshAsync"),
        ("SettingsPage.xaml.cs", "OnAppearing"),
        ("TrashPage.xaml.cs", "OnAppearing"),
        ("EditNotePage.xaml.cs", "OnAppearing"),
    ];

    /// <summary>The body of the async method <paramref name="method"/> (braces matched), for the source guards.</summary>
    private static string MethodBody(string source, string method)
    {
        var head = Regex.Match(source, @"async (?:void|Task) " + method + @"\([^)]*\)\s*\{");
        head.Success.Should().BeTrue($"the async method {method} is there");
        var depth = 1;
        var i = head.Index + head.Length;
        for (; i < source.Length && depth > 0; i++) depth += source[i] == '{' ? 1 : source[i] == '}' ? -1 : 0;
        return source[(head.Index + head.Length)..i];
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
        Regex.Matches(store, "Synchronizable = false").Count.Should().BeGreaterThanOrEqualTo(2, "no item may go to the iCloud Keychain");
        store.Should().NotContain("AfterFirstUnlock").And.NotContain("Always");
        // 2.12: the Keychain keeps only the door's secret; the master key is in the vault's quick_unlock door. An earlier version's
        // wrapped master key (unlock-slot-v1) is read once and removed, never written again.
        Regex.Matches(store, @"SecKeyChain\.Add\(").Count.Should().Be(1, "one item is written: the secret behind the biometry");
        store.Should().Contain("Account = SecretAccount,").And.NotContain("Account = LegacySlotAccount").And.NotContain("Account = LegacyKeyAccount");
    }

    [Fact]
    public void TheKeychain_RemovesAnOlderVersionsItems_OnlyAfterTheNewSecretIsStored()
    {
        // a Save that forgot every item before adding the new one turned Face ID off for good when the Keychain refused the add
        // during the one-time conversion. QuickUnlockTests drive the conversion against a fake that keeps this order; this holds the real store to it.
        var store = File.ReadAllText(App("Platforms", "iOS", "KeychainUnlockKeyStore.cs"));
        var save = store[store.IndexOf("public void Save(byte[] secret)", StringComparison.Ordinal)..store.IndexOf("public byte[]? ReadLegacySlot()", StringComparison.Ordinal)];
        save.Should().NotContain("Forget()", "Save never removes the older version's items before the new secret is stored");
        var add = save.IndexOf("Check(SecKeyChain.Add(keyRecord)", StringComparison.Ordinal);
        add.Should().BeGreaterThan(0);
        save.IndexOf("RemoveLegacyItems();", StringComparison.Ordinal).Should().BeGreaterThan(add, "the older version's items go after the add succeeded");
        save.IndexOf("Remove(SecretAccount);", StringComparison.Ordinal).Should().BeInRange(0, add, "only this store's own earlier secret goes before the add");
    }

    [Fact]
    public void TheDisconnectedSentence_IsInBothLanguages_AndTheSyncScreensSayItCalmly()
    {
        // 2.12: when a computer proves that an administrator disconnected this phone, the sync screens say one calm sentence (FullSyncTests hold
        // the state; the screens are iOS-only and cannot run here).
        var en = File.ReadAllText(App("Resources", "Strings", "AppResources.resx"));
        var ru = File.ReadAllText(App("Resources", "Strings", "AppResources.ru.resx"));
        foreach (var key in new[] { "SyncProblemDisconnected", "SyncDisconnectedTitle", "SyncBadgeDisconnected", "SlotRecoveryOwnerFormat", "SlotRecoveryNoOwner", "SlotQuickUnlock" })
        {
            en.Should().Contain($"name=\"{key}\"");
            ru.Should().Contain($"name=\"{key}\"");
        }
        en.Should().Contain(BeeMemoryBank.Sync.ThisDeviceRevokedException.Sentence);
        File.ReadAllText(App("Controls", "SyncText.cs")).Should().Contain("FullSync.ProblemDisconnected => AppStrings.Get(\"SyncProblemDisconnected\")");
        var page = File.ReadAllText(App("Pages", "SyncPage.xaml.cs"));
        page.Should().Contain("DisconnectedBanner.IsVisible = disconnected").And.Contain("!disconnected", "the red banner is not shown for it");
        File.ReadAllText(App("Pages", "SyncPage.xaml")).Should().Contain("x:Name=\"DisconnectedBanner\"");
    }

    [Fact]
    public void AfterFaceIdTurnedItselfOff_ThePasswordUnlockOffersItAgain()
    {
        var page = File.ReadAllText(App("Pages", "UnlockPage.xaml.cs"));
        page.Should().Contain("_quickTurnedOff = true;").And.Contain("if (_quickTurnedOff) _ = _flow.OfferQuickUnlockAgainAsync();");
        // An older Face ID that opened the vault but could not be kept on: the notes, then the same question.
        var unlockedButOff = page[page.IndexOf("case QuickUnlockResult.UnlockedButTurnedOff:", StringComparison.Ordinal)..];
        unlockedButOff[..unlockedButOff.IndexOf("break;", StringComparison.Ordinal)].Should().Contain("_flow.ShowMain();").And.Contain("_ = _flow.OfferQuickUnlockAgainAsync();");
        File.ReadAllText(App("AppFlow.cs")).Should().Contain("public async Task OfferQuickUnlockAgainAsync()").And.Contain("quick.ForgetSecret();");
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
