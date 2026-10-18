using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// BMB-306 fix round 5 (J1): the link check is the FIRST thing every entry point does, before any "does it exist" question, any probe, any read and any swap or wipe, and it
/// covers the data folder itself and every reserved name inside it (the databases, their sidecars, the boot store, the marker, the folder id, their temp files, the working
/// folder). A link - to something or to nothing - is refused by name, and nothing is read, written, moved or wiped through it.
/// <para>
/// The scenarios are the ones that a check made AFTER the first look gets wrong on Windows too: a data folder that is a link (live or dangling) was classified only after
/// <c>Directory.Exists</c>, <c>IsPending</c> and <c>KindOf</c> had already looked through it; the swap resolvers of a start ran before the intent resolver and acted through it;
/// <c>VaultStoreProbe.IsSealedPair</c> read a linked boot store, database or sidecar before the link inventory refused it, and a node whose pair looked sealed through the
/// links was "completed"; and a dangling boot entry was missing from the inventory.
/// </para>
/// </summary>
public sealed class LinkGateEntryPointTests : IClassFixture<SealIntentTests.Corpus>
{
    private readonly SealIntentTests.Corpus _corpus;
    private readonly string _canary = MetadataCanaries.Ascii("linkgate");

    public LinkGateEntryPointTests(SealIntentTests.Corpus corpus) => _corpus = corpus;

    private SealedCorpus.Pool Pool => _corpus.Pool;

    private string NewDir()
    {
        var dir = Path.Combine(_corpus.Root, "g-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- links the account can make --------------------------------------------------------------------------------------------------------

    private static bool CanLinkFiles()
    {
        var probe = Path.Combine(Path.GetTempPath(), "bmb_gate_probe_" + Guid.NewGuid().ToString("N"));
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

    private const string NoFileLinks = "This account cannot create file symbolic links (Windows needs a privilege for them); the junction tests still run.";

    private sealed class LinkFactAttribute : FactAttribute
    {
        public LinkFactAttribute()
        {
            if (!CanLinkFiles()) Skip = NoFileLinks;
        }
    }

    private sealed class LinkTheoryAttribute : TheoryAttribute
    {
        public LinkTheoryAttribute()
        {
            if (!CanLinkFiles()) Skip = NoFileLinks;
        }
    }

    /// <summary>A file that is a copy of <paramref name="source"/> (or empty), in a folder of its own outside any data folder: what a link points at.</summary>
    private string Outside(string? source = null)
    {
        var path = Path.Combine(NewDir(), "outside.bin");
        if (source is null) File.WriteAllBytes(path, []);
        else File.Copy(source, path);
        return path;
    }

    /// <summary>A folder link (a junction on Windows, a symbolic link elsewhere) to a real folder.</summary>
    private static void FolderLink(string link, string target) => TestDirectoryLinks.Create(link, target);

    /// <summary>A folder link to a folder that is gone.</summary>
    private string DanglingFolderLink(string link)
    {
        var target = Path.Combine(NewDir(), "ghost-folder");
        Directory.CreateDirectory(target);
        TestDirectoryLinks.Create(link, target);
        Directory.Delete(target);   // the test's own fixture
        return target;
    }

    private string DanglingFileLink(string link)
    {
        var target = Path.Combine(NewDir(), "ghost-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        File.WriteAllText(target, _canary);
        File.CreateSymbolicLink(link, target);
        File.Delete(target);   // the test's own fixture
        return target;
    }

    // ---- nodes -----------------------------------------------------------------------------------------------------------------------------

    /// <summary>An encrypted pair (database, chat database, boot store): what a finished node looks like.</summary>
    private void SealedPair(string dir)
    {
        File.Copy(Pool.SealedMain, Path.Combine(dir, "beememorybank.db"));
        File.Copy(Pool.SealedChat, Path.Combine(dir, "chat.db"));
        SealedCorpus.MakeBoot(Path.Combine(dir, "beememorybank.boot.db"));
    }

    private const string Leftover = "beememorybank.db.seal-old";

    /// <summary>An encrypted pair and a plain file the resolver of an interrupted encryption would wipe (a replaced database): what "acting through the folder" would remove.</summary>
    private string NodeWithWipeableLeftover()
    {
        var dir = NewDir();
        SealedPair(dir);
        File.WriteAllText(Path.Combine(dir, Leftover), _canary);
        return dir;
    }

    private static bool LeftoverIntact(string dir) => File.Exists(Path.Combine(dir, Leftover));

    /// <summary>A folder with a pending setup marker and something the intent's wipe would remove.</summary>
    private string PendingSetupWithWipeableLeftover()
    {
        var dir = NewDir();
        SealIntent.Begin(dir, SealIntent.KindSetup);
        Directory.CreateDirectory(Path.Combine(dir, "tmp"));
        File.WriteAllText(Path.Combine(dir, "tmp", "archive.tar.gz"), _canary);
        return dir;
    }

    private static bool WipeableIntact(string dir) => File.Exists(Path.Combine(dir, "tmp", "archive.tar.gz"));

    // ---- the reserved names ----------------------------------------------------------------------------------------------------------------

    private static readonly string[] Sidecars = ["", "-wal", "-shm", "-journal"];
    private static readonly string[] SealNewSidecars = [".seal-new", ".seal-new-wal", ".seal-new-shm", ".seal-new-journal"];

    /// <summary>Every name a start, a swap, a probe or the intent reads, writes, moves or wipes at the top of a data folder (the working folder is tested on its own).</summary>
    public static IEnumerable<object[]> ReservedNames()
    {
        foreach (var store in new[] { "beememorybank.db", "chat.db" })
            foreach (var suffix in Sidecars.Concat(SealNewSidecars))
                yield return [store + suffix];
        foreach (var suffix in Sidecars.Concat(SealNewSidecars))
            yield return ["beememorybank.boot.db" + suffix];
        yield return [SealIntent.FileName];
        yield return [SealIntent.FileName + ".tmp"];
        yield return [SealIntent.FolderIdFileName];
        yield return [SealIntent.FolderIdFileName + ".tmp"];
        yield return [SealJournal.FileName];
        yield return [SealJournal.FileName + ".tmp"];
        yield return [Path.GetFileName(SealJournal.UnreadablePathFor("d"))];
    }

    [LinkTheory]
    [MemberData(nameof(ReservedNames))]
    public void A_live_link_at_any_reserved_name_beside_an_encrypted_pair_is_refused_before_the_pair_is_probed_through_it(string name)
    {
        var dir = NewDir();
        SealedPair(dir);
        File.WriteAllText(SealIntent.PathFor(dir), "garbage");   // beside an encrypted pair a marker only goes: the pair is the proof, so a probe that reads through a link "completes" it
        var path = Path.Combine(dir, name);
        string outside;
        if (File.Exists(path) && name != SealIntent.FileName)
        {
            outside = Outside(path);
            File.Delete(path);
        }
        else
        {
            if (File.Exists(path)) File.Delete(path);
            outside = Outside();
        }
        File.CreateSymbolicLink(path, outside);
        var before = File.ReadAllBytes(outside);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused, name);
        result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode, name);
        result.Refusal.Files.Should().Contain(name);
        File.ReadAllBytes(outside).Should().Equal(before, name + ": nothing was written through the link");
        File.Exists(Path.Combine(dir, "beememorybank.db")).Should().BeTrue(name);
    }

    [LinkTheory]
    [MemberData(nameof(ReservedNames))]
    public void A_dangling_link_at_any_reserved_name_is_refused_by_name_and_nothing_is_wiped_or_created(string name)
    {
        var dir = PendingSetupWithWipeableLeftover();
        var path = Path.Combine(dir, name);
        if (name == SealIntent.FileName || name == SealIntent.FolderIdFileName) File.Delete(path);   // Begin wrote these two: the link takes their place
        var ghost = DanglingFileLink(path);

        var result = SealIntent.Resolve(dir);

        result.Action.Should().Be(SealIntentAction.Refused, name);
        result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode, name);
        result.Refusal.Files.Should().Contain(name);
        WipeableIntact(dir).Should().BeTrue(name + ": one link wipes nothing");
        File.Exists(ghost).Should().BeFalse(name + ": nothing was created through the link");
        SealIntent.Complete(dir);
    }

    // ---- the data folder itself ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_data_folder_that_is_a_live_link_is_refused_first_by_Resolve_before_its_marker_is_read_even_with_nothing_pending()
    {
        // With a marker behind the link: refused, and the kind of the operation is NOT read from the marker through the link.
        var withMarker = PendingSetupWithWipeableLeftover();
        var link = Path.Combine(NewDir(), "data-link");
        FolderLink(link, withMarker);

        var result = SealIntent.Resolve(link);

        result.Action.Should().Be(SealIntentAction.Refused);
        result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
        result.Refusal.Files.Should().Contain("data-link");
        result.Kind.Should().Be("unknown", "the marker behind a link is not read, not even for its kind");
        WipeableIntact(withMarker).Should().BeTrue("nothing behind the link was wiped");
        SealIntent.IsPending(withMarker).Should().BeTrue();
        SealIntent.Complete(withMarker);

        // With nothing pending behind it: still a refusal by name, not "nothing to do".
        var clean = NodeWithWipeableLeftover();
        var cleanLink = Path.Combine(NewDir(), "data-link-2");
        FolderLink(cleanLink, clean);

        var cleanResult = SealIntent.Resolve(cleanLink);

        cleanResult.Action.Should().Be(SealIntentAction.Refused);
        cleanResult.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
        LeftoverIntact(clean).Should().BeTrue();
    }

    [Fact]
    public void A_data_folder_that_is_a_dangling_link_is_refused_first_by_Resolve_not_answered_with_nothing_to_do()
    {
        var link = Path.Combine(NewDir(), "data-link");
        var ghost = DanglingFolderLink(link);

        var result = SealIntent.Resolve(link);

        result.Action.Should().Be(SealIntentAction.Refused, "a data folder that is a link to nothing is not 'no data folder, nothing pending'");
        result.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
        result.Refusal.Files.Should().Contain("data-link");
        Directory.Exists(ghost).Should().BeFalse();
    }

    [Fact]
    public void A_start_over_a_data_folder_that_is_a_link_live_or_dangling_is_refused_by_name_before_any_swap_resolver_acts_through_it()
    {
        var node = NodeWithWipeableLeftover();
        var live = Path.Combine(NewDir(), "data-live");
        FolderLink(live, node);
        var dangling = Path.Combine(NewDir(), "data-dangling");
        DanglingFolderLink(dangling);

        foreach (var link in new[] { live, dangling })
        {
            var (resolution, lease) = VaultStartup.Enter(link, TimeSpan.FromSeconds(2));
            using (lease)
            {
                resolution.Seal.Should().NotBeNull(link);
                resolution.Seal!.Action.Should().Be(SealResolveAction.Refused, link);
                resolution.Seal.Refusal!.Code.Should().Be(SealIntent.UnsafeCode, link);
            }
        }
        LeftoverIntact(node).Should().BeTrue("the swap resolver did not wipe the leftover behind the link");
    }

    [LinkFact]
    public void A_start_over_a_folder_whose_reserved_name_is_a_link_is_refused_by_name_before_any_swap_resolver_acts()
    {
        var dir = NodeWithWipeableLeftover();
        var outside = Outside(Path.Combine(dir, "beememorybank.db"));
        File.Delete(Path.Combine(dir, "beememorybank.db"));
        File.CreateSymbolicLink(Path.Combine(dir, "beememorybank.db"), outside);
        var before = File.ReadAllBytes(outside);

        var (resolution, lease) = VaultStartup.Enter(dir, TimeSpan.FromSeconds(2));
        using (lease)
        {
            resolution.Seal.Should().NotBeNull();
            resolution.Seal!.Action.Should().Be(SealResolveAction.Refused);
            resolution.Seal.Refusal!.Code.Should().Be(SealIntent.UnsafeCode);
            resolution.Seal.Refusal.Files.Should().Contain("beememorybank.db");
        }
        LeftoverIntact(dir).Should().BeTrue("the leftover was not wiped on the strength of a pair that is sealed only through a link");
        File.ReadAllBytes(outside).Should().Equal(before);
    }

    // ---- the entry points on their own -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_resolver_refuses_a_data_folder_that_is_a_link_on_its_own_and_acts_through_nothing()
    {
        var node = NodeWithWipeableLeftover();
        var live = Path.Combine(NewDir(), "data-live");
        FolderLink(live, node);
        var dangling = Path.Combine(NewDir(), "data-dangling");
        DanglingFolderLink(dangling);

        foreach (var link in new[] { live, dangling })
        {
            var swap = SealSwapResolver.Resolve(link);
            swap.Action.Should().Be(SealResolveAction.Refused, "SealSwapResolver.Resolve " + link);
            swap.Refusal!.Code.Should().Be(SealIntent.UnsafeCode, link);

            var retry = SealSwapResolver.RetryWipe(link);
            retry.Action.Should().Be(SealResolveAction.Refused, "SealSwapResolver.RetryWipe " + link);

            var forward = SealSwapResolver.GoForward(link, SealJournal.New(SealJournal.Swapping, [], ""));
            forward.Action.Should().Be(SealResolveAction.Refused, "SealSwapResolver.GoForward " + link);

            var rekey = RekeySwapResolver.Resolve(link);
            rekey.Seal.Should().NotBeNull("RekeySwapResolver.Resolve " + link);
            rekey.Seal!.Refusal!.Code.Should().Be(SealIntent.UnsafeCode, link);

            var begin = () => SealIntent.Begin(link, SealIntent.KindSetup);
            begin.Should().Throw<SealIntentException>(link).Which.Code.Should().Be(SealIntent.UnsafeCode, link);
        }
        LeftoverIntact(node).Should().BeTrue("no resolver wiped the leftover behind the link");
    }

    [LinkFact]
    public void The_swap_resolvers_refuse_a_folder_whose_reserved_name_is_a_link_on_their_own()
    {
        var dir = NodeWithWipeableLeftover();
        var boot = Path.Combine(dir, "beememorybank.boot.db");
        var outside = Outside(boot);
        File.Delete(boot);
        File.CreateSymbolicLink(boot, outside);

        SealSwapResolver.Resolve(dir).Action.Should().Be(SealResolveAction.Refused);
        SealSwapResolver.RetryWipe(dir).Action.Should().Be(SealResolveAction.Refused);
        SealSwapResolver.GoForward(dir, SealJournal.New(SealJournal.Swapping, [], "")).Action.Should().Be(SealResolveAction.Refused);
        RekeySwapResolver.Resolve(dir).Seal!.Action.Should().Be(SealResolveAction.Refused);
        LeftoverIntact(dir).Should().BeTrue();
    }

    [LinkFact]
    public void The_probe_refuses_a_linked_input_on_its_own()
    {
        var dir = NewDir();
        SealedPair(dir);
        VaultStoreProbe.IsSealedPair(dir).Should().BeTrue("control: the pair is sealed");

        foreach (var linked in new[] { "beememorybank.boot.db", "beememorybank.db", "chat.db" })
        {
            var node = NewDir();
            SealedPair(node);
            var path = Path.Combine(node, linked);
            var outside = Outside(path);
            File.Delete(path);
            File.CreateSymbolicLink(path, outside);

            VaultStoreProbe.IsSealedPair(node).Should().BeFalse(linked + " is a link: the pair is not probed through it");
        }

        foreach (var sidecar in new[] { "beememorybank.db-wal", "beememorybank.db-journal", "chat.db-wal", "chat.db-journal" })
        {
            var node = NewDir();
            SealedPair(node);
            File.CreateSymbolicLink(Path.Combine(node, sidecar), Outside());   // an empty file would read as "fine" through the link

            VaultStoreProbe.IsSealedPair(node).Should().BeFalse(sidecar + " is a link");
        }

        var plain = Outside(Pool.PlainMain);
        var plainLink = Path.Combine(NewDir(), "plain-link");
        File.CreateSymbolicLink(plainLink, plain);
        VaultStoreProbe.HasSqliteMagic(plainLink).Should().BeFalse("not opened through the link");
        VaultStoreProbe.IsBootStore(plainLink).Should().BeFalse();
        VaultStoreProbe.ReadApplicationId(plainLink).Should().Be(0);
        VaultStoreProbe.InspectCompanion(plainLink).Should().Be(ProbeFileState.Unreadable, "a link is refused (fail closed), not read as a plain database");
    }
}
