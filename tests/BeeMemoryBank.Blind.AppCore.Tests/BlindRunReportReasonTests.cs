using System.Security.Authentication;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// A failed sync says why in the log: the HTTP stack's "see inner exception" is followed by the inner exception's words, so a refused
/// certificate does not read the same as a closed port.
/// </summary>
public sealed class BlindRunReportReasonTests
{
    [Fact]
    public void TheInnerExceptionsWords_FollowTheOuterMessage()
    {
        var error = new HttpRequestException("The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback."));

        BlindRunReport.Reason(error).Should().Be(
            "The SSL connection could not be established, see inner exception. (AuthenticationException: The remote certificate was rejected by the provided RemoteCertificateValidationCallback.)");
    }

    [Fact]
    public void APlainException_IsItsMessage_AndARepeatedOrEmptyInnerMessageIsNotRepeated()
    {
        BlindRunReport.Reason(new IOException("link went down")).Should().Be("link went down");
        BlindRunReport.Reason(new IOException("same", new IOException("same", new IOException("")))).Should().Be("same");
    }

    [Fact]
    public void ALongChain_IsCut_AndLinesAreJoined()
    {
        var deep = new Exception("a\nb", new Exception(new string('x', 600)));
        var reason = BlindRunReport.Reason(deep);
        reason.Should().StartWith("a b (Exception: xxx").And.EndWith("...");
        reason.Length.Should().BeLessThanOrEqualTo(403);
    }
}
