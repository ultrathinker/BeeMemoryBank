using System.Text.RegularExpressions;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Boundary;
using BeeMemoryBank.Busy;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>
/// Fix round 3, item 2 (final review, shared MAJOR-1): every failure a page of the iPhone head shows goes through the shared busy helper's <see cref="BusyAction.Describer"/>, which the app sets to its
/// closed failure text before anything else. The REAL helper (<c>BusyAction.cs</c>, compiled into this project as the app links it) is driven with hostile exceptions; the pages' source is held to
/// "a page does not format an exception". The helper keeps its in-flight set in a static and the Describer is a static too, so these tests share one class and run one after the other.
/// </summary>
[Collection("BusyActionRoute")]
public sealed class IosBusyRouteCanaryTests
{
    private const string Canary = "canary-123";
    private const string SecretPath = "/private/var/mobile/Containers/Data/Application/AAAA/Library/Application Support/BeeMemoryBankBlind/accounts/8f3a1b2c3d4e5f60718293a4b5c6d7e8";
    private const string AccountId = "8f3a1b2c3d4e5f60718293a4b5c6d7e8";

    private static readonly string[] Needles =
    [
        Canary, "canary-host", "token=", "https://", "/private/var", "Containers", "Application Support", "BeeMemoryBankBlind", AccountId,
        "identity-seed", "pairing-secret", "backup-key", "bmb_blind_", "Access to the path", "being used by another process", @"C:\",
    ];

    public static TheoryData<string> Names => new(
        "io-path", "io-inner-path", "http-uri-token", "aggregate", "crypto", "unauthorized", "invalid-operation-with-path", "invalid-operation-uri-token", "argument-with-path",
        "unknown", "wrapper-with-inner", "type-initialization", "http-wrapped-in-io", "invalid-data", "aggregate-of-invalid-operations", "target-invocation",
        "keychain-with-item-name", "keychain-wrapped-in-aggregate", "keychain-without-status", "keychain-in-invalid-operation");

    private static Exception Make(string name) => name switch
    {
        "io-path" => new IOException($"The process cannot access the file '{SecretPath}' because it is being used by another process. {Canary}"),
        "io-inner-path" => new IOException("The file could not be written.", new IOException($"{SecretPath} {Canary}")),
        "http-uri-token" => new HttpRequestException($"An error occurred while sending the request to https://canary-host.invalid/api?token={Canary}", new IOException(SecretPath)),
        "aggregate" => new AggregateException($"One or more errors occurred. {Canary}", new IOException($"{SecretPath} {Canary}"), new FormatException($"bad {Canary}")),
        "crypto" => new System.Security.Cryptography.CryptographicException($"The key {SecretPath} could not be loaded. {Canary}"),
        "unauthorized" => new UnauthorizedAccessException($"Access to the path '{SecretPath}' is denied. {Canary}"),
        "invalid-operation-with-path" => new InvalidOperationException($"Could not open {SecretPath} {Canary}"),
        "invalid-operation-uri-token" => new InvalidOperationException($"https://canary-host.invalid/?token={Canary} {SecretPath}"),
        "argument-with-path" => new ArgumentException($"The path '{SecretPath}' is not valid. {Canary} (Parameter 'path')"),
        "unknown" => new NotSupportedException($"{Canary} {SecretPath}"),
        "wrapper-with-inner" => new InvalidOperationException("The step failed.", new IOException($"{SecretPath} {Canary}")),
        "type-initialization" => new TypeInitializationException("Some.Type", new IOException($"{SecretPath} {Canary}")),
        "http-wrapped-in-io" => new IOException("The step failed.", new HttpRequestException($"https://canary-host.invalid/x?token={Canary}")),
        "invalid-data" => new InvalidDataException($"The package {SecretPath} is damaged. {Canary}"),
        "aggregate-of-invalid-operations" => new AggregateException("many", new InvalidOperationException($"https://canary-host.invalid/?token={Canary} {SecretPath}"), new ArgumentException($"account {AccountId}-{Canary}")),
        "target-invocation" => new System.Reflection.TargetInvocationException(new ArgumentException($"https://canary-host.invalid/?token={Canary} {SecretPath}")),
        // the exception the Keychain store throws, with a hostile message that names the item (the message the store used to write: the account's id is in the item's name)
        "keychain-with-item-name" => new IosKeychainException($"The Keychain did not answer for {AccountId}/identity-seed-v1 (OSStatus -25308). {Canary}", -25308),
        "keychain-wrapped-in-aggregate" => new AggregateException($"Not every Keychain item could be removed: {AccountId}/pairing-secret-v1 {Canary}",
            new IosKeychainException($"The Keychain did not remove {AccountId}/pairing-secret-v1 (OSStatus -25291).", -25291)),
        "keychain-without-status" => new IosKeychainException($"The Keychain item {AccountId}/backup-key-v1 is not one this app wrote. {Canary}"),
        "keychain-in-invalid-operation" => new InvalidOperationException("The start failed.", new IosKeychainException($"{AccountId}/identity-seed-v1 {Canary}", -34018)),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>What the helper put on the screen for a failed action: through its real RunAsync, with a result callback in place of a label or a dialog (nothing else of the route is replaced).</summary>
    private static async Task<(bool Ran, bool? Ok, string? Text)> ShownFor(Exception failure)
    {
        bool? ok = null;
        string? text = null;
        var ran = await BusyAction.RunAsync(null, () => Task.FromException(failure), new BusyOptions { Result = (success, said) => { ok = success; text = said; } });
        return (ran, ok, text);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task EveryHostileFailure_OfAnAction_IsToldByTheClosedSentenceOnly_ThroughTheRealBusyRoute(string name)
    {
        IosFailureSetup.Apply();

        var (ran, ok, text) = await ShownFor(Make(name));

        ran.Should().BeFalse("the action failed");
        ok.Should().BeFalse();
        text.Should().NotBeNullOrWhiteSpace();
        foreach (var needle in Needles) text.Should().NotContain(needle, $"the closed rule says a failure by its kind, and an item or a place not at all: {name}");
        text.Should().Be(IosFailureText.Say(Make(name)), "what the screen got is exactly the head's closed text");
        BusyAction.Describe(Make(name)).Should().Be(text, "a page that asks for the story of a failure gets the same sentence");
    }

    [Fact]
    public async Task TheKeychainStoresOwnFailures_AreToldWithTheStatusNumberAndFixedWordsOnly_ThroughTheRealBusyRoute()
    {
        IosFailureSetup.Apply();
        var entry = new BlindAccountEntry(AccountId, BlindAccountLayouts.Dir, "Anna");
        var cases = new List<(string What, Action Act)>();
        foreach (var status in new[] { IosKeychainStatus.InteractionNotAllowed, IosKeychainStatus.MissingEntitlement, -25291 })
        {
            var keychain = new IosKeychainSecretStoreTests.FakeKeychain { FailWith = status };
            var store = new IosKeychainSecretStore(keychain, entry);
            cases.Add(("save", () => store.SaveIdentitySeed([1, 2, 3])));
            cases.Add(("load", () => store.LoadBackupKey()));
            cases.Add(("clear one", () => store.ClearPairingSecret()));
            cases.Add(("clear all", () => store.Clear()));
        }
        var altered = new IosKeychainSecretStoreTests.FakeKeychain();
        var other = new IosKeychainSecretStore(altered, entry);
        other.SaveIdentitySeed([9, 9, 9]);
        foreach (var key in altered.Items.Keys.ToList()) altered.Items[key] = [1, 0, 0, 0];      // an item that is not what this app wrote
        cases.Add(("not ours", () => other.LoadIdentitySeed()));

        foreach (var (what, act) in cases)
        {
            var failure = Thrown(act, what);
            var (_, _, text) = await ShownFor(failure);
            foreach (var needle in Needles) text.Should().NotContain(needle, what);
            text.Should().NotBeNullOrWhiteSpace().And.Contain("Keychain", what + ": the sentence says what failed");
            if (failure is IosKeychainException { Status: not 0 } or AggregateException) text.Should().MatchRegex(@"OSStatus -?\d+", what + ": the status number is the detail");
        }
    }

    private static Exception Thrown(Action act, string what)
    {
        try { act(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("nothing was thrown: " + what);
    }

    [Fact]
    public void TheKeychainExceptions_AreMarkedOwnSentences_WithoutAnItemName()
    {
        var keychain = new IosKeychainSecretStoreTests.FakeKeychain { FailWith = IosKeychainStatus.InteractionNotAllowed };
        var store = new IosKeychainSecretStore(keychain, new BlindAccountEntry(AccountId, BlindAccountLayouts.Dir, "Anna"));

        var ex = FluentActions.Invoking(() => store.LoadIdentitySeed()).Should().Throw<IosKeychainException>().Which;

        ex.Should().BeAssignableTo<BeeMemoryBank.Sync.Blind.IBlindOwnSentence>("the closed failure text says a marked sentence as it is");
        ex.Message.Should().NotContain(AccountId).And.NotContain("identity-seed").And.NotContain("/");
        BlindFailureText.Say(ex).Should().Be(ex.Message, "told by the shared rule as well as by the head's: the same words, no matter which route reads it");
    }

    [Fact]
    public async Task ASuccessAndACancelledAction_ShowNothingOfAnException()
    {
        IosFailureSetup.Apply();
        string? shown = null;
        var cancelled = await BusyAction.RunAsync(null, () => Task.FromCanceled(new CancellationToken(true)), new BusyOptions { Result = (_, said) => shown = said });
        var ok = await BusyAction.RunAsync(null, () => Task.CompletedTask, new BusyOptions { Result = (_, said) => shown = said });

        cancelled.Should().BeFalse("a stopped action is not reported");
        ok.Should().BeTrue();
        shown.Should().BeNull();
    }

    [Fact]
    public async Task WithoutTheSetup_TheSharedHelpersDefaultWouldShowTheWords_WhichIsWhyTheAppSetsItFirstThing()
    {
        // the proof that the canary above is sensitive: the same route with no Describer (what an app that forgot the setup gets) says the local failure's own text
        var before = BusyAction.Describer;
        BusyAction.Describer = null;
        try
        {
            var (_, _, text) = await ShownFor(new IOException($"cannot read {SecretPath} {Canary}"));

            text.Should().Contain(Canary, "the default story of the helper is the text of a local failure");
        }
        finally
        {
            BusyAction.Describer = before;
        }
    }

    [Fact]
    public void TheSetup_GivesTheHelperTheHeadsClosedText()
    {
        IosFailureSetup.Apply();

        BusyAction.Describer.Should().NotBeNull();
        BusyAction.Describer!(new IOException(Canary)).Should().Be(IosFailureText.Say(new IOException(Canary)));
        BusyAction.Describer(new IosKeychainException("x", -25308)).Should().Be("the Keychain did not answer (OSStatus -25308)");
    }
}

[CollectionDefinition("BusyActionRoute", DisableParallelization = true)]
public sealed class BusyActionRouteCollection;

/// <summary>The source of the pages: the registration of the closed text, and "a page does not format an exception".</summary>
public sealed class IosPageRouteGuardTests
{
    private static readonly string Head = Path.Combine(VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindIos");

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Head, .. parts])).ReplaceLineEndings("\n");

    private static readonly string[] Pages = ["BlindHomePage.xaml.cs", "AccountsPage.xaml.cs", "AddAccountPage.xaml.cs"];

    [Fact]
    public void TheApp_SetsTheClosedFailureTextOnTheSharedBusyHelper_BeforeAnythingElseAtStart()
    {
        var program = Source("MauiProgram.cs");
        var create = program[program.IndexOf("public static MauiApp CreateMauiApp()", StringComparison.Ordinal)..];
        var apply = create.IndexOf("IosFailureSetup.Apply();", StringComparison.Ordinal);

        apply.Should().BeGreaterThan(-1, "an app that never sets a Describer gets the helper's default, which says the text of a local failure");
        apply.Should().BeLessThan(create.IndexOf("MauiApp.CreateBuilder()", StringComparison.Ordinal), "set before anything is composed, so before any page can run an action");
        apply.Should().BeLessThan(create.IndexOf("builder.Build()", StringComparison.Ordinal));
        Regex.Matches(program, @"Describer\s*=").Count.Should().Be(0, "the registration is IosFailureSetup's, in one place");
        Source("Services", "IosFailureSetup.cs").Should().Contain("BusyAction.Describer = IosFailureText.Say;");
    }

    [Fact]
    public void TheSharedBusyHelpersRoute_TellsEveryFailureThroughDescribe_WhichUsesTheAppsDescriber()
    {
        var helper = File.ReadAllText(Path.Combine(VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.Mobile", "Services", "BusyAction.cs")).ReplaceLineEndings("\n");

        helper.Should().Contain("public static string Describe(Exception ex) => (Describer ?? BeeMemoryBank.Sync.FailureText.Story)(ex);");
        helper.Should().Contain("await ShowResultAsync(options, ok: false, Describe(ex));", "the catch of the helper shows nothing but Describe(ex)");
        Regex.Matches(helper, @"ShowResultAsync\(options, ok: false,").Count.Should().Be(1, "one failure route");
    }

    /// <summary>
    /// What a page may do with a failure: nothing of its own. A page does not format an exception (the closed text is the busy helper's Describer, set at start), does not call a formatter directly,
    /// and does not throw a sentence as a plain exception. Returns what breaks that.
    /// </summary>
    internal static IReadOnlyList<string> PageFormatsAnExceptionItself(string source)
    {
        var code = Regex.Replace(source, @"(?m)(^|\s)//.*$", "$1");
        var found = new List<string>();
        foreach (var (token, what) in new[]
                 {
                     ("IosFailureText.", "calls the head's formatter itself (the busy helper's Describer is the one route: BusyAction.Describe)"),
                     ("BlindFailureText.", "calls the shared formatter itself"),
                     ("FailureText.", "calls a failure formatter itself"),
                     ("BlindRunReport.", "builds text from an exception with the run report"),
                     ("PeerContactLog", "formats a failure of a call to another device itself"),
                     ("new InvalidOperationException(", "throws a sentence as an unmarked exception"),
                     ("new ArgumentException(", "throws a sentence as an unmarked exception"),
                 })
            if (code.Contains(token, StringComparison.Ordinal)) found.Add(what);
        return found;
    }

    /// <summary>
    /// What a page does with the variable of a catch clause: it may hand it to the busy helper's story (<c>BusyAction.Describe(ex)</c>), to the shared status sentence
    /// (<c>BlindHomeView.StatusFailedText(ex)</c>) or to the page's own <c>ShowProblem(ex)</c> (which uses nothing else) - never its message, its text, its inner exceptions, its type, or an
    /// interpolation of it. Fixed sentences need no variable at all. Returns what breaks that.
    /// </summary>
    internal static IReadOnlyList<string> ExceptionTextViolations(string source)
    {
        var found = new List<string>();
        foreach (Match catchClause in Regex.Matches(source, @"catch\s*\(\s*[\w.]+\s+(?<var>\w+)\s*\)"))
        {
            var open = source.IndexOf('{', catchClause.Index + catchClause.Length);
            if (open < 0) continue;
            var depth = 0;
            var close = open;
            for (; close < source.Length; close++)
            {
                if (source[close] == '{') depth++;
                else if (source[close] == '}' && --depth == 0) break;
            }
            var block = source[open..Math.Min(close + 1, source.Length)];
            var name = Regex.Escape(catchClause.Groups["var"].Value);
            foreach (Match use in Regex.Matches(block, $@"\b{name}\b"))
            {
                var before = block[Math.Max(0, use.Index - 40)..use.Index];
                var after = block[(use.Index + use.Length)..Math.Min(block.Length, use.Index + use.Length + 2)];
                var allowed = after.StartsWith(')') && Regex.IsMatch(before, @"(BusyAction\.Describe|BlindHomeView\.StatusFailedText|ShowProblem)\($");
                if (!allowed) found.Add($"uses '{use.Value}' as {block[Math.Max(0, use.Index - 24)..Math.Min(block.Length, use.Index + use.Length + 12)].ReplaceLineEndings(" ").Trim()}");
            }
        }
        return found;
    }

    [Fact]
    public void ThePageRouteGuard_SeesAPageThatFormatsAFailureItself_AndLetsTheBusyHelpersRouteThrough()
    {
        PageFormatsAnExceptionItself("Label.Text = IosFailureText.Say(ex);").Should().NotBeEmpty();
        PageFormatsAnExceptionItself("Label.Text = BlindFailureText.Say(ex);").Should().NotBeEmpty();
        PageFormatsAnExceptionItself("Label.Text = BeeMemoryBank.Sync.FailureText.Story(ex);").Should().NotBeEmpty();
        PageFormatsAnExceptionItself("Log(BlindRunReport.Reason(ex));").Should().NotBeEmpty();
        PageFormatsAnExceptionItself("throw new InvalidOperationException(refused);").Should().NotBeEmpty();
        PageFormatsAnExceptionItself("Label.Text = PeerContactLog.Describe(ex);").Should().NotBeEmpty();
        PageFormatsAnExceptionItself("await TellAsync(\"x\", BusyAction.Describe(ex)); // IosFailureText.Say is the helper's").Should().BeEmpty();

        foreach (var offence in new[]
                 {
                     "try { } catch (Exception ex) { Label.Text = ex.Message; }",
                     "try { } catch (Exception ex) { Label.Text = $\"failed: {ex}\"; }",
                     "try { } catch (Exception ex) { Label.Text = ex.ToString(); }",
                     "try { } catch (Exception ex) { Label.Text = ex.GetType().Name; }",
                     "try { } catch (Exception ex) { Label.Text = ex.InnerException?.Message; }",
                     "try { } catch (Exception ex) { Notice(\"x: \" + ex); }",
                     "try { } catch (Exception error) { Label.Text = BusyAction.Describe(error.InnerException!); }",
                 })
            ExceptionTextViolations(offence).Should().NotBeEmpty("the guard must refuse: " + offence);
        foreach (var clean in new[]
                 {
                     "try { } catch (Exception ex) { Label.Text = BusyAction.Describe(ex); }",
                     "try { } catch (Exception ex) { StatusErrorLabel.Text = BlindHomeView.StatusFailedText(ex); }",
                     "try { } catch (Exception ex) { ShowProblem(ex); }",
                     "try { } catch (Exception) { /* the page is closing */ }",
                     "try { } catch (KeyNotFoundException) { Label.Text = \"fixed\"; }",
                 })
            ExceptionTextViolations(clean).Should().BeEmpty("the guard must let the clean shape through: " + clean);
    }

    [Fact]
    public void NoIPhonePage_FormatsAFailureItself_EveryRouteIsTheBusyHelpersDescribe()
    {
        foreach (var file in Pages)
        {
            var source = Source("Pages", file);
            PageFormatsAnExceptionItself(source).Should().BeEmpty($"{file}: the page leaves every failure to the busy helper (closed text) or says a fixed sentence");
            ExceptionTextViolations(source).Should().BeEmpty($"{file}: the variable of a catch clause goes to BusyAction.Describe / BlindHomeView.StatusFailedText and nowhere else");
        }
    }

    [Fact]
    public void EveryBusyRoute_OfAPage_LeavesTheFailureToTheHelper_ACustomResultNeverPrintsAnythingElse()
    {
        foreach (var file in Pages)
        {
            var code = Regex.Replace(Source("Pages", file), @"(?m)(^|\s)//.*$", "$1");
            // a failure of the work is reported by the helper through Describe; a page may take the text of a custom Result only as the closed sentence it was given, in a sentence of its own
            foreach (Match result in Regex.Matches(code, @"Result\s*=\s*\((?<ok>\w+),\s*(?<text>\w+)\)\s*=>\s*\{"))
            {
                var open = result.Index + result.Length - 1;
                var depth = 0;
                var close = open;
                for (; close < code.Length; close++)
                {
                    if (code[close] == '{') depth++;
                    else if (code[close] == '}' && --depth == 0) break;
                }
                var body = code[open..Math.Min(close + 1, code.Length)];
                var text = result.Groups["text"].Value;
                body.Should().NotMatchRegex($@"{text}\.(Substring|Split|Replace|Trim)|\b(ex|exception)\b|\.Message\b", $"{file}: a Result callback shows the closed sentence it got, and builds nothing from an exception");
            }
        }
        // the helper's own failure dialog and label need no formatter of the page; nothing in a page may switch the Describer off or replace it
        foreach (var file in Pages) Source("Pages", file).Should().NotContain("Describer");
    }
}
