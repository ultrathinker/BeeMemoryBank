using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>"Restart to update": hand over the session, stop the node, apply. When applying fails the node comes back and the person is told.</summary>
public sealed class UpdateRestartSequenceTests
{
    private readonly List<string> _steps = new();
    private readonly List<string> _log = new();
    private string? _reported;

    private Task Run(Action apply, Action? resume = null) => UpdateRestartSequence.RunAsync(
        prepare: () => { _steps.Add("prepare"); return Task.CompletedTask; },
        stopNode: () => _steps.Add("stop"),
        apply: () => { _steps.Add("apply"); apply(); },
        resumeNode: () => { _steps.Add("resume"); resume?.Invoke(); },
        onFailure: message => { _steps.Add("failure"); _reported = message; },
        log: _log.Add);

    [Fact]
    public async Task WhenApplyingWorks_TheNodeIsStopped_AndNothingIsResumedOrReported()
    {
        await Run(() => { });

        _steps.Should().Equal("prepare", "stop", "apply");
        _reported.Should().BeNull();
    }

    [Fact]
    public async Task WhenApplyingFails_TheNodeIsStartedAgain_TheReasonIsLogged_AndThePersonIsTold()
    {
        await Run(() => throw new InvalidOperationException("Update.exe was not found"));

        _steps.Should().Equal("prepare", "stop", "apply", "resume", "failure");
        _reported.Should().Contain("Update.exe was not found");
        _log.Should().ContainSingle().Which.Should().Contain("Update.exe was not found");
    }

    [Fact]
    public async Task WhenTheNodeCannotBeStartedAgainEither_TheReportSaysSo_AndItIsLogged()
    {
        await Run(() => throw new InvalidOperationException("locked file"), () => throw new InvalidOperationException("no node"));

        _steps.Should().EndWith(new[] { "resume", "failure" });
        _reported.Should().Contain("locked file").And.Contain("no node");
        _log.Should().HaveCount(2);
    }

    [Fact]
    public async Task ALogThatCannotBeWritten_DoesNotHideTheFailure()
    {
        await UpdateRestartSequence.RunAsync(() => Task.CompletedTask, () => { }, () => throw new InvalidOperationException("boom"), () => { },
            message => _reported = message, _ => throw new System.IO.IOException("disk full"));

        _reported.Should().Contain("boom");
    }
}
