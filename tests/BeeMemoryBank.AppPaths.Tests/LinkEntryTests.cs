using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// BMB-306 fix round 4 (H1): <see cref="LinkEntry"/> judges a link by the directory entry itself, never by whether what it points to is there. File.Exists, Directory.Exists and
/// FileSystemInfo.Exists look through a link where the platform resolves it, and what they answer for a link to nothing differs between platforms and runtime versions; the
/// answers here must not.
/// </summary>
public sealed class LinkEntryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb_linkentry_" + Guid.NewGuid().ToString("N"));

    public LinkEntryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Sub(string name) => Path.Combine(_root, name);

    private string NewFolder(string name)
    {
        Directory.CreateDirectory(Sub(name));
        return Sub(name);
    }

    [Fact]
    public void A_regular_file_a_folder_and_a_path_that_is_not_there_are_no_links()
    {
        var file = Sub("plain.txt");
        File.WriteAllText(file, "x");
        var folder = NewFolder("plain-folder");

        LinkEntry.IsLink(file).Should().BeFalse();
        LinkEntry.IsLink(folder).Should().BeFalse();
        LinkEntry.IsLink(Sub("nothing-here")).Should().BeFalse();
        LinkEntry.IsLink(Path.Combine(Sub("no-such-parent"), "nothing-here")).Should().BeFalse("a missing parent is a missing entry, not a link");

        LinkEntry.Exists(file).Should().BeTrue();
        LinkEntry.Exists(folder).Should().BeFalse("a plain folder is not a file or a link: it is no marker");
        LinkEntry.Exists(Sub("nothing-here")).Should().BeFalse();
    }

    [Fact]
    public void A_folder_link_is_a_link_whether_or_not_its_target_is_there()
    {
        var target = NewFolder("target-folder");
        var link = Sub("folder-link");
        TestDirectoryLinks.Create(link, target);

        LinkEntry.IsLink(link).Should().BeTrue("a live link to a folder");
        LinkEntry.Exists(link).Should().BeTrue();

        Directory.Delete(target);   // the test's own fixture: the link now points at nothing

        LinkEntry.IsLink(link).Should().BeTrue("a link to a folder that is gone is still a link");
        LinkEntry.Exists(link).Should().BeTrue("and it is there: it is not 'nothing pending'");
        LinkEntry.IsLink(Path.Combine(link, "child")).Should().BeFalse("nothing is there below a link to nothing");
    }

    [SealIntentBindingTests.FileSymlinkFact]
    public void A_file_link_is_a_link_whether_or_not_its_target_is_there()
    {
        var target = Sub("target.txt");
        File.WriteAllText(target, "x");
        var link = Sub("file-link");
        File.CreateSymbolicLink(link, target);

        LinkEntry.IsLink(link).Should().BeTrue("a live link to a file");
        LinkEntry.Exists(link).Should().BeTrue();

        File.Delete(target);   // the test's own fixture: the link now points at nothing

        LinkEntry.IsLink(link).Should().BeTrue("a link to a file that is gone is still a link");
        LinkEntry.Exists(link).Should().BeTrue("and it is there: a marker or a journal that is a link to nothing is not 'no marker, no journal'");
        File.Exists(target).Should().BeFalse("asking did not create what it points to");
    }
}
