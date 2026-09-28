using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// The Desktop entry of the offline re-key (rekey-offline.md §8.4): stop the node, run the verb with the password on
/// stdin and its progress as JSON lines, map its exit code, start the node again whatever happened, and open the
/// report page only when there is a re-keyed vault to report on.
/// </summary>
public class DesktopRekeyServiceTests : IDisposable
{
    private const string DataDir = @"C:\vault";
    private const string Password = "owner password with spaces";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "bmb-desktop-rekey-" + Guid.NewGuid().ToString("N"));

    public DesktopRekeyServiceTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    [Fact]
    public async Task ADoneReKey_StopsRunsAndStarts_InThatOrder_AndOpensTheReport()
    {
        var node = new FakeNode();
        var runner = new FakeRunner(0,
            """{"step":"rows","done":1,"total":4,"note":"bodies"}""",
            """{"step":"chat","done":3,"total":3}""",
            """{"result":"done","report":"C:\\vault\\rekey-report.json"}""") { Node = node };
        var progress = new List<RekeyProgressLine>();

        var result = await Service(node, runner).RunAsync(DataDir, Password, nodeIsRunning: true, new SyncProgress<RekeyProgressLine>(progress.Add), null, CancellationToken.None);

        node.Calls.Should().Equal("stop", "run", "start " + DataDir);
        runner.Args.Should().Equal("rekey", "--data", DataDir, "--password-stdin", "--progress-json");
        runner.Stdin.Should().Be(Password + "\n");
        progress.Should().Equal(new RekeyProgressLine("rows", 1, 4, "bodies"), new RekeyProgressLine("chat", 3, 3, null));
        result.Outcome.Should().Be(RekeyOutcome.Done);
        result.ReportPath.Should().Be(@"C:\vault\rekey-report.json");
        result.ReportUrl.Should().Be("http://127.0.0.1:5301/RekeyReport");
    }

    [Fact]
    public async Task ThePassword_IsNeverOnTheCommandLine()
    {
        var runner = new FakeRunner(0);
        await Service(new FakeNode(), runner).RunAsync(DataDir, Password, true, null, null, CancellationToken.None);

        runner.Args.Should().NotContain(a => a.Contains("password with spaces"));
    }

    [Fact]
    public async Task ARefusedPreflight_ListsWhatItFound_AndOpensNoReport()
    {
        var report = Path.Combine(_tmp, "rekey-report.json");
        await File.WriteAllTextAsync(report,
            """{"result":"preflight-refused","preflight":{"blocking":[{"table":"tbl_comment","rowKey":"C1","problem":"does not open"}],"warnings":[],"bytesNeeded":1}}""");
        var node = new FakeNode();
        var runner = new FakeRunner(2, $$"""{"result":"preflight-refused","report":{{System.Text.Json.JsonSerializer.Serialize(report)}}}""") { Node = node };

        var result = await Service(node, runner).RunAsync(DataDir, Password, true, null, null, CancellationToken.None);

        result.Outcome.Should().Be(RekeyOutcome.PreflightRefused);
        result.Problems.Should().Equal("tbl_comment C1: does not open");
        result.ReportUrl.Should().BeNull();
        node.Calls.Last().Should().Be("start " + DataDir, "the node comes back on the untouched vault");
    }

    [Theory]
    [InlineData(3, RekeyOutcome.Failed)]
    [InlineData(1, RekeyOutcome.Failed)]
    [InlineData(-1, RekeyOutcome.Failed)]
    [InlineData(4, RekeyOutcome.SwapPending)]
    public async Task EachExitCode_MapsToItsOutcome_AndTheNodeStartsAgain(int exit, RekeyOutcome outcome)
    {
        var node = new FakeNode();
        var runner = new FakeRunner(exit) { Node = node, Stderr = ["disk full"] };

        var result = await Service(node, runner).RunAsync(DataDir, Password, true, null, null, CancellationToken.None);

        result.Outcome.Should().Be(outcome);
        result.ExitCode.Should().Be(exit);
        node.Calls.Last().Should().Be("start " + DataDir);
        if (outcome == RekeyOutcome.Failed)
        {
            result.Message.Should().Contain("disk full").And.Contain("old vault is still the one in use");
            result.ReportUrl.Should().BeNull();
        }
        else
            result.ReportUrl.Should().NotBeNull("the swap finishes as the node starts, and the report is then in the vault");
    }

    [Fact]
    public async Task AVerbThatCannotRun_IsAFailure_AndTheNodeStillStarts()
    {
        var node = new FakeNode();
        var runner = new FakeRunner(0) { Node = node, Throw = new System.ComponentModel.Win32Exception("access denied") };

        var result = await Service(node, runner).RunAsync(DataDir, Password, true, null, null, CancellationToken.None);

        result.Outcome.Should().Be(RekeyOutcome.Failed);
        result.Message.Should().Contain("access denied");
        node.Calls.Should().Equal("stop", "run", "start " + DataDir);
    }

    [Fact]
    public async Task WithoutTheCli_NothingIsStopped()
    {
        var node = new FakeNode();

        var result = await new DesktopRekeyService(node, new FakeRunner(0), () => null)
            .RunAsync(DataDir, Password, true, null, null, CancellationToken.None);

        result.Outcome.Should().Be(RekeyOutcome.NotRun);
        node.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ANodeThatDoesNotStartAgain_IsReported()
    {
        var node = new FakeNode { StartResult = new NodeLifecycleResult { Success = false, ErrorMessage = "port taken" } };

        var result = await Service(node, new FakeRunner(0) { Node = node }).RunAsync(DataDir, Password, true, null, null, CancellationToken.None);

        result.FrontUrl.Should().BeNull();
        result.StartError.Should().Be("port taken");
        result.ReportUrl.Should().BeNull();
    }

    /// <summary>Once the verb runs, a cancelled token does not stop it: killing it mid-swap is the crash the journal
    /// is for, not something to cause.</summary>
    [Fact]
    public async Task ACancelledToken_DoesNotInterruptTheVerb_OrKeepTheNodeDown()
    {
        using var cts = new CancellationTokenSource();
        var node = new FakeNode();
        var runner = new FakeRunner(0) { Node = node, OnRun = cts.Cancel };

        var result = await Service(node, runner).RunAsync(DataDir, Password, true, null, null, cts.Token);

        result.Outcome.Should().Be(RekeyOutcome.Done);
        node.Calls.Last().Should().Be("start " + DataDir);
    }

    [Fact]
    public async Task LinesThatAreNotProgress_AreShownAsTheyAre_AndBrokenJsonIsNotFatal()
    {
        var status = new List<string>();
        var runner = new FakeRunner(0, "plain text from the verb", "{\"step\":", "[1,2]", """{"step":"verify","done":0,"total":0}""");

        await Service(new FakeNode(), runner).RunAsync(DataDir, Password, false, null, new SyncProgress<string>(status.Add), CancellationToken.None);

        status.Should().Contain("plain text from the verb").And.Contain("{\"step\":").And.Contain("Re-keying: verify");
    }

    /// <summary>R2-3: StopAsync leaves an attached node running, so the re-key refuses before touching anything.</summary>
    [Fact]
    public async Task AnAttachedExternalNode_IsRefused_BeforeAnythingIsStoppedOrRun()
    {
        var node = new FakeNode { Attached = true };
        var runner = new FakeRunner(0) { Node = node };

        var result = await Service(node, runner).RunAsync(DataDir, Password, nodeIsRunning: true, null, null, CancellationToken.None);

        result.Outcome.Should().Be(RekeyOutcome.NotRun);
        result.Message.Should().Contain("not started by this app").And.Contain("Stop that node first");
        node.Calls.Should().BeEmpty("nothing is stopped, run or started");
        result.ReportUrl.Should().BeNull();
    }

    [Fact]
    public async Task ANodeThatIsNotRunning_IsNotStopped()
    {
        var node = new FakeNode();

        await Service(node, new FakeRunner(0) { Node = node }).RunAsync(DataDir, Password, nodeIsRunning: false, null, null, CancellationToken.None);

        node.Calls.Should().Equal("run", "start " + DataDir);
    }

    // ─── Fakes ──────────────────────────────────────────────────────────────

    private static DesktopRekeyService Service(FakeNode node, FakeRunner runner) => new(node, runner, () => @"C:\app\cli\bmb.exe");

    /// <summary>Reports synchronously, so the test sees every line before RunAsync returns.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeNode : INodeLifecycleService
    {
        public List<string> Calls { get; } = [];
        public bool Attached { get; init; }
        public bool IsAttachedToExternalNode => Attached;
        public NodeLifecycleResult StartResult { get; init; } = new() { Success = true, FrontUrl = "http://127.0.0.1:5301/" };

        public Task<NodeLifecycleResult> StartOrAttachAsync(string dataDir, IProgress<string>? progress, CancellationToken ct)
        {
            Calls.Add("start " + dataDir);
            return Task.FromResult(StartResult);
        }

        public Task StopAsync(TimeSpan gracefulTimeout, CancellationToken ct)
        {
            Calls.Add("stop");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRunner(int exit, params string[] stdout) : IRekeyProcessRunner
    {
        public FakeNode? Node { get; init; }
        public string[] Stderr { get; init; } = [];
        public Exception? Throw { get; init; }
        public Action? OnRun { get; init; }
        public IReadOnlyList<string> Args { get; private set; } = [];
        public string? Stdin { get; private set; }

        public Task<int> RunAsync(string exe, IReadOnlyList<string> args, string stdin, Action<string> onStdout, Action<string> onStderr)
        {
            Node?.Calls.Add("run");
            OnRun?.Invoke();
            if (Throw != null) throw Throw;
            Args = args;
            Stdin = stdin;
            foreach (var line in stdout) onStdout(line);
            foreach (var line in Stderr) onStderr(line);
            return Task.FromResult(exit);
        }
    }
}
