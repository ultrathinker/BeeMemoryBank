using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// BMB-306 fix round 5 (J1): the inventory of <see cref="LinkGate"/> is the single list the entry points refuse links by, so it must cover every name the wipe inventory, the marker
/// and the swaps work on, and it must judge the folder itself by its own entry, with or without a trailing separator.
/// </summary>
public sealed class LinkGateTests : IClassFixture<SealIntentTests.Corpus>
{
    private readonly SealIntentTests.Corpus _corpus;

    public LinkGateTests(SealIntentTests.Corpus corpus) => _corpus = corpus;

    private string NewDir()
    {
        var dir = Path.Combine(_corpus.Root, "lg-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void The_reserved_names_cover_the_wipe_inventory_the_marker_files_and_the_swap_names_so_the_lists_cannot_drift()
    {
        var reserved = LinkGate.ReservedNames;
        reserved.Should().OnlyHaveUniqueItems();
        reserved.Should().OnlyContain(name => !name.Contains('/') && !name.Contains('\\'), "they are names directly inside the data folder");

        // The wipe inventory, with the boot store ours (the case that lists the most) and without it.
        var withBoot = NewDir();
        SealedCorpus.MakeBoot(Path.Combine(withBoot, VaultStoreProbe.Boot));
        SealIntent.Artifacts(withBoot).Should().OnlyContain(name => reserved.Contains(name), "every name the wipe can touch is judged by the gate first");
        SealIntent.Artifacts(NewDir()).Should().OnlyContain(name => reserved.Contains(name));

        SealIntent.MarkerFiles().Should().OnlyContain(name => reserved.Contains(name));
        reserved.Should().Contain([SealJournal.FileName, SealJournal.FileName + ".tmp", SealJournal.FileName + SealJournal.UnreadableSuffix, SealIntent.WorkingFolder]);

        // The names the swap resolver renames (live, target and the sidecars of both) and the probe opens.
        foreach (var store in new[] { VaultStoreProbe.Database, VaultStoreProbe.Chat, VaultStoreProbe.Boot })
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                reserved.Should().Contain(store + suffix);
                reserved.Should().Contain(store + VaultStoreProbe.SealNewSuffix + suffix);
            }
        VaultStoreProbe.Companions.Should().OnlyContain(name => reserved.Contains(name));
    }

    [Fact]
    public void A_folder_with_no_link_has_an_empty_inventory_and_no_refusal()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "beememorybank.db"), "x");
        Directory.CreateDirectory(Path.Combine(dir, "tmp"));

        LinkGate.Linked(dir).Should().BeEmpty();
        LinkGate.Refusal(dir).Should().BeNull();
        LinkGate.RefusedStart(dir).Should().BeNull();
        LinkGate.Linked(Path.Combine(dir, "not-there")).Should().BeEmpty("a folder that is not there is not a link");
    }

    [Fact]
    public void The_folder_itself_is_judged_by_its_own_entry_with_or_without_a_trailing_separator_and_nothing_inside_it_is_looked_at()
    {
        var real = NewDir();
        File.WriteAllText(Path.Combine(real, "beememorybank.db"), "x");
        var link = Path.Combine(NewDir(), "data-link");
        TestDirectoryLinks.Create(link, real);

        foreach (var path in new[] { link, link + Path.DirectorySeparatorChar })
        {
            LinkGate.Linked(path).Should().Equal("data-link");
            var refusal = LinkGate.Refusal(path)!;
            refusal.Code.Should().Be(SealIntent.UnsafeCode);
            refusal.Files.Should().Equal("data-link");
            refusal.Message.Should().Contain("data folder").And.Contain("link").And.NotContain(real);
            LinkGate.RefusedStart(path)!.Seal!.Action.Should().Be(SealResolveAction.Refused);
        }
    }

    [Fact]
    public void A_dangling_data_folder_link_is_in_the_inventory_too()
    {
        var target = Path.Combine(NewDir(), "ghost");
        Directory.CreateDirectory(target);
        var link = Path.Combine(NewDir(), "data-link");
        TestDirectoryLinks.Create(link, target);
        Directory.Delete(target);   // the test's own fixture

        LinkGate.Linked(link).Should().Equal("data-link");
        LinkGate.Refusal(link)!.Code.Should().Be(SealIntent.UnsafeCode);
    }
}
