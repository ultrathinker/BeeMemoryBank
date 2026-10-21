using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// The one failure-text rule of the shared blind core (fix round 2), pinned where it is decided: <see cref="BlindFailureText"/> says the KIND of a failure or a bounded sentence of this code - never the
/// message of an exception - and cleans a text that was stored by an earlier version when it is read back.
/// </summary>
public sealed class BlindFailureTextRuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-appcore-tests", Guid.NewGuid().ToString("N"));

    public BlindFailureTextRuleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static TheoryData<string> Adversarial => FailureCanaries.Names;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    }

    // ---- what is said about an exception -------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Adversarial))]
    public void EveryWayOfSayingAnException_IsFreeOfItsWords(string name)
    {
        var said = new[]
        {
            BlindFailureText.Say(FailureCanaries.Make(name)),
            BlindFailureText.Typed(FailureCanaries.Make(name)),
            BlindFailureText.Sentence(FailureCanaries.Make(name)),
            BlindRunReport.Reason(FailureCanaries.Make(name)),
        };

        foreach (var text in said)
        {
            FailureCanaries.Leaks(text).Should().BeFalse($"'{text}' must say the kind of failure and nothing the exception carried");
            text.Should().NotBeNullOrWhiteSpace().And.NotContain("\n").And.NotContain("\r");
            text.Length.Should().BeLessThan(BlindFailureText.MaxSentence + 60);
        }
    }

    [Theory]
    [MemberData(nameof(Adversarial))]
    public void AnUnexpectedErrorOfARun_IsLoggedAsItsKind_WithNoWordOfTheException_AndTheFileHoldsNone(string name)
    {
        var path = Path.Combine(_dir, "log.jsonl");
        var log = new BlindPhoneLog(path, new FixedClock());

        BlindRunReport.RecordFailure(log, "run", FailureCanaries.Make(name));

        var line = log.LatestStored(10).Should().ContainSingle().Which;
        line.Message.Should().StartWith("Stopped by an unexpected error");
        FailureCanaries.Leaks(line.Message).Should().BeFalse();
        FailureCanaries.Leaks(File.ReadAllText(path)).Should().BeFalse("the file the log is kept in");
    }

    [Fact]
    public void TheMessageOfTheFrameworksOwnTypes_IsNotASentenceOfThisCode()
    {
        // The framework throws these types too, with the path or the parameter in the words.
        BlindFailureText.Say(new UnauthorizedAccessException(@"Access to the path 'C:\secret\x' is denied.")).Should().Be("access was denied");
        BlindFailureText.Say(new InvalidOperationException(@"Could not open C:\secret\x")).Should().Be("an unexpected problem (InvalidOperationException)");
        BlindFailureText.Say(new ArgumentException("The path '/private/var/x' is not valid. (Parameter 'path')")).Should().Be("an unexpected problem (ArgumentException)");
        BlindFailureText.Say(new InvalidOperationException("The key bmb_blind_seed_v1 is gone.")).Should().Be("an unexpected problem (InvalidOperationException)");
        // Not even the words of this code, when the exception does not say that this code threw it on purpose: the TYPE proves nothing about the message.
        BlindFailureText.Say(new InvalidOperationException("The blind phone identity has not been recorded.")).Should().Be("an unexpected problem (InvalidOperationException)");
        // The sentences this code wrote on purpose are still said.
        BlindFailureText.Say(new BlindSentenceException("The blind phone identity has not been recorded.")).Should().Be("The blind phone identity has not been recorded.");
        BlindFailureText.Say(new BlindSentenceException("Choose a smaller number.")).Should().Be("Choose a smaller number.");
    }

    public static TheoryData<string> Provenance => new("invalid-operation-url-and-path", "argument-url-and-path", "unauthorized-url-and-path", "invalid-operation-plain",
        "wrapped-invalid-operation", "aggregate-of-invalid-operations", "target-invocation-of-argument");

    [Theory]
    [MemberData(nameof(Provenance))]
    public void AnInvalidOperationAnArgumentOrAnUnauthorizedAccess_ThatThisCodeDidNotThrowOnPurpose_IsToldByItsKind_ThroughAnyWrapping(string name)
    {
        var said = BlindFailureText.Say(FailureCanaries.Make(name));

        FailureCanaries.Leaks(said).Should().BeFalse($"'{said}' must hold nothing of the message");
        said.Should().MatchRegex("^(an unexpected problem \\((InvalidOperation|Argument)Exception\\)|access was denied)$", "the kind of failure and the name of its type, nothing else");
    }

    [Fact]
    public void ASentenceOfThisCode_ThrownOnPurpose_IsSaidAsItIs_AlsoAsTheInnerFailureOfAWrapper_ButNeverTheWordsOfTheWrapper()
    {
        BlindFailureText.Say(new BlindSentenceException("Choose a smaller number.")).Should().Be("Choose a smaller number.");
        BlindFailureText.Say(new AggregateException("many " + FailureCanaries.Canary, new BlindSentenceException("Choose a smaller number."))).Should().Be("Choose a smaller number.");
        BlindFailureText.Say(new System.Reflection.TargetInvocationException(new BlindSentenceException("Choose a smaller number."))).Should().Be("Choose a smaller number.");
        // A wrapper that is not itself a sentence of this code says its kind: what it wraps is not read.
        BlindFailureText.Say(new InvalidOperationException("wrapper " + FailureCanaries.Canary, new BlindSentenceException("Choose a smaller number."))).Should().Be("an unexpected problem (InvalidOperationException)");
        // A sentence of this code is still not said when it names a place (the code would have written a path or an address into it).
        BlindFailureText.Say(new BlindSentenceException("Could not open https://x.example/?token=" + FailureCanaries.Canary)).Should().Be("an unexpected problem (BlindSentenceException)");
    }

    [Fact]
    public void TheTypesThisCodeThrowsToBeRead_AreSaidAsTheirSentence_UnlessTheyNameAPlace()
    {
        BlindFailureText.Say(new BlindFileSentenceException("The account's folder is a link; it was not followed or deleted. Remove the link by hand.")).Should().Contain("is a link");
        BlindFailureText.Say(new BlindRegistryDamagedException("accounts.json is damaged and cannot be read. Nothing was started or changed.")).Should().StartWith("accounts.json is damaged");
        BlindFailureText.Say(new BlindNotEnoughRoomException("Not enough room for a backup: about 120 MiB are needed, 80 MiB are free.")).Should().Contain("Not enough room");
        BlindFailureText.Say(BlindOwnSentence.Mark(new InvalidDataException("The last replica install failed 2 time(s) (IOException); next attempt after 2026-10-21 12:00:00Z."))).Should().Contain("next attempt after");
        BlindFailureText.Say(new BlindFeaturePendingException("the first download")).Should().Contain("waiting for the first download");

        // The same types with a place in the words are told as the kind they are.
        BlindFailureText.Say(new BlindFileSentenceException(@"Could not remove C:\secret\x")).Should().Be("a file or the storage of this device could not be read or written");
        BlindFailureText.Say(new BlindNotEnoughRoomException("No room on /private/var/x")).Should().Be("a file or the storage of this device could not be read or written");
        BlindFailureText.Say(BlindOwnSentence.Mark(new InvalidDataException(@"Replica manifest file is missing: C:\secret\x"))).Should().Be("a package or file could not be read or verified");
        BlindFailureText.Say(new InvalidDataException("The last replica install failed 2 time(s) (IOException); next attempt after 2026-10-21 12:00:00Z.")).Should()
            .Be("a package or file could not be read or verified", "an InvalidDataException that is not marked is never said by its words");
    }

    [Fact]
    public void ADamagedPackage_IsToldAsSuch_NotAsStoredData()
    {
        BlindFailureText.Say(new InvalidDataException("the baseline's signature does not verify " + FailureCanaries.Canary)).Should().Be("a package or file could not be read or verified");
        BlindFailureText.Say(new FormatException("bad base64 " + FailureCanaries.Canary)).Should().Be("stored data could not be read");
    }

    [Fact]
    public void ARepeatedWrapperOfTheSameFailure_SaysItOnce()
    {
        var said = BlindFailureText.Say(new AggregateException(new TypeInitializationException("X", new CryptographicException("key " + FailureCanaries.SecretPath))));

        said.Should().Be("a key or encrypted data could not be used");
    }

    [Fact]
    public void WithTheTypeInFront_OnlyWhenTheSentenceDoesNotNameItAlready()
    {
        BlindFailureText.Typed(new NotSupportedException("x")).Should().Be("an unexpected problem (NotSupportedException)");
        BlindFailureText.Typed(new IOException("x")).Should().Be("IOException: a file or the storage of this device could not be read or written");
        BlindFailureText.WithoutFullStop("a sentence.").Should().Be("a sentence");
        BlindFailureText.WithoutFullStop("a cut sentence...").Should().Be("a cut sentence...");
        BlindFailureText.Sentence(new IOException("x")).Should().EndWith("written.");
        BlindFailureText.Sentence(new BlindSentenceException("Already a sentence.")).Should().Be("Already a sentence.");
    }

    // ---- what is read back from storage --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("First load: starts after pairing.", "First load: starts after pairing.")]
    [InlineData("a\r\nb\tc", "a b c")]
    [InlineData("tab\u0007bell", "tabbell")]
    public void AStoredTextThatIsPlain_IsPassedOnAsOneCleanLine(string? stored, string shown) =>
        BlindFailureText.ForDisplay(stored).Should().Be(shown);

    [Theory]
    [InlineData(@"Could not find file 'C:\Users\x\blind.db'.")]
    [InlineData(@"C:\secret\bmb_blind_seed_v1")]
    [InlineData("Could not find file '/data/user/0/app/files/x.db'.")]
    [InlineData("An error occurred while sending the request to https://192.168.1.5:5610/api.")]
    [InlineData("the key BMB_BLIND_seed_v1 was rejected")]
    [InlineData("two lines\r\nsecond: /var/x")]
    public void AStoredTextThatNamesAFileAnAddressOrAKey_IsReplacedByTheGenericSentence(string stored) =>
        BlindFailureText.ForDisplay(stored).Should().Be(BlindFailureText.StoredTextHidden);

    [Fact]
    public void AStoredText_IsBounded_AndTheFilterIsIdempotent()
    {
        var long1 = BlindFailureText.ForDisplay(new string('x', 5000));
        long1.Length.Should().Be(BlindFailureText.MaxStored + 3);
        long1.Should().EndWith("...");

        foreach (var text in new[] { "plain", @"C:\x", new string('y', 900), BlindFailureText.StoredTextHidden })
            BlindFailureText.ForDisplay(BlindFailureText.ForDisplay(text)).Should().Be(BlindFailureText.ForDisplay(text));
        BlindFailureText.LooksLikeALocation(BlindFailureText.StoredTextHidden).Should().BeFalse("the sentence that replaces a text must pass the filter itself");
    }

    [Fact]
    public void TheLog_ShowsWhatThisVersionWrote_HidesWhatItDidNot_AndKeepsTheStoredLineForTheCodeThatCompares()
    {
        var path = Path.Combine(_dir, "log.jsonl");
        var at = new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
        // The lines of 2.6.4 had no stamp (the provenance rule is pinned in BlindStoredLineProvenanceTests).
        File.WriteAllLines(path,
        [
            JsonSerializer.Serialize(new { At = at, Kind = "pairing", Message = "Paired: calls https://127.0.0.1:5610 (node abc)." }),
            JsonSerializer.Serialize(new { At = at, Kind = "backup", Message = @"Backup failed: Could not find file 'C:\x\y.bmbbackup'" }),
            JsonSerializer.Serialize(new { At = at, Kind = "load", Message = "First load finished." }),
        ]);
        var log = new BlindPhoneLog(path, new FixedClock());
        log.Add("load", "First load finished.");

        var shown = log.Latest(10);
        var stored = log.LatestStored(10);

        shown.Select(e => e.Message).Should().Equal("First load finished.", BlindFailureText.StoredTextHidden, BlindFailureText.StoredTextHidden, BlindFailureText.StoredTextHidden);
        stored.Select(e => e.Message).Should().Contain(@"Backup failed: Could not find file 'C:\x\y.bmbbackup'");
    }
}
