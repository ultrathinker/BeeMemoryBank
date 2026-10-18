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
