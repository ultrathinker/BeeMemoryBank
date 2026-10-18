using System.Text;
using BeeMemoryBank.TestSupport;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// BMB-306 fix round, finding B/C: a setup, join or restore writes in the open before the node is encrypted, and the process can die in between. The
/// seal intent is a marker written BEFORE the first such write; a start (VaultStartup.Enter) that finds it still there never lets the plain start serve
/// what was written: an encrypted pair only loses the marker, anything else is wiped, and a file that will not go refuses the start by name.
/// The directories are real SQLite files with a canary in them, so a byte scan shows what a wipe left.
/// </summary>
public sealed class SealIntentTests : IClassFixture<SealIntentTests.Corpus>
{
    public sealed class Corpus : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bmb_sealintent_" + Guid.NewGuid().ToString("N"));
        public SealedCorpus.Pool Pool { get; }

        public Corpus()
        {
            Directory.CreateDirectory(Root);
            Pool = SealedCorpus.Pool.Create(Root);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    private readonly Corpus _corpus;
    private readonly string _canary = MetadataCanaries.Ascii("intent");

    public SealIntentTests(Corpus corpus) => _corpus = corpus;

    private SealedCorpus.Pool Pool => _corpus.Pool;

    private string NewDir()
    {
        var dir = Path.Combine(_corpus.Root, "d-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void PlainFile(string path, string source)
    {
        File.Copy(source, path, overwrite: true);
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write);
        fs.Write(Encoding.UTF8.GetBytes(_canary));
    }

    /// <summary>What a cut setup leaves: the plain databases with notes (a canary), a WAL, the working folder of a join, a half-made migration target.</summary>
    private string CutSetup(string kind = SealIntent.KindSetup)
    {
        var dir = NewDir();
        PlainFile(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain);
        PlainFile(Path.Combine(dir, "chat.db"), Pool.PlainChat);
        File.WriteAllText(Path.Combine(dir, "beememorybank.db-wal"), _canary);
        Directory.CreateDirectory(Path.Combine(dir, "tmp", "work-1"));
        File.WriteAllText(Path.Combine(dir, "tmp", "work-1", "extracted.db"), _canary);
        File.WriteAllText(Path.Combine(dir, "tmp", "archive.tar.gz"), _canary);
        SealIntent.Begin(dir, kind);
        return dir;
    }

    private string SealedNode(bool intent)
    {
        var dir = NewDir();
        File.Copy(Pool.SealedMain, Path.Combine(dir, "beememorybank.db"));
        File.Copy(Pool.SealedChat, Path.Combine(dir, "chat.db"));
        SealedCorpus.MakeBoot(Path.Combine(dir, "beememorybank.boot.db"));
        if (intent) SealIntent.Begin(dir, SealIntent.KindSetup);
        return dir;
    }

    // ---- the marker ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_marker_is_durable_names_its_kind_and_holds_nothing_secret()
    {
        var dir = NewDir();

        SealIntent.Begin(dir, SealIntent.KindJoin);

        SealIntent.IsPending(dir).Should().BeTrue();
        SealIntent.KindOf(dir).Should().Be("join");
        var text = File.ReadAllText(SealIntent.PathFor(dir));
        text.Should().NotContain(dir, "the marker names a kind, not a location");
        Directory.EnumerateFiles(dir, "*.tmp").Should().BeEmpty("the temp file of the write is renamed over the marker");
        SealIntent.Complete(dir).Should().BeTrue();
        SealIntent.IsPending(dir).Should().BeFalse();
        SealIntent.KindOf(dir).Should().BeNull();
    }

    [Fact]
    public void A_marker_that_cannot_be_written_fails_the_flow_before_it_writes_anything()
    {
        var dir = NewDir();
        Directory.CreateDirectory(SealIntent.PathFor(dir));   // a folder where the marker has to go

        var begin = () => SealIntent.Begin(dir, SealIntent.KindSetup);

        begin.Should().Throw<Exception>();
    }

    // ---- a start that finds nothing to decide ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_plain_node_with_no_marker_is_never_touched()
    {
        var dir = NewDir();
        PlainFile(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain);
        PlainFile(Path.Combine(dir, "chat.db"), Pool.PlainChat);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.None);
        File.Exists(Path.Combine(dir, "beememorybank.db")).Should().BeTrue("a node that was set up long ago and is not encrypted yet is the owner's, not an interrupted setup");
        File.Exists(Path.Combine(dir, "chat.db")).Should().BeTrue();
    }

    // ---- a start that finds an intent ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SealIntent.KindSetup)]
    [InlineData(SealIntent.KindJoin)]
    [InlineData(SealIntent.KindRestore)]
    public void A_cut_flow_leaves_no_readable_node_behind(string kind)
    {
        var dir = CutSetup(kind);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Discarded);
        result.Kind.Should().Be(kind);
        result.WipedNames.Should().Contain(["beememorybank.db", "chat.db", "beememorybank.db-wal", "tmp"]);
        SealIntent.IsPending(dir).Should().BeFalse();
        Directory.EnumerateFileSystemEntries(dir).Should().BeEmpty("the node is the empty node it was before the flow began");
    }

    [Fact]
    public void A_half_made_migration_target_and_boot_store_go_with_it()
    {
        var dir = CutSetup();
        File.Copy(Pool.SealedMain, Path.Combine(dir, "beememorybank.db.seal-new"));
        SealedCorpus.MakeBoot(Path.Combine(dir, "beememorybank.boot.db.seal-new"));

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Discarded);
        Directory.EnumerateFileSystemEntries(dir).Should().BeEmpty();
    }

    [Fact]
    public void An_encrypted_pair_only_loses_the_marker_it_has_not_lost_its_data()
    {
        var dir = SealedNode(intent: true);
        var before = new FileInfo(Path.Combine(dir, "beememorybank.db")).Length;

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Completed);
        SealIntent.IsPending(dir).Should().BeFalse();
        new FileInfo(Path.Combine(dir, "beememorybank.db")).Length.Should().Be(before);
        File.Exists(Path.Combine(dir, "chat.db")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "beememorybank.boot.db")).Should().BeTrue();
    }

    [Fact]
    public void A_pending_swap_is_the_swap_resolvers_to_settle_not_this()
    {
        var dir = CutSetup();
        SealJournal.Write(dir, SealJournal.New(SealJournal.Building, ["beememorybank.db", "chat.db"], string.Empty));

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.None);
        SealIntent.IsPending(dir).Should().BeTrue("the marker waits until the swap is settled");
        File.Exists(Path.Combine(dir, "beememorybank.db")).Should().BeTrue();
    }

    [Fact]
    public void A_marker_nobody_can_read_is_no_permission_to_wipe_anything()
    {
        // BMB-306 fix round 2 (finding N2): a damaged marker used to count as pending and cost the folder its databases. It refuses the start instead, and touches nothing.
        var dir = CutSetup();
        File.WriteAllText(SealIntent.PathFor(dir), "\u0001 not json");

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.UntrustedCode);
        result.Kind.Should().Be("unknown");
        File.Exists(Path.Combine(dir, "beememorybank.db")).Should().BeTrue("nothing was wiped");
        MetadataCanaries.ScanDirectory(dir, [_canary]).Should().NotBeEmpty("the node's data is exactly as it was");
        SealIntent.IsPending(dir).Should().BeTrue();
    }

    [Fact]
    public void A_boot_store_that_is_not_ours_is_left_alone()
    {
        var dir = CutSetup();
        File.WriteAllText(Path.Combine(dir, "beememorybank.boot.db"), "somebody else's file");

        SealIntent.Resolve(dir);

        File.Exists(Path.Combine(dir, "beememorybank.boot.db")).Should().BeTrue("the start's own refusal names it; this does not guess");
    }

    [Fact]
    public void A_file_that_will_not_go_refuses_the_start_by_name_and_keeps_the_marker()
    {
        var dir = CutSetup();
        var stuck = Path.Combine(dir, "beememorybank.db");

        var result = SealIntent.Resolve(dir, wipe: path => path != stuck && SecureFileWipe.TryWipe(path));

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.RefusedCode);
        result.Refusal.Files.Should().Contain("beememorybank.db");
        result.Refusal.Message.Should().Contain("beememorybank.db").And.NotContain(dir);
        SealIntent.IsPending(dir).Should().BeTrue("a start that could not remove it must come back to it");
        File.Exists(stuck).Should().BeTrue();
    }

    // ---- the gate every process passes ------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_start_gate_wipes_a_cut_setup_before_any_host_can_serve_it()
    {
        var dir = CutSetup(SealIntent.KindJoin);

        var (resolution, lease) = VaultStartup.Enter(dir);
        lease.Dispose();

        resolution.Intent!.Action.Should().Be(SealIntentAction.Discarded);
        resolution.Seal.Should().BeNull("a wiped setup is not a refusal");
        MetadataCanaries.ScanDirectory(dir, [_canary]).Should().BeEmpty();
        File.Exists(Path.Combine(dir, "beememorybank.db")).Should().BeFalse();
    }

    [Fact]
    public void The_start_gate_turns_an_unwipeable_leftover_into_the_refusal_every_host_already_reports()
    {
        var dir = CutSetup();
        // Held open without sharing: the wipe cannot overwrite it, so the start must refuse by name instead of serving it.
        using var held = new FileStream(Path.Combine(dir, "beememorybank.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var (resolution, lease) = VaultStartup.Enter(dir);
        lease.Dispose();

        resolution.Intent!.Action.Should().Be(SealIntentAction.Refused);
        resolution.Seal!.Action.Should().Be(SealResolveAction.Refused);
        resolution.Seal.Refusal!.Code.Should().Be(SealIntent.RefusedCode);
        resolution.Seal.Refusal.Files.Should().Contain("beememorybank.db");
        SealIntent.IsPending(dir).Should().BeTrue();
    }
}
