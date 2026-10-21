using System.Net;
using BeeMemoryBank.Sync;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Release 2.7: the answer of a blind node whose owner's limit stops it (503 + <c>X-BMB-Paused</c> + <c>Retry-After</c>) becomes a typed pause on the full node,
/// read strictly: only a 503 that names a reason is one, the reason is a closed token, the time is bounded, and nothing the peer wrote is repeated in a sentence.
/// </summary>
public class PeerPausedExceptionTests
{
    private static HttpResponseMessage Answer(HttpStatusCode status, string? paused = null, Action<HttpResponseMessage>? more = null)
    {
        var response = new HttpResponseMessage(status);
        if (paused is not null) response.Headers.TryAddWithoutValidation(PeerPausedException.HeaderName, paused);
        more?.Invoke(response);
        return response;
    }

    [Fact]
    public void A503ThatNamesAReason_IsAPause_WithTheTimeItNamed()
    {
        using var response = Answer(HttpStatusCode.ServiceUnavailable, "traffic", r => r.Headers.RetryAfter = new(TimeSpan.FromMinutes(90)));

        var thrown = FluentActions.Invoking(() => PeerPausedException.ThrowIfPaused(response)).Should().Throw<PeerPausedException>().Which;

        thrown.Reason.Should().Be("traffic");
        thrown.RetryAfter.Should().Be(TimeSpan.FromMinutes(90));
        thrown.Answer.Should().Be("it is paused by its owner (daily traffic limit)");
    }

    [Fact]
    public void ABusyNode_IsAShortPause_AskedAgainAboutAMinuteLater_AndTold_InTheFullNodesWords()
    {
        // srvfix7 (RV5-05): a node whose room is held by other transfers in flight says so; it is not at a limit.
        using var response = Answer(HttpStatusCode.ServiceUnavailable, "busy", r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(60)));

        var thrown = FluentActions.Invoking(() => PeerPausedException.ThrowIfPaused(response)).Should().Throw<PeerPausedException>().Which;

        thrown.Reason.Should().Be("busy");
        thrown.RetryAfter.Should().Be(TimeSpan.FromSeconds(60));
        thrown.Answer.Should().Be("it is busy with other transfers right now");
        // srvfix8 (RV6-03): a busy node is not paused by its owner - nobody set anything; the words say so.
        PeerPauseReport.Sentence(thrown.Reason, null, null, DateTime.UtcNow, byNode: true).Should().Be("Busy with other transfers right now; asked again in about a minute");
    }

    [Fact]
    public void ARetryAfterGivenAsADate_IsReadAsTheTimeFromNow()
    {
        using var response = Answer(HttpStatusCode.ServiceUnavailable, "disk", r => r.Headers.RetryAfter = new(DateTimeOffset.UtcNow.AddMinutes(10)));

        var thrown = FluentActions.Invoking(() => PeerPausedException.ThrowIfPaused(response)).Should().Throw<PeerPausedException>().Which;

        thrown.RetryAfter.Should().BeCloseTo(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));
        thrown.Answer.Should().Be("it is paused by its owner (storage limit)");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InsufficientStorage)]
    public void OnlyA503IsAPause_WhateverTheHeaderSays(HttpStatusCode status)
    {
        using var response = Answer(status, "traffic");

        FluentActions.Invoking(() => PeerPausedException.ThrowIfPaused(response)).Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A503WithoutAReason_IsAnOrdinary503_AnOverloadedProxyOrANodeInMaintenance(string? header)
    {
        using var response = Answer(HttpStatusCode.ServiceUnavailable, header, r => r.Headers.RetryAfter = new(TimeSpan.FromMinutes(5)));

        FluentActions.Invoking(() => PeerPausedException.ThrowIfPaused(response)).Should().NotThrow();
        FluentActions.Invoking(() => response.EnsureSuccessStatusCode()).Should().Throw<HttpRequestException>().Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Theory]
    [InlineData("a reason this build does not know")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("requests")]
    [InlineData("TRAFFIC")]
    public void AReasonThatIsNotOneOfTheTwoThisBuildKnowsHere_IsOther_NeverRepeated(string header)
    {
        using var response = Answer(HttpStatusCode.ServiceUnavailable, header);

        var thrown = FluentActions.Invoking(() => PeerPausedException.ThrowIfPaused(response)).Should().Throw<PeerPausedException>().Which;

        thrown.Answer.Should().NotContain("script").And.NotContain("know");
        thrown.Message.Should().NotContain(header, "nothing the peer wrote is put in a message");
    }

    [Theory]
    [InlineData(null, 3600)]      // nothing said: the hourly retry
    [InlineData(0, 3600)]
    [InlineData(-30, 3600)]
    [InlineData(45, 45)]
    [InlineData(7 * 86400, 86400)] // a week is a day: nothing a peer says holds a node off for longer
    public void TheTimeIsTheNodesOwn_OrAnHourWhenItSaidNothingSensible_AndAtMostADay(int? seconds, int expectedSeconds)
    {
        using var response = Answer(HttpStatusCode.ServiceUnavailable, "disk", r =>
        {
            if (seconds is { } s) r.Headers.RetryAfter = new(TimeSpan.FromSeconds(s));
        });

        var thrown = FluentActions.Invoking(() => PeerPausedException.ThrowIfPaused(response)).Should().Throw<PeerPausedException>().Which;

        thrown.RetryAfter.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void ThePauseIsNeverAFailureOfTheContactStatus_ButSaysWhatItIsInTheHubsWords()
    {
        var thrown = new PeerPausedException("traffic", TimeSpan.FromHours(2));

        PeerContactLog.Describe(thrown).Should().Be("it is paused by its owner (daily traffic limit)");
    }
}
