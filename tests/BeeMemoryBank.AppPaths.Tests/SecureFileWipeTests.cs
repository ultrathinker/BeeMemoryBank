using System.Security.Cryptography;
using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// <see cref="SecureFileWipe"/> (docs/design/step-b-spec.md 10.6, Appendix A.6, red test 15.1.13): a plain file is overwritten with random
/// bytes, flushed, truncated and deleted; a file with another name is never overwritten through this one.
/// </summary>
public sealed class SecureFileWipeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_wipe_" + Guid.NewGuid().ToString("N"));

    public SecureFileWipeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] WithCanary(string canary, int length)
    {
        var bytes = new byte[length];
        var text = System.Text.Encoding.UTF8.GetBytes(canary);
        for (var i = 0; i + text.Length <= length; i += text.Length + 13) text.CopyTo(bytes, i);
        return bytes;
    }

    [Fact]
    public void The_content_is_overwritten_at_its_original_length_before_the_file_goes_and_no_canary_survives_in_the_bytes()
    {
        var canary = MetadataCanaries.Ascii("wipe");
        var original = WithCanary(canary, 200_000); // more than three 64 KiB blocks
        var path = Write("plain.db", original);
        MetadataCanaries.ScanFile(path, [canary]).Should().NotBeEmpty("the control: the file does hold the canary");
        byte[]? seen = null;

        var done = SecureFileWipe.TryWipe(path, afterOverwrite: p => seen = ReadShared(p));

        done.Should().BeTrue();
        seen.Should().NotBeNull("the seam runs after the overwrite");
        seen!.Length.Should().Be(original.Length, "the overwrite covers the file's whole length");
        MetadataCanaries.Scan(seen, [canary], "after overwrite").Should().BeEmpty();
        seen.Should().NotEqual(original);
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void An_absent_file_is_a_success_and_an_empty_file_is_removed()
    {
        SecureFileWipe.TryWipe(Path.Combine(_dir, "never-existed")).Should().BeTrue();

        var empty = Write("empty", []);
        SecureFileWipe.TryWipe(empty).Should().BeTrue();
        File.Exists(empty).Should().BeFalse();
    }

    [Fact]
    public void A_read_only_file_is_wiped_too()
    {
        var path = Write("ro.db", WithCanary("ro", 10_000));
        File.SetAttributes(path, FileAttributes.ReadOnly);

        SecureFileWipe.TryWipe(path).Should().BeTrue();

        File.Exists(path).Should().BeFalse();
    }

    [WindowsOnlyFact]
    public void A_file_another_program_holds_open_exclusively_returns_false_and_does_not_throw()
    {
        var path = Write("locked.db", WithCanary("lock", 10_000));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            SecureFileWipe.TryWipe(path).Should().BeFalse();
        }

        File.Exists(path).Should().BeTrue("a file that could not be wiped is still there for the retry at the next start");
        SecureFileWipe.TryWipe(path).Should().BeTrue("and the retry works once the lock is gone");
    }

    [UnixOnlyFact]
    public void A_file_in_a_folder_that_cannot_be_written_returns_false_and_does_not_throw()
    {
        if (Environment.UserName == "root") return; // root ignores permission bits: the assertion is meaningless there
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "fixed")).FullName;
        var path = Path.Combine(folder, "stuck.db");
        File.WriteAllBytes(path, WithCanary("stuck", 10_000));
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            SecureFileWipe.TryWipe(path).Should().BeFalse();
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_symbolic_link_is_removed_without_writing_through_it_and_the_target_keeps_its_content()
    {
        var canary = MetadataCanaries.Ascii("target");
        var target = Write("target.db", WithCanary(canary, 50_000));
        var link = Path.Combine(_dir, "link.db");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Windows creates file symbolic links only with a privilege (developer mode or an elevated session). Without it there is nothing
            // to test here; the reparse-point branch is also covered by the directory junction test.
            return;
        }
        var before = File.ReadAllBytes(target);
        byte[]? seenByHook = null;

        SecureFileWipe.TryWipe(link, afterOverwrite: p => seenByHook = ReadShared(p)).Should().BeTrue();

        File.Exists(link).Should().BeFalse("the link is gone");
        File.ReadAllBytes(target).Should().Equal(before, "nothing was written through the link");
        seenByHook.Should().BeNull("a link is never overwritten, so the overwrite seam never runs");
    }

    [Fact]
    public void A_file_with_another_name_is_not_overwritten_only_this_name_is_removed_and_the_other_name_keeps_every_byte()
    {
        var original = WithCanary(MetadataCanaries.Ascii("shared"), 150_000);
        var live = Write("live.db", original);
        var second = Path.Combine(_dir, "second-name.db");
        TestLinks.CreateHardLink(live, second);
        var asked = new List<string>();
        var hookRan = false;

        var done = SecureFileWipe.TryWipe(second, afterOverwrite: _ => hookRan = true, sharedWithAnotherName: p => { asked.Add(p); return true; });

        done.Should().BeTrue();
        asked.Should().ContainSingle("the callback is asked once, before anything is written").Which.Should().Be(second);
        hookRan.Should().BeFalse("nothing was overwritten");
        File.Exists(second).Should().BeFalse("this name is removed");
        File.ReadAllBytes(live).Should().Equal(original, "the other name keeps every byte");
    }

    [Fact]
    public void Without_the_callback_the_same_two_names_are_overwritten_together_which_is_why_the_callback_exists()
    {
        var original = WithCanary(MetadataCanaries.Ascii("shared"), 150_000);
        var live = Write("live.db", original);
        var second = Path.Combine(_dir, "second-name.db");
        TestLinks.CreateHardLink(live, second);

        SecureFileWipe.TryWipe(second).Should().BeTrue();

        File.Exists(second).Should().BeFalse();
        File.Exists(live).Should().BeTrue();
        var after = File.ReadAllBytes(live);
        after.Length.Should().Be(0, "the control: the wipe truncated the one file that both names are");
    }

    [Fact]
    public void A_callback_that_answers_false_overwrites_as_usual_and_one_that_throws_propagates()
    {
        var path = Write("plain.db", WithCanary("x", 10_000));
        SecureFileWipe.TryWipe(path, sharedWithAnotherName: _ => false).Should().BeTrue();
        File.Exists(path).Should().BeFalse();

        var other = Write("other.db", WithCanary("y", 10_000));
        var act = () => SecureFileWipe.TryWipe(other, sharedWithAnotherName: _ => throw new InvalidOperationException("identity unreadable"));
        act.Should().Throw<InvalidOperationException>("the caller decides what an unreadable identity means");
        File.Exists(other).Should().BeTrue("nothing was touched");
    }
}
