using System.Security.Authentication;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// A failed sync says why in the log: the HTTP stack's "see inner exception" is followed by the inner exception's words, so a refused
/// certificate does not read the same as a closed port.
/// </summary>
public sealed class BlindRunReportReasonTests
{
    [Fact]
    public void AFailedCallIsToldByItsKind_ARefusedCertificateReadsOtherwiseThanAClosedPort_AndNoWordOfTheStackOrTheOtherSideIsShown()
    {
        var certificate = new HttpRequestException("The SSL connection could not be established, see inner exception. (https://192.168.1.5:5610)",
            new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback."));
        var closedPort = new HttpRequestException("No connection could be made because the target machine actively refused it. (192.168.1.5:5610)",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        var answered = new HttpRequestException("Response status code does not indicate success: 502 (Pay at https://evil.example/?token=abc).", null,
            System.Net.HttpStatusCode.BadGateway);

        BlindRunReport.Reason(certificate).Should().Be("no answer: its certificate was not accepted");
        BlindRunReport.Reason(closedPort).Should().Contain("connection refused");
        BlindRunReport.Reason(answered).Should().Contain("502").And.NotContain("evil.example").And.NotContain("token");
        BlindRunReport.Reason(certificate).Should().NotBe(BlindRunReport.Reason(closedPort));
        BlindRunReport.Reason(certificate).Should().NotContain("192.168.1.5").And.NotContain("see inner exception");
    }

    [Fact]
    public void ALocalException_IsItsKind_NeverItsMessageNorTheMessageOfAnythingInsideIt()
    {
        BlindRunReport.Reason(new IOException("link went down")).Should().Be("a file or the storage of this device could not be read or written");
        BlindRunReport.Reason(new IOException("same", new IOException("same", new IOException("")))).Should().Be("a file or the storage of this device could not be read or written");
        BlindRunReport.Reason(new BlindSentenceException("The blind phone identity has not been recorded.")).Should().Be("The blind phone identity has not been recorded.");
        BlindRunReport.Reason(new InvalidOperationException("The blind phone identity has not been recorded.")).Should().Be("an unexpected problem (InvalidOperationException)", "an exception this code did not mark is not a sentence of this code");
    }

    [Fact]
    public void ALongSentenceOfThisCode_IsCut_AndLinesAreJoined_AndWhatAnUnknownExceptionHoldsIsNotSaid()
    {
        var reason = BlindRunReport.Reason(new BlindSentenceException("a\nb " + new string('x', 600)));
        reason.Should().StartWith("a b xxx").And.EndWith("...");
        reason.Length.Should().BeLessThanOrEqualTo(BlindFailureText.MaxSentence + 3);

        BlindRunReport.Reason(new Exception("a\nb", new Exception(new string('x', 600)))).Should().Be("an unexpected problem (Exception)");
    }
}
