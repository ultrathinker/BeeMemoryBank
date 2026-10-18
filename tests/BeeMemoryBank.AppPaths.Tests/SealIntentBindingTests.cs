using System.Text;
using System.Text.Json.Nodes;
using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// BMB-306 fix round 2 (re-review of d140d7af, findings N1, N2, N3). A marker is never permission to erase on its own:
/// <list type="bullet">
/// <item>N2: a stale, damaged, copied or foreign marker next to a node somebody keeps wipes NOTHING - the start is refused by name instead;</item>
/// <item>N1: one operation at a time across processes, and a start that joins a process which holds the vault still sees a pending marker;</item>
/// <item>N3: nothing is read, wiped or followed through a link;</item>
/// <item>BMB-306 fix round 3 (G1): a database is the operation's because it is the FILE the operation found when it began (the volume and file index in the marker), never because
///   it was written after the marker: a node copied or restored into the folder later has a newer time than any marker, and the start is refused with all of it intact.</item>
/// <item>BMB-306 fix round 4 (H1): a link is judged by the directory entry itself, never by whether its target exists: a link to nothing (marker, id, temp, database, sidecar,
///   working folder, data folder, the two journals) is refused like any link, and nothing is read, written, created or wiped through it.</item>
/// </list>
/// The databases are real SQLite files with a canary in them, so a byte scan shows what was left.
/// </summary>
public sealed class SealIntentBindingTests : IClassFixture<SealIntentTests.Corpus>
{
    private readonly SealIntentTests.Corpus _corpus;
    private readonly string _canary = MetadataCanaries.Ascii("binding");

    public SealIntentBindingTests(SealIntentTests.Corpus corpus) => _corpus = corpus;

    private SealedCorpus.Pool Pool => _corpus.Pool;

    private string NewDir(string? name = null)
    {
        var dir = Path.Combine(_corpus.Root, name ?? "b-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A plain database with a canary in it; <paramref name="writtenAt"/> is the time it was last written (a node somebody kept is old).</summary>
    private void PlainDb(string path, string source, DateTime writtenAt)
    {
        File.Copy(source, path, overwrite: true);
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write))
            fs.Write(Encoding.UTF8.GetBytes(_canary));
        File.SetLastWriteTimeUtc(path, writtenAt);
    }

    private static readonly DateTime LongAgo = DateTime.UtcNow.AddDays(-30);

    /// <summary>
    /// A node that somebody keeps is put under <paramref name="path"/> after the operation found the file there: a file of its own (its own identity, made beside the
    /// old one while that still exists) takes the name, as a copy or a volume restore does. Its write time is <paramref name="writtenAt"/>.
    /// </summary>
    private void PutAnotherNodeOver(string path, string source, DateTime writtenAt)
    {
        var incoming = path + ".incoming";
        PlainDb(incoming, source, writtenAt);
        File.Move(incoming, path, overwrite: true);
    }

    private string MarkerText(string dir) => File.ReadAllText(SealIntent.PathFor(dir));

    private static void EditMarker(string dir, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllText(SealIntent.PathFor(dir)))!.AsObject();
        change(node);
        File.WriteAllText(SealIntent.PathFor(dir), node.ToJsonString());
    }

    private bool Readable(string dir) => MetadataCanaries.ScanDirectory(dir, [_canary]).Count > 0;

    // ---- N2: what a marker proves ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_database_the_operation_found_and_went_on_writing_is_wiped_with_its_marker_its_folder_id_and_what_it_made_beside_it()
    {
        // The real shape: the migrations made the databases before the operation began, and the operation writes them in place (same file, a new write time).
        var dir = NewDir();
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, LongAgo);
        PlainDb(Path.Combine(dir, "chat.db"), Pool.PlainChat, LongAgo);
        SealIntent.Begin(dir, SealIntent.KindSetup);
        foreach (var name in new[] { "beememorybank.db", "chat.db" })
            using (var fs = new FileStream(Path.Combine(dir, name), FileMode.Append, FileAccess.Write))
                fs.Write(Encoding.UTF8.GetBytes(_canary));
        File.WriteAllText(Path.Combine(dir, "beememorybank.db-wal"), _canary);
        Directory.CreateDirectory(Path.Combine(dir, "tmp"));
        File.WriteAllText(Path.Combine(dir, "tmp", "archive.tar.gz"), _canary);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Discarded);
        Directory.EnumerateFileSystemEntries(dir).Should().BeEmpty("the marker and the folder id go with what the operation wrote");
    }

    [Fact]
    public void A_database_that_appeared_after_the_operation_began_is_not_provably_the_operations_and_wipes_nothing()
    {
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindSetup);                                          // nothing of the node was there ...
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, DateTime.UtcNow);       // ... and a database is, newer than the marker
        File.WriteAllText(Path.Combine(dir, "beememorybank.db-wal"), _canary);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.ForeignCode);
        result.Refusal.Files.Should().Equal("beememorybank.db");
        Readable(dir).Should().BeTrue();
        File.Exists(Path.Combine(dir, "beememorybank.db-wal")).Should().BeTrue("a sidecar follows its database: nothing is wiped");
        SealIntent.IsPending(dir).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_completed_node_put_into_the_folder_after_a_cut_setup_survives_whatever_its_write_time_is(bool newerThanTheMarker)
    {
        // The finding (G1): a pending marker of a cut setup is kept, and a completed open node is later put in the same folder (a volume copy, a restore). Its
        // databases are NEWER than the marker, which used to count as "written by the operation": the databases, the sidecars and tmp were wiped.
        var dir = NewDir();
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, LongAgo);              // the empty node's databases the setup began on
        PlainDb(Path.Combine(dir, "chat.db"), Pool.PlainChat, LongAgo);
        SealIntent.Begin(dir, SealIntent.KindSetup);
        var written = newerThanTheMarker ? DateTime.UtcNow.AddMinutes(5) : LongAgo;
        PutAnotherNodeOver(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, written);
        PutAnotherNodeOver(Path.Combine(dir, "chat.db"), Pool.PlainChat, written);
        File.WriteAllText(Path.Combine(dir, "beememorybank.db-wal"), _canary);
        Directory.CreateDirectory(Path.Combine(dir, "tmp"));
        File.WriteAllText(Path.Combine(dir, "tmp", "keep.txt"), _canary);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.ForeignCode);
        result.Refusal.Files.Should().Contain(["beememorybank.db", "chat.db"]);
        result.Refusal.Message.Should().Contain("remove only seal-intent.json").And.NotContain(dir);
        Readable(dir).Should().BeTrue("nothing was wiped");
        File.Exists(Path.Combine(dir, "beememorybank.db")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "chat.db")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "beememorybank.db-wal")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "tmp", "keep.txt")).Should().BeTrue();
        SealIntent.IsPending(dir).Should().BeTrue("the refusal stays visible to every start");
    }

    [Fact]
    public void A_database_that_is_exactly_the_file_the_operation_found_is_wiped_however_old_it_is()
    {
        var dir = NewDir();
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, LongAgo);   // the empty node's database, made by the migrations long before
        SealIntent.Begin(dir, SealIntent.KindJoin);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Discarded);
        File.Exists(Path.Combine(dir, "beememorybank.db")).Should().BeFalse();
    }

    [Fact]
    public void A_populated_node_is_not_wiped_for_a_marker_it_did_not_begin_with()
    {
        // The finding: a folder with a node somebody keeps and a stale marker next to it lost its databases at the next start.
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindSetup);                      // a marker of an operation on an empty target ...
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, LongAgo);   // ... and a node that is older than it and was not there when it began
        PlainDb(Path.Combine(dir, "chat.db"), Pool.PlainChat, LongAgo);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.ForeignCode);
        result.Refusal.Files.Should().Contain(["beememorybank.db", "chat.db"]);
        result.Refusal.Message.Should().Contain("remove only seal-intent.json").And.NotContain(dir);
        Readable(dir).Should().BeTrue("nothing was wiped");
        File.Exists(Path.Combine(dir, "beememorybank.db")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "chat.db")).Should().BeTrue();
        SealIntent.IsPending(dir).Should().BeTrue("the refusal stays visible to every start");
    }

    [Fact]
    public void A_database_that_grew_in_place_since_the_operation_found_it_is_still_the_operations_whatever_its_time()
    {
        var dir = NewDir();
        var db = Path.Combine(dir, "beememorybank.db");
        PlainDb(db, Pool.PlainMain, LongAgo);
        SealIntent.Begin(dir, SealIntent.KindSetup);
        using (var fs = new FileStream(db, FileMode.Append, FileAccess.Write)) fs.Write(new byte[8192]);
        File.SetLastWriteTimeUtc(db, LongAgo);                              // grown, with the old time: the same file

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Discarded);
        File.Exists(db).Should().BeFalse();
    }

    [Fact]
    public void A_database_that_was_replaced_by_a_different_file_after_the_operation_found_it_is_not_the_operations_even_with_the_same_length_and_time()
    {
        var dir = NewDir();
        var db = Path.Combine(dir, "beememorybank.db");
        PlainDb(db, Pool.PlainMain, LongAgo);
        var length = new FileInfo(db).Length;
        SealIntent.Begin(dir, SealIntent.KindSetup);
        PutAnotherNodeOver(db, Pool.PlainMain, LongAgo);                    // the same bytes, the same length, the same time: another file

        new FileInfo(db).Length.Should().Be(length);
        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.ForeignCode);
        File.Exists(db).Should().BeTrue();
    }

    [Fact]
    public void One_database_that_is_not_the_operations_stops_the_wipe_of_everything_else_too()
    {
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindSetup);
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, LongAgo);          // somebody's
        PlainDb(Path.Combine(dir, "chat.db"), Pool.PlainChat, DateTime.UtcNow);           // the operation's
        Directory.CreateDirectory(Path.Combine(dir, "tmp"));
        File.WriteAllText(Path.Combine(dir, "tmp", "archive.tar.gz"), _canary);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        File.Exists(Path.Combine(dir, "chat.db")).Should().BeTrue("a doubt wipes nothing: the decision is taken before the first file goes");
        File.Exists(Path.Combine(dir, "tmp", "archive.tar.gz")).Should().BeTrue();
    }

    [Fact]
    public void A_corrupt_marker_on_a_populated_node_wipes_nothing()
    {
        var dir = NewDir();
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, LongAgo);
        File.WriteAllText(SealIntent.PathFor(dir), "{ \"version\": 2, \"kind\": ");   // cut off

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.UntrustedCode);
        Readable(dir).Should().BeTrue();
        File.Exists(SealIntent.PathFor(dir)).Should().BeTrue();
    }

    [Theory]
    [InlineData("version", 1)]                      // the marker of the first fix round: kind and time only
    [InlineData("version", 2)]                      // the marker of the second round: no identity of the files it found, so the write time was the only proof
    [InlineData("version", 4)]
    public void A_marker_of_another_version_is_not_trusted(string field, int value)
    {
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindSetup);
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, DateTime.UtcNow);
        EditMarker(dir, m => m[field] = value);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.UntrustedCode);
        Readable(dir).Should().BeTrue();
    }

    [Theory]
    [InlineData("operation_id")]
    [InlineData("folder")]
    [InlineData("pre")]
    public void A_marker_with_a_missing_part_is_not_trusted(string removed)
    {
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindSetup);
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, DateTime.UtcNow);
        EditMarker(dir, m => m.Remove(removed));

        SealIntent.Resolve(dir).Refusal!.Code.Should().Be(SealIntent.UntrustedCode);
        Readable(dir).Should().BeTrue();
    }

    [Fact]
    public void A_marker_that_does_not_say_the_target_was_empty_is_not_trusted()
    {
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindSetup);
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, DateTime.UtcNow);
        EditMarker(dir, m => m["empty_at_begin"] = false);

        SealIntent.Resolve(dir).Refusal!.Code.Should().Be(SealIntent.UntrustedCode);
        Readable(dir).Should().BeTrue();
    }

    [Fact]
    public void A_marker_that_names_a_time_in_the_future_is_not_trusted()
    {
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindSetup);
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, DateTime.UtcNow);
        EditMarker(dir, m => m["started_at"] = DateTime.UtcNow.AddDays(1).ToString("o"));

        SealIntent.Resolve(dir).Refusal!.Code.Should().Be(SealIntent.UntrustedCode);
        Readable(dir).Should().BeTrue();
    }

    [Fact]
    public void A_marker_copied_alone_into_another_folder_wipes_nothing_there()
    {
        // The copied marker names the folder id of the folder it came from; the folder it was put in holds none, or another.
        var source = NewDir();
        SealIntent.Begin(source, SealIntent.KindSetup);
        var populated = NewDir();
        PlainDb(Path.Combine(populated, "beememorybank.db"), Pool.PlainMain, DateTime.UtcNow);   // even a node written a moment ago
        File.Copy(SealIntent.PathFor(source), SealIntent.PathFor(populated));

        var withoutId = SealIntent.Resolve(populated);

        withoutId.Action.Should().Be(SealIntentAction.Refused);
        withoutId.Refusal!.Code.Should().Be(SealIntent.UntrustedCode);
        Readable(populated).Should().BeTrue();

        File.WriteAllText(Path.Combine(populated, SealIntent.FolderIdFileName), Guid.NewGuid().ToString("N"));
        var otherId = SealIntent.Resolve(populated);

        otherId.Refusal!.Code.Should().Be(SealIntent.UntrustedCode);
        Readable(populated).Should().BeTrue();
    }

    [Fact]
    public void A_whole_folder_moved_after_a_cut_is_settled_like_the_cut_it_is_and_a_copy_of_it_is_not_touched()
    {
        // The marker and the id travel with the folder, and so do its files when the folder is MOVED (same files, same identity): a moved half-made node is wiped,
        // wherever it is. A COPY has files of its own that the marker never saw: it is refused and nothing in it is touched.
        var source = NewDir();
        PlainDb(Path.Combine(source, "beememorybank.db"), Pool.PlainMain, LongAgo);
        SealIntent.Begin(source, SealIntent.KindSetup);
        File.AppendAllText(Path.Combine(source, "beememorybank.db"), _canary);
        var copy = NewDir();
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));

        var copied = SealIntent.Resolve(copy);

        copied.Action.Should().Be(SealIntentAction.Refused);
        copied.Refusal!.Code.Should().Be(SealIntent.ForeignCode);
        Readable(copy).Should().BeTrue("a copy is somebody's file");

        var moved = Path.Combine(_corpus.Root, "moved-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.Move(source, moved);
        SealIntent.Resolve(moved).Action.Should().Be(SealIntentAction.Discarded);
        Directory.EnumerateFileSystemEntries(moved).Should().BeEmpty();
        SealIntent.Complete(source);
    }

    [Fact]
    public void A_marker_beside_an_encrypted_pair_only_goes_whatever_it_says()
    {
        var dir = NewDir();
        File.Copy(Pool.SealedMain, Path.Combine(dir, "beememorybank.db"));
        File.Copy(Pool.SealedChat, Path.Combine(dir, "chat.db"));
        SealedCorpus.MakeBoot(Path.Combine(dir, "beememorybank.boot.db"));
        File.WriteAllText(SealIntent.PathFor(dir), "garbage");

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Completed);
        SealIntent.IsPending(dir).Should().BeFalse();
        File.Exists(Path.Combine(dir, "beememorybank.db")).Should().BeTrue();
    }

    [Fact]
    public void The_marker_names_the_operation_the_folder_and_what_was_there_and_no_location_or_secret()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "beememorybank.db"), "x");

        SealIntent.Begin(dir, SealIntent.KindRestore);

        var marker = JsonNode.Parse(MarkerText(dir))!.AsObject();
        marker["version"]!.GetValue<int>().Should().Be(3);
        marker["kind"]!.GetValue<string>().Should().Be("restore");
        marker["empty_at_begin"]!.GetValue<bool>().Should().BeTrue();
        Guid.TryParseExact(marker["operation_id"]!.GetValue<string>(), "N", out _).Should().BeTrue();
        marker["folder"]!.GetValue<string>().Should().Be(File.ReadAllText(Path.Combine(dir, SealIntent.FolderIdFileName)).Trim());
        marker["pre"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).Should().Equal("beememorybank.db");
        var identity = FileIdentity.TryGet(Path.Combine(dir, "beememorybank.db"))!.Value;
        marker["pre"]![0]!["volume"]!.GetValue<ulong>().Should().Be(identity.Volume, "the marker names which file it found: the volume and the file index");
        marker["pre"]![0]!["index"]!.GetValue<ulong>().Should().Be(identity.Index);
        MarkerText(dir).Should().NotContain(dir);
        SealIntent.Complete(dir).Should().BeTrue();
        File.Exists(Path.Combine(dir, SealIntent.FolderIdFileName)).Should().BeFalse("the folder id goes with the marker");
    }

    // ---- N1: one operation at a time, across processes ----------------------------------------------------------------------------------------

    [Fact]
    public void A_second_operation_cannot_begin_while_the_first_holds_the_lease_and_can_after_it_ends()
    {
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindSetup);
        var markerBefore = MarkerText(dir);

        var second = () => SealIntent.Begin(dir, SealIntent.KindJoin);

        second.Should().Throw<SealIntentException>().Which.Code.Should().Be(SealIntent.BusyCode);
        MarkerText(dir).Should().Be(markerBefore, "the live marker was not overwritten");

        SealIntent.Complete(dir).Should().BeTrue();
        SealIntent.Begin(dir, SealIntent.KindJoin);
        SealIntent.KindOf(dir).Should().Be("join");
        SealIntent.Complete(dir);
    }

    [Fact]
    public void An_operation_cannot_begin_over_a_marker_that_is_pending_even_with_nobody_running()
    {
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindSetup);
        var marker = MarkerText(dir);
        var id = File.ReadAllText(Path.Combine(dir, SealIntent.FolderIdFileName));
        SealIntent.Complete(dir);                                           // the process that held the lease is gone ...
        File.WriteAllText(SealIntent.PathFor(dir), marker);                 // ... and its marker was cut off before it could be settled
        File.WriteAllText(Path.Combine(dir, SealIntent.FolderIdFileName), id);

        var begin = () => SealIntent.Begin(dir, SealIntent.KindJoin);

        begin.Should().Throw<SealIntentException>().Which.Code.Should().Be(SealIntent.PendingCode);
        MarkerText(dir).Should().Be(marker);
        // and the failed Begin did not keep the lease: the start that settles the marker can run
        SealIntent.Resolve(dir).Action.Should().Be(SealIntentAction.Discarded);
        SealIntent.Begin(dir, SealIntent.KindSetup);
        SealIntent.Complete(dir);
    }

    [Fact]
    public void An_operation_never_begins_on_a_target_that_is_not_an_empty_node()
    {
        var dir = NewDir();

        var begin = () => SealIntent.Begin(dir, SealIntent.KindSetup, targetIsEmpty: () => false);

        begin.Should().Throw<SealIntentException>().Which.Code.Should().Be(SealIntent.NotEmptyCode);
        SealIntent.IsPending(dir).Should().BeFalse("no marker was written");
        File.Exists(Path.Combine(dir, SealIntent.FolderIdFileName)).Should().BeFalse();
        SealIntent.Begin(dir, SealIntent.KindSetup, targetIsEmpty: () => true);   // and the lease was not kept
        SealIntent.Complete(dir);
    }

    [Fact]
    public void A_start_that_joins_a_process_that_holds_the_vault_refuses_by_name_while_a_marker_is_pending_and_wipes_nothing()
    {
        // Another process (a node, an Api, a CLI command) holds the vault lease shared: this start cannot resolve - and must not serve what the operation wrote.
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindSetup);
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, DateTime.UtcNow);
        using var other = VaultStartup.TryAcquireShared(dir);
        other.Should().NotBeNull();

        var (resolution, lease) = VaultStartup.Enter(dir);
        lease.Dispose();

        resolution.Intent!.Action.Should().Be(SealIntentAction.Refused);
        resolution.Seal!.Action.Should().Be(SealResolveAction.Refused);
        resolution.Seal.Refusal!.Code.Should().Be(SealIntent.BusyCode);
        resolution.Seal.Refusal.Files.Should().Equal(SealIntent.FileName);
        Readable(dir).Should().BeTrue("another live process may own the operation: nothing is wiped from here");
        SealIntent.IsPending(dir).Should().BeTrue();
        SealIntent.Complete(dir);
    }

    [Fact]
    public void A_start_that_joins_a_process_that_holds_the_vault_goes_on_when_nothing_is_pending()
    {
        var dir = NewDir();
        using var other = VaultStartup.TryAcquireShared(dir);

        var (resolution, lease) = VaultStartup.Enter(dir);
        lease.Dispose();

        resolution.Seal.Should().BeNull();
        resolution.Intent.Should().BeNull();
    }

    // ---- N3: nothing through a link ----------------------------------------------------------------------------------------------------------

    private string OutsideFolderWithCanary()
    {
        var outside = NewDir();
        File.WriteAllText(Path.Combine(outside, "precious.txt"), _canary);
        return outside;
    }

    [Fact]
    public void A_data_folder_that_is_a_link_is_refused_by_Begin_and_by_Resolve_and_nothing_behind_it_is_touched()
    {
        var real = NewDir();
        SealIntent.Begin(real, SealIntent.KindSetup);
        PlainDb(Path.Combine(real, "beememorybank.db"), Pool.PlainMain, DateTime.UtcNow);
        var link = Path.Combine(_corpus.Root, "link-" + Guid.NewGuid().ToString("N")[..8]);
        TestDirectoryLinks.Create(link, real);

        var resolved = SealIntent.Resolve(link);
        var begin = () => SealIntent.Begin(link, SealIntent.KindJoin);

        resolved.Action.Should().Be(SealIntentAction.Refused);
        resolved.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
        begin.Should().Throw<SealIntentException>().Which.Code.Should().Be(SealIntent.UnsafeCode);
        Readable(real).Should().BeTrue("what is behind the link was not touched");
        SealIntent.IsPending(real).Should().BeTrue();
        SealIntent.Complete(real);
    }

    [Fact]
    public void A_working_folder_that_is_a_link_is_refused_and_neither_the_outside_nor_the_operations_own_files_are_touched()
    {
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindJoin);
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, DateTime.UtcNow);
        var outside = OutsideFolderWithCanary();
        TestDirectoryLinks.Create(Path.Combine(dir, "tmp"), outside);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
        result.Refusal.Files.Should().Contain("tmp");
        File.Exists(Path.Combine(outside, "precious.txt")).Should().BeTrue("the wipe never went through the link");
        File.Exists(Path.Combine(dir, "beememorybank.db")).Should().BeTrue("one link wipes nothing: the intent stays for the owner");
        SealIntent.IsPending(dir).Should().BeTrue();
        SealIntent.Complete(dir);
    }

    [Fact]
    public void A_link_below_the_working_folder_is_refused_and_not_followed()
    {
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindRestore);
        var outside = OutsideFolderWithCanary();
        Directory.CreateDirectory(Path.Combine(dir, "tmp", "work-1"));
        File.WriteAllText(Path.Combine(dir, "tmp", "work-1", "extracted.db"), _canary);
        TestDirectoryLinks.Create(Path.Combine(dir, "tmp", "work-1", "elsewhere"), outside);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
        result.Refusal.Files.Should().ContainSingle().Which.Should().EndWith("elsewhere");
        File.Exists(Path.Combine(outside, "precious.txt")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "tmp", "work-1", "extracted.db")).Should().BeTrue("nothing was wiped");
        SealIntent.Complete(dir);
    }

    [Fact]
    public void Begin_refuses_when_the_working_folder_is_already_a_link_and_writes_no_marker()
    {
        var dir = NewDir();
        var outside = OutsideFolderWithCanary();
        TestDirectoryLinks.Create(Path.Combine(dir, "tmp"), outside);

        var begin = () => SealIntent.Begin(dir, SealIntent.KindSetup);

        begin.Should().Throw<SealIntentException>().Which.Code.Should().Be(SealIntent.UnsafeCode);
        SealIntent.IsPending(dir).Should().BeFalse();
        File.Exists(Path.Combine(outside, "precious.txt")).Should().BeTrue();
    }

    private static bool CanLinkFiles()
    {
        var probe = Path.Combine(Path.GetTempPath(), "bmb_filelink_probe_" + Guid.NewGuid().ToString("N"));
        var target = probe + ".target";
        try
        {
            File.WriteAllText(target, "x");
            File.CreateSymbolicLink(probe, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    internal sealed class FileSymlinkFactAttribute : FactAttribute
    {
        public FileSymlinkFactAttribute()
        {
            if (!CanLinkFiles()) Skip = "This account cannot create file symbolic links (Windows needs a privilege for them); a junction is tested above.";
        }
    }

    [FileSymlinkFact]
    public void A_marker_that_is_a_link_is_refused_before_it_is_read_and_nothing_is_wiped_or_followed()
    {
        var dir = NewDir();
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, LongAgo);
        SealIntent.Begin(dir, SealIntent.KindSetup);
        var outside = OutsideFolderWithCanary();
        var elsewhere = Path.Combine(outside, "marker-copy.json");
        File.Move(SealIntent.PathFor(dir), elsewhere);                              // the marker now lives outside the folder ...
        File.CreateSymbolicLink(SealIntent.PathFor(dir), elsewhere);                // ... and the folder holds a link to it: a well-formed marker, read through a link

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
        result.Refusal.Files.Should().Contain(SealIntent.FileName);
        Readable(dir).Should().BeTrue("the databases were not wiped on the word of a marker that is a link");
        File.Exists(elsewhere).Should().BeTrue();
        File.Exists(Path.Combine(outside, "precious.txt")).Should().BeTrue();
        SealIntent.KindOf(dir).Should().Be("unknown", "a linked marker is not read for its kind either");
        SealIntent.Complete(dir);
    }

    [FileSymlinkFact]
    public void A_folder_id_that_is_a_link_is_refused_and_not_read()
    {
        var dir = NewDir();
        PlainDb(Path.Combine(dir, "beememorybank.db"), Pool.PlainMain, LongAgo);
        SealIntent.Begin(dir, SealIntent.KindSetup);
        var outside = OutsideFolderWithCanary();
        var elsewhere = Path.Combine(outside, "id-copy.txt");
        File.Move(Path.Combine(dir, SealIntent.FolderIdFileName), elsewhere);
        File.CreateSymbolicLink(Path.Combine(dir, SealIntent.FolderIdFileName), elsewhere);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
        result.Refusal.Files.Should().Contain(SealIntent.FolderIdFileName);
        Readable(dir).Should().BeTrue();
        SealIntent.Complete(dir);
    }

    [FileSymlinkFact]
    public void Begin_is_refused_while_a_marker_or_a_temp_file_it_writes_through_is_a_link_and_writes_nothing_through_it()
    {
        foreach (var linked in new[] { SealIntent.FileName + ".tmp", SealIntent.FolderIdFileName + ".tmp" })
        {
            var dir = NewDir();
            var outside = OutsideFolderWithCanary();
            var target = Path.Combine(outside, "precious.txt");
            File.CreateSymbolicLink(Path.Combine(dir, linked), target);

            var begin = () => SealIntent.Begin(dir, SealIntent.KindSetup);

            begin.Should().Throw<SealIntentException>().Which.Code.Should().Be(SealIntent.UnsafeCode, linked);
            File.ReadAllText(target).Should().Be(_canary, "a temp file that is a link was not written through");
            SealIntent.IsPending(dir).Should().BeFalse();
        }
    }

    [FileSymlinkFact]
    public void A_database_that_is_a_link_is_refused_and_its_target_is_not_wiped()
    {
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindSetup);
        var outside = OutsideFolderWithCanary();
        File.CreateSymbolicLink(Path.Combine(dir, "beememorybank.db"), Path.Combine(outside, "precious.txt"));

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
        File.ReadAllText(Path.Combine(outside, "precious.txt")).Should().Be(_canary);
        SealIntent.Complete(dir);
    }

    // ---- fix round 4 (H1): a link whose target is gone is still a link ---------------------------------------------------------------------------

    /// <summary>A symbolic link to a file that is gone: the target is made, linked to and removed (the test's own fixture). Returns where the link points.</summary>
    private string DanglingFileLink(string link)
    {
        var target = Path.Combine(NewDir(), "ghost-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(target, _canary);
        File.CreateSymbolicLink(link, target);
        File.Delete(target);
        return target;
    }

    /// <summary>A junction (a symbolic link where there are no junctions) to a folder that is gone. Returns where the link points.</summary>
    private string DanglingFolderLink(string link)
    {
        var target = Path.Combine(NewDir(), "ghost-folder");
        Directory.CreateDirectory(target);
        TestDirectoryLinks.Create(link, target);
        Directory.Delete(target);
        return target;
    }

    /// <summary>A folder with a pending setup marker and something the wipe would remove, so a refusal can be seen to have left it alone.</summary>
    private string PendingSetupWithWipeableLeftover()
    {
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindSetup);
        Directory.CreateDirectory(Path.Combine(dir, "tmp"));
        File.WriteAllText(Path.Combine(dir, "tmp", "archive.tar.gz"), _canary);
        return dir;
    }

    private static bool LeftoverIntact(string dir) => File.Exists(Path.Combine(dir, "tmp", "archive.tar.gz"));

    [FileSymlinkFact]
    public void A_marker_that_is_a_dangling_link_is_pending_and_named_and_nothing_is_wiped_or_created()
    {
        var dir = PendingSetupWithWipeableLeftover();
        File.Delete(SealIntent.PathFor(dir));
        var ghost = DanglingFileLink(SealIntent.PathFor(dir));

        SealIntent.IsPending(dir).Should().BeTrue("an entry named like the marker is there: the start must not go on as if nothing were pending");
        SealIntent.KindOf(dir).Should().Be("unknown", "a linked marker is not read for its kind either");
        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
        result.Refusal.Files.Should().Contain(SealIntent.FileName);
        LeftoverIntact(dir).Should().BeTrue("nothing was wiped on the word of a marker that is a link");
        File.Exists(ghost).Should().BeFalse("nothing was written through the link");
        SealIntent.Complete(dir);
    }

    [FileSymlinkFact]
    public void A_folder_id_that_is_a_dangling_link_is_refused_and_not_followed()
    {
        var dir = PendingSetupWithWipeableLeftover();
        File.Delete(Path.Combine(dir, SealIntent.FolderIdFileName));
        var ghost = DanglingFileLink(Path.Combine(dir, SealIntent.FolderIdFileName));

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
        result.Refusal.Files.Should().Contain(SealIntent.FolderIdFileName);
        LeftoverIntact(dir).Should().BeTrue();
        File.Exists(ghost).Should().BeFalse();
        SealIntent.Complete(dir);
    }

    [FileSymlinkFact]
    public void A_temp_file_of_the_marker_or_the_id_that_is_a_dangling_link_stops_Begin_and_Resolve_before_anything_is_written_through_it()
    {
        foreach (var linked in new[] { SealIntent.FileName + ".tmp", SealIntent.FolderIdFileName + ".tmp" })
        {
            // Begin writes the marker and the id through these temp files: a link there would create its target wherever it points.
            var fresh = NewDir();
            var ghost = DanglingFileLink(Path.Combine(fresh, linked));

            var begin = () => SealIntent.Begin(fresh, SealIntent.KindSetup);

            begin.Should().Throw<SealIntentException>(linked).Which.Code.Should().Be(SealIntent.UnsafeCode, linked);
            File.Exists(ghost).Should().BeFalse(linked + ": nothing was created through the link");
            SealIntent.IsPending(fresh).Should().BeFalse(linked);

            var pending = PendingSetupWithWipeableLeftover();
            var ghostBeside = DanglingFileLink(Path.Combine(pending, linked));

            var result = SealIntent.Resolve(pending);

            result.Action.Should().Be(SealIntentAction.Refused, linked);
            result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode, linked);
            result.Refusal.Files.Should().Contain(linked);
            LeftoverIntact(pending).Should().BeTrue(linked);
            File.Exists(ghostBeside).Should().BeFalse(linked);
            SealIntent.Complete(pending);
        }
    }

    [FileSymlinkFact]
    public void A_database_or_a_sidecar_that_is_a_dangling_link_is_refused_by_Begin_and_by_Resolve_and_nothing_is_created_or_wiped()
    {
        foreach (var linked in new[] { "beememorybank.db", "chat.db", "beememorybank.db-wal", "chat.db-journal" })
        {
            var fresh = NewDir();
            var ghost = DanglingFileLink(Path.Combine(fresh, linked));

            var begin = () => SealIntent.Begin(fresh, SealIntent.KindSetup);

            begin.Should().Throw<SealIntentException>(linked).Which.Code.Should().Be(SealIntent.UnsafeCode, linked);
            File.Exists(ghost).Should().BeFalse(linked + ": nothing was created through the link");
            SealIntent.IsPending(fresh).Should().BeFalse(linked);

            var pending = PendingSetupWithWipeableLeftover();
            var ghostBeside = DanglingFileLink(Path.Combine(pending, linked));

            var result = SealIntent.Resolve(pending);

            result.Action.Should().Be(SealIntentAction.Refused, linked);
            result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode, linked);
            result.Refusal.Files.Should().Contain(linked);
            LeftoverIntact(pending).Should().BeTrue(linked + ": one link wipes nothing");
            File.Exists(ghostBeside).Should().BeFalse(linked);
            SealIntent.Complete(pending);
        }
    }

    [Fact]
    public void A_working_folder_that_is_a_dangling_link_is_refused_by_Begin_and_by_Resolve_and_nothing_is_created_behind_it()
    {
        var fresh = NewDir();
        var ghost = DanglingFolderLink(Path.Combine(fresh, "tmp"));

        var begin = () => SealIntent.Begin(fresh, SealIntent.KindSetup);

        begin.Should().Throw<SealIntentException>().Which.Code.Should().Be(SealIntent.UnsafeCode);
        Directory.Exists(ghost).Should().BeFalse("nothing was created behind the link");
        SealIntent.IsPending(fresh).Should().BeFalse();

        var pending = NewDir();
        var database = Path.Combine(pending, "beememorybank.db");
        PlainDb(database, Pool.PlainMain, LongAgo);
        SealIntent.Begin(pending, SealIntent.KindSetup);
        var before = File.ReadAllBytes(database);
        var ghostBeside = DanglingFolderLink(Path.Combine(pending, "tmp"));

        var result = SealIntent.Resolve(pending);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
        result.Refusal.Files.Should().Contain("tmp");
        File.ReadAllBytes(database).Should().Equal(before, "one link wipes nothing: the database stays as it was");
        Directory.Exists(ghostBeside).Should().BeFalse();
        SealIntent.Complete(pending);
    }

    [Fact]
    public void A_data_folder_that_is_a_dangling_link_is_refused_by_Begin_by_name_and_nothing_is_created_behind_it()
    {
        var link = Path.Combine(NewDir(), "data-link");
        var ghost = DanglingFolderLink(link);

        var begin = () => SealIntent.Begin(link, SealIntent.KindSetup);

        begin.Should().Throw<SealIntentException>().Which.Code.Should().Be(SealIntent.UnsafeCode);
        Directory.Exists(ghost).Should().BeFalse("nothing was created behind the link");
    }

    [FileSymlinkFact]
    public void A_seal_journal_that_is_a_dangling_link_is_unreadable_not_missing_and_the_intent_refuses_by_name_and_wipes_nothing_beside_it()
    {
        var dir = PendingSetupWithWipeableLeftover();
        var ghost = DanglingFileLink(SealJournal.PathFor(dir));

        SealJournal.Read(dir).State.Should().Be(SealJournalState.Unreadable, "an entry is there that the swap wrote or somebody planted: it is no missing journal");
        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused, "the link gate names a journal that is a link before anything is decided beside it (fix round 5)");
        result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
        result.Refusal.Files.Should().Contain(SealJournal.FileName);
        LeftoverIntact(dir).Should().BeTrue();
        File.Exists(ghost).Should().BeFalse();
        SealIntent.Complete(dir);
    }

    [FileSymlinkFact]
    public void A_re_key_journal_that_is_a_dangling_link_is_refused_like_a_linked_one_and_is_no_missing_journal()
    {
        var dir = Path.Combine(NewDir(), "vault");
        Directory.CreateDirectory(dir);
        var ghost = DanglingFileLink(RekeySwapJournal.PathFor(dir));

        var read = () => RekeySwapJournal.Read(dir);

        read.Should().Throw<InvalidDataException>("the journal is a link, not a file the swap wrote");
        File.Exists(ghost).Should().BeFalse("nothing was read or written through the link");
    }
}
