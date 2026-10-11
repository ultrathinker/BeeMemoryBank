using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// <see cref="FileIdentity"/> (docs/design/step-b-spec.md 10.2, red test 15.1.17): two names of one file have one identity and a link
/// count of two, a copy has another identity, an unreadable path has none. The Linux and macOS implementation (fstat) runs these same
/// tests at the platform gate (15.5).
/// </summary>
public sealed class FileIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_identity_" + Guid.NewGuid().ToString("N"));

    public FileIdentityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_single_file_has_a_link_count_of_one()
    {
        var path = Write("a.db", "content");

        var id = FileIdentity.TryGet(path);

        id.Should().NotBeNull();
        id!.Value.LinkCount.Should().Be(1);
        id.Value.Index.Should().NotBe(0);
    }

    [Fact]
    public void Two_names_of_one_file_have_the_same_volume_and_index_and_a_link_count_of_two()
    {
        var first = Write("first.db", "content");
        var second = Path.Combine(_dir, "second.db");
        TestLinks.CreateHardLink(first, second);

        var a = FileIdentity.TryGet(first);
        var b = FileIdentity.TryGet(second);

        a.Should().NotBeNull();
        b.Should().NotBeNull();
        a!.Value.Volume.Should().Be(b!.Value.Volume);
        a.Value.Index.Should().Be(b.Value.Index);
        a.Value.IsSameFileAs(b.Value).Should().BeTrue();
        a.Value.LinkCount.Should().Be(2);
        b.Value.LinkCount.Should().Be(2);

        File.Delete(second);
        FileIdentity.TryGet(first)!.Value.LinkCount.Should().Be(1, "removing a name takes the count back");
    }

    [Fact]
    public void A_copy_has_another_identity()
    {
        var original = Write("original.db", "content");
        var copy = Path.Combine(_dir, "copy.db");
        File.Copy(original, copy);

        var a = FileIdentity.TryGet(original)!.Value;
        var b = FileIdentity.TryGet(copy)!.Value;

        a.IsSameFileAs(b).Should().BeFalse();
        a.LinkCount.Should().Be(1);
        b.LinkCount.Should().Be(1);
    }

    [Fact]
    public void A_missing_file_and_a_missing_folder_have_no_identity()
    {
        FileIdentity.TryGet(Path.Combine(_dir, "nothing.db")).Should().BeNull();
        FileIdentity.TryGet(Path.Combine(_dir, "no-such-folder", "nothing.db")).Should().BeNull();
    }

    [Fact]
    public void A_path_with_a_bad_character_has_no_identity_instead_of_throwing()
    {
        FileIdentity.TryGet("\0").Should().BeNull();
        FileIdentity.TryGet("").Should().BeNull();
    }

    [WindowsOnlyFact]
    public void A_file_another_program_holds_exclusively_still_has_an_identity_because_the_handle_asks_for_attributes_only()
    {
        var path = Write("held.db", "content");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var id = FileIdentity.TryGet(path);

        id.Should().NotBeNull("an attribute-only handle conflicts with no share mode, so the live database of a running node is not 'unreadable'");
    }

    [Fact]
    public void A_symbolic_link_has_the_identity_of_its_target()
    {
        var target = Write("target.db", "content");
        var link = Path.Combine(_dir, "link.db");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // Windows creates file symbolic links only with a privilege; there is nothing to test without it
        }

        FileIdentity.TryGet(link)!.Value.IsSameFileAs(FileIdentity.TryGet(target)!.Value).Should().BeTrue();
    }

    [UnixOnlyFact]
    public void The_unix_identity_agrees_with_what_the_runtime_knows_of_a_file_by_hard_link_count()
    {
        // The fstat layouts differ by platform and architecture (spec 19.15): this is the check that they are read from the right offsets.
        var first = Write("first.db", "content");
        var second = Path.Combine(_dir, "second.db");
        var third = Path.Combine(_dir, "third.db");
        TestLinks.CreateHardLink(first, second);
        TestLinks.CreateHardLink(first, third);

        FileIdentity.TryGet(third)!.Value.LinkCount.Should().Be(3);
        FileIdentity.TryGet(first)!.Value.IsSameFileAs(FileIdentity.TryGet(second)!.Value).Should().BeTrue();
    }
}
