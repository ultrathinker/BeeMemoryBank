using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// BMB-306 fix round 2 (finding N5, with N1): the command line is a host of its own. A <c>bmb init</c> or <c>bmb join</c> that is cut off before the node is
/// encrypted leaves a seal intent, and the next command (the CLI's own <see cref="CliServiceProvider"/>, the gate every command passes) settles it: what was
/// written in the open is gone, and the node is the empty node it was. A command that starts while another process still holds the vault - the process that is
/// running the operation, or one that was cut off while another still runs - is refused by name and wipes nothing.
/// </summary>
[Collection("CliSealedStatics")]
public sealed class SealIntentCliTests : IDisposable
{
    private const string Password = "SealIntentCliPw1x";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_cli_seal_intent_" + Guid.NewGuid().ToString("N"));
    private readonly string _canary = MetadataCanaries.Ascii("clisetup");

    public SealIntentCliTests()
    {
        NodeApi.UrlOverride = "http://127.0.0.1:1";   // no node answers: a test must not reach a node of the machine it runs on
        CliSealed.Error = new StringWriter();
        CliSealed.StdinIsRedirected = () => true;
        CliSealed.Set(null, false);
    }

    public void Dispose()
    {
        NodeApi.UrlOverride = null;
        CliSealed.Error = Console.Error;
        CliSealed.StdinIsRedirected = () => Console.IsInputRedirected;
        CliSealed.Set(null, false);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { Directory.Delete(_dir + "_image", true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private bool Readable(string dir, string canary) => MetadataCanaries.ScanDirectory(dir, [canary]).Count > 0;

    /// <summary>The first half of <c>bmb init</c>: the vault is written in the open under a seal intent, and the process is gone before it encrypts it.</summary>
    private async Task<ServiceProvider> CutInitAsync()
    {
        var services = await CliServiceProvider.CreateAsync(_dir, forFreshSeal: true);
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync(_canary, "Cut node", Password);
        return services;
    }

    [Fact]
    public async Task A_command_line_setup_cut_before_it_encrypts_the_node_is_wiped_by_the_next_command()
    {
        await using (await CutInitAsync())
        {
            SealIntent.IsPending(_dir).Should().BeTrue("the marker was written before the first row");
            Readable(_dir, _canary).Should().BeTrue("the control: the cut leaves the setup readable");
        }
        SqliteConnection.ClearAllPools();   // the process is gone

        await using var next = await CliServiceProvider.CreateAsync(_dir);

        using var scope = next.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsClaimedAsync()).Should().BeFalse("the next command found the empty node it was");
        SealIntent.IsPending(_dir).Should().BeFalse();
        SqliteConnection.ClearAllPools();
        Readable(_dir, _canary).Should().BeFalse("nothing of the cut setup is readable any more");
    }

    [Fact]
    public async Task A_command_that_starts_while_the_operation_still_runs_is_refused_and_wipes_nothing()
    {
        // The first provider is the process that runs the setup (it holds the vault lease shared); the second is "bmb status" started beside it.
        await using var running = await CutInitAsync();

        var second = async () => await CliServiceProvider.CreateAsync(_dir);

        (await second.Should().ThrowAsync<CliVaultException>()).Which.Message.Should().Contain("seal-intent.json").And.Contain("pending").And.NotContain(_dir);
        SealIntent.IsPending(_dir).Should().BeTrue("the running operation's marker is not touched");
        SqliteConnection.ClearAllPools();
        Readable(_dir, _canary).Should().BeTrue("another live process may own the operation: nothing was wiped");
    }

    [Fact]
    public async Task A_second_setup_cannot_begin_over_an_operation_that_has_begun_and_takes_nothing_of_it_back()
    {
        await using var services = await CliServiceProvider.CreateAsync(_dir, forFreshSeal: true);
        SealIntent.Begin(_dir, SealIntent.KindJoin);   // another operation of this node has begun and has written nothing yet
        var markerBefore = File.ReadAllText(SealIntent.PathFor(_dir));
        var media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(media);
        File.WriteAllText(Path.Combine(media, "other-operation.enc"), "written by the operation that is running");
        using var scope = services.CreateScope();
        using var flow = SealIntent.Flow.Enter();   // the second setup: a flow of its own, as the init command is

        var again = async () => await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("other", "Second", "OtherPw1x2y3");

        (await again.Should().ThrowAsync<SealIntentException>()).Which.Code.Should().BeOneOf(SealIntent.PendingCode, SealIntent.BusyCode);
        File.ReadAllText(SealIntent.PathFor(_dir)).Should().Be(markerBefore, "the marker of the operation that began is not overwritten");
        // The failed setup's own take-back (what the init command does on any failure) takes back nothing that is not its own.
        (await scope.ServiceProvider.GetRequiredService<NodeResetService>().DiscardPendingSetupAsync("test: the second setup failed")).Should().BeFalse();
        File.Exists(Path.Combine(media, "other-operation.enc")).Should().BeTrue("what the other operation wrote is not taken back by a flow that did not begin it");
        SealIntent.IsPending(_dir).Should().BeTrue();
        SealIntent.Complete(_dir);
    }

    // ---- a join, cut where the host's snapshot is asked for ----------------------------------------------------------------------------

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write);
            input.CopyTo(output);
        }
        CutImage.Adopt(to);   // the image of a cut: its marker names the files of the image
    }

    [Fact]
    public async Task A_command_line_join_cut_at_the_snapshot_is_wiped_by_the_next_command()
    {
        var image = _dir + "_image";
        var host = new FakeJoinHost(Guid.NewGuid());
        var join = host.JoinResponseJson(Password);
        var pendingAtSnapshot = false;
        using var server = new RecordingJoinHost(r =>
        {
            if (r.Path == "/api/sync/snapshot/for-join")
            {
                // The joiner has written its own identity, slot and user in the open by now: this is the instant of the cut.
                pendingAtSnapshot = SealIntent.IsPending(_dir);
                CopyFolder(_dir, image);
            }
            return r.Path == "/api/join" ? RecordingJoinHost.Reply(200, join) : host.Answer(r.Path);
        });

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_dir, server.Url, Password, "CliJoinerCut", output: output);

        rc.Should().Be(0, output.ToString());
        pendingAtSnapshot.Should().BeTrue("the seal intent was on disk before the first row of the join");
        SealIntent.IsPending(_dir).Should().BeFalse("the encrypted node ends the intent");
        Readable(image, "CliJoinerCut").Should().BeTrue("the control: the image of the cut holds the joiner's identity in the open");

        // What the next command makes of that image:
        SqliteConnection.ClearAllPools();
        await using var next = await CliServiceProvider.CreateAsync(image);
        using var scope = next.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsClaimedAsync()).Should().BeFalse();
        SealIntent.IsPending(image).Should().BeFalse();
        SqliteConnection.ClearAllPools();
        Readable(image, "CliJoinerCut").Should().BeFalse("nothing of the cut join is readable any more");
    }
}
