using System;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.MacOS;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>The real ProcessCommandRunner timing out and killing, on a Mac. Skipped, not failed, everywhere else.</summary>
public sealed class MacOsCommandRunnerMacTests
{
    [MacOnlyFact]
    public void TheProcessRunner_TimesOut_AndTheHangingProcessIsGoneAfterwards()
    {
        // The hang carries a marker in its command line for its whole life (`sleep` alone has none to find):
        // the compound command keeps the shell from exec-replacing itself with sleep, so the shell whose
        // arguments hold the marker is the process that hangs, and `pgrep -f` can see it.
        var marker = "bmb-gone-check-" + Guid.NewGuid().ToString("N");
        var arguments = new[] { "-c", "sleep 30; exit 0", marker };
        var runner = new ProcessCommandRunner();

        var hung = Task.Run(() => runner.Run("/bin/sh", arguments, TimeSpan.FromSeconds(1)));

        // the marker is really findable while the process lives - otherwise the gone-check below would
        // pass vacuously if the marker never matched anything at all
        var seen = false;
        for (var attempt = 0; attempt < 40 && !seen; attempt++)
        {
            seen = runner.Run("/usr/bin/pgrep", ["-f", marker], TimeSpan.FromSeconds(5)).Succeeded;
            if (!seen) Thread.Sleep(50);
        }
        seen.Should().BeTrue("the hanging process must be findable by its marker while it lives");

        hung.Result.TimedOut.Should().BeTrue();
        runner.Run("/usr/bin/pgrep", ["-f", marker], TimeSpan.FromSeconds(5)).Succeeded
            .Should().BeFalse("the runner kills the tree and waits for the kill: nothing matches the marker afterwards");
    }
}
