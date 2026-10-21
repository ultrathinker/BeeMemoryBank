using System.Security.Cryptography;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// What a head says about a caught exception: by the TYPE of the failure, never by its message. A failure that came through the HTTP stack (its message holds the address that was dialled or the
/// redirect target the other side chose) is the kind from the closed set, however many exceptions wrap it; a local failure is a bounded sentence of this code or just its kind.
/// </summary>
public sealed class BlindFailureTextTests
{
    private const string Canary = "https://canary-host.invalid/redirect?token=CANARY-TOKEN-9f3a";

    public static TheoryData<Exception> RemoteFailures => new()
    {
        new HttpRequestException("Request to " + Canary + " was redirected (302) to " + Canary + "; redirects are not followed.", null, System.Net.HttpStatusCode.Found),
        new HttpRequestException("Cleartext HTTP is forbidden for blind node communications: " + Canary),
        new HttpRequestException("This connection only calls pinned.test:5610; a request to " + Canary + " was refused."),
        new Exception("wrapper " + Canary, new HttpRequestException(Canary)),
        new InvalidOperationException("wrapper " + Canary, new HttpRequestException(Canary, new System.Net.Sockets.SocketException(10061))),
        new AggregateException("many " + Canary, new IOException("io"), new HttpRequestException(Canary)),
        new TaskCanceledException(Canary),
        new System.Text.Json.JsonException(Canary),
        new System.Security.Authentication.AuthenticationException(Canary),
    };

    [Theory]
    [MemberData(nameof(RemoteFailures))]
    public void AFailureThatCameFromTheOtherSide_IsTheKindOfFailure_AndNoCanaryReachesTheText(Exception ex)
    {
        var text = BlindFailureText.Say(ex);

        text.Should().NotBeNullOrWhiteSpace();
        text.Should().NotContain("CANARY").And.NotContain("canary-host").And.NotContain("token=").And.NotContain("wrapper").And.NotContain("redirect?");
    }

    [Fact]
    public void ARedirect_IsTold_AsAnAnswerOfThePeer_WithoutItsTarget()
    {
        var redirect = new HttpRequestException("Request to https://a.test/x was redirected (307) to " + Canary, null, System.Net.HttpStatusCode.TemporaryRedirect);

        BlindFailureText.Say(redirect).Should().Be(BeeMemoryBank.Sync.PeerContactLog.Describe(redirect)).And.Contain("307");
    }

    [Fact]
    public void ALocalFailure_IsTold_ByItsKind_NeverByTheLibrarysMessage()
    {
        BlindFailureText.Say(new IOException("The process cannot access the file '/private/var/mobile/Containers/secret.db' because it is being used")).Should()
            .Be("a file or the storage of this device could not be read or written");
        BlindFailureText.Say(new CryptographicException("key blob 9f3a")).Should().Be("a key or encrypted data could not be used");
        BlindFailureText.Say(new FormatException("bad base64 CANARY")).Should().Be("stored data could not be read");
        BlindFailureText.Say(new KeyNotFoundException("account abc")).Should().Be("the account is not there any more");
        BlindFailureText.Say(new OperationCanceledException("CANARY")).Should().Be("it was cancelled");
        BlindFailureText.Say(new NotSupportedException("CANARY detail")).Should().Be("an unexpected problem (NotSupportedException)");
        BlindFailureText.Say(new Microsoft.Data.Sqlite.SqliteException("SQLite Error 5: 'database is locked' CANARY", 5)).Should().Be("the database of this copy could not be used");
    }

    [Fact]
    public void ASentenceThisCodeWrote_IsSaid_OnOneLine_AndBounded()
    {
        BlindFailureText.Say(new BlindSentenceException("The list of accounts is damaged.")).Should().Be("The list of accounts is damaged.");
        BlindFailureText.Say(BlindOwnSentence.Mark(new ArgumentException("An account needs a name."))).Should().StartWith("An account needs a name.");

        var long1 = BlindFailureText.Say(new BlindSentenceException("first line\r\nsecond " + new string('x', 400)));
        long1.Should().NotContain("\n").And.NotContain("\r");
        long1.Length.Should().BeLessThanOrEqualTo(BlindFailureText.MaxSentence + 3);
        long1.Should().EndWith("...");
    }

    [Fact]
    public void AnAggregateAndAWrapperAroundALocalFailure_SayWhatTheInnerOneSays()
    {
        BlindFailureText.Say(new AggregateException(new IOException("x"))).Should().Be("a file or the storage of this device could not be read or written");
        BlindFailureText.Say(new System.Reflection.TargetInvocationException(new KeyNotFoundException("x"))).Should().Be("the account is not there any more");
    }
}
