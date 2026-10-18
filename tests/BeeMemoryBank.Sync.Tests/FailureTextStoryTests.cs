using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using BeeMemoryBank.Sync;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-306/307 fix round 3 (G3): the generic busy helper of the phone apps used to put the message of an exception and of everything inside it on the screen, so that the
/// whole story of a LOCAL failure was there - and with it the address, the token and the page a proxy sent when the failure was a CALL to another device. The story is now
/// <see cref="FailureText.Story"/>: the kinds of failure that can carry the other side's words are told by <see cref="PeerContactLog.Describe"/>, by TYPE, and nothing inside
/// them is read; every other failure says what it says.
/// </summary>
public class FailureTextStoryTests
{
    private const string Hostile = "Could not reach https://evil.example/pay?token=tok_5c4b3a29 (192.0.2.7:443): <html>Pay now</html>";

    private static void NothingOfTheOtherSide(string text) =>
        text.Should().NotContain("tok_5c4b3a29").And.NotContain("evil.example").And.NotContain("192.0.2.7").And.NotContain("Pay now").And.NotContain("Could not reach");

    [Fact]
    public void A_refused_connection_is_told_as_a_kind_of_failure_without_the_text_of_the_exception_or_of_its_socket()
    {
        var failure = new HttpRequestException(Hostile, new SocketException((int)SocketError.ConnectionRefused));

        var story = FailureText.Story(failure);

        story.Should().Be("no answer: connection refused (nothing listens at that address)");
        NothingOfTheOtherSide(story);
    }

    [Fact]
    public void An_answer_the_peer_could_not_have_made_is_told_by_its_status_and_not_by_its_reason_phrase()
    {
        var story = FailureText.Story(new HttpRequestException(Hostile, null, HttpStatusCode.BadGateway));

        story.Should().Contain("502");
        NothingOfTheOtherSide(story);
    }

    [Fact]
    public void A_sentence_of_this_code_that_wraps_a_failed_call_says_its_own_words_and_the_kind_of_the_call_failure()
    {
        var failure = new InvalidOperationException("The connect link could not be used.", new HttpRequestException(Hostile, new TimeoutException(Hostile)));

        var story = FailureText.Story(failure);

        story.Should().StartWith("The connect link could not be used.");
        story.Should().Contain("no answer");
        NothingOfTheOtherSide(story);
    }

    [Theory]
    [MemberData(nameof(RemoteInfluenced))]
    public void Every_kind_that_can_carry_the_other_sides_words_is_told_and_never_read(Exception failure)
    {
        var story = FailureText.Story(failure);

        story.Should().NotBeNullOrWhiteSpace();
        NothingOfTheOtherSide(story);
        FailureText.IsPeerFailure(failure).Should().BeTrue();
    }

    public static IEnumerable<object[]> RemoteInfluenced() =>
    [
        [new HttpRequestException(Hostile)],
        [new JsonException(Hostile)],
        [new SocketException((int)SocketError.HostNotFound)],
        [new AuthenticationException(Hostile)],
        [new TimeoutException(Hostile)],
        [new TaskCanceledException(Hostile)],
        [new NoSyncServerException("it answered 404")],   // its reason is a sentence of this code (SyncAddress), built from the status only
        [new PeerIncompatibleException(3)],
    ];

    [Fact]
    public void A_failure_of_local_work_keeps_its_whole_text_and_the_text_of_what_is_inside_it()
    {
        // A disk, a file the person chose, a database: it cannot carry the other side's words, and the person has to be able to act on it.
        var failure = new IOException("The disk is full.", new UnauthorizedAccessException("Access to 'E:\\backup' is denied."));

        FailureText.Story(failure).Should().Be("The disk is full. Access to 'E:\\backup' is denied.");
    }

    [Fact]
    public void A_local_failure_beside_a_failed_call_is_told_in_both_halves_once()
    {
        var failure = new AggregateException(new IOException("The disk is full."), new HttpRequestException(Hostile), new IOException("The disk is full."));

        var story = FailureText.Story(failure);

        story.Should().StartWith("The disk is full.").And.Contain("no answer");
        story.Split("The disk is full.").Should().HaveCount(2, "the same words are not said twice");
        NothingOfTheOtherSide(story);
    }

    [Fact]
    public void A_failure_that_says_nothing_is_told_by_its_type()
    {
        FailureText.Story(new Exception("")).Should().Be("Exception");
    }
}
