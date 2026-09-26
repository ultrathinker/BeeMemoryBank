using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Profiles.Tests;

public sealed class VaultCopierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BmbVaultCopier_" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "vault");

    public VaultCopierTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "media", "2026"));
        File.WriteAllBytes(Path.Combine(Source, "beememorybank.db"),
            Encoding.ASCII.GetBytes("SQLite format 3\0").Concat(new byte[10_000]).ToArray());
        File.WriteAllText(Path.Combine(Source, "beememorybank.db-wal"), "wal");
        File.WriteAllText(Path.Combine(Source, "media", "2026", "a.png"), "png");
        File.WriteAllText(Path.Combine(Source, "node.lock"), "");
        File.WriteAllText(Path.Combine(Source, "node.status.json"), "{}");
        File.WriteAllText(Path.Combine(Source, "web.ready"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void CopyVerified_CopiesDataButNotRuntimeFiles_AndLeavesSourceAlone()
    {
        var target = Path.Combine(_root, "new-place");

        var result = VaultCopier.CopyVerified(Source, target, progress: null, CancellationToken.None);

        result.FileCount.Should().Be(3);
        File.ReadAllText(Path.Combine(target, "media", "2026", "a.png")).Should().Be("png");
        new FileInfo(Path.Combine(target, "beememorybank.db")).Length.Should().Be(10_016);
        File.Exists(Path.Combine(target, "beememorybank.db-wal")).Should().BeTrue();
        File.Exists(Path.Combine(target, "node.lock")).Should().BeFalse();
        File.Exists(Path.Combine(target, "node.status.json")).Should().BeFalse();
        File.Exists(Path.Combine(target, "web.ready")).Should().BeFalse();
        Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories).Should().HaveCount(6);
    }

    [Fact]
    public void CopyVerified_IntoExistingEmptyFolder_Works()
    {
        var target = Path.Combine(_root, "empty");
        Directory.CreateDirectory(target);

        VaultCopier.CopyVerified(Source, target, null, CancellationToken.None).FileCount.Should().Be(3);
    }

    [Fact]
    public void ValidateTarget_RefusesUnsafeTargets()
    {
        VaultCopier.ValidateTarget(Source, Source).Should().NotBeNull("same folder");
        VaultCopier.ValidateTarget(Source, Source + Path.DirectorySeparatorChar).Should().NotBeNull("same folder, trailing slash");
        VaultCopier.ValidateTarget(Source, Path.Combine(Source, "media", "x")).Should().NotBeNull("inside the source");
        VaultCopier.ValidateTarget(Source, _root).Should().NotBeNull("contains the source");

        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "note.txt"), "x");
        VaultCopier.ValidateTarget(Source, other).Should().NotBeNull("not empty");

        var anotherVault = Path.Combine(_root, "another-vault");
        Directory.CreateDirectory(anotherVault);
        File.Copy(Path.Combine(Source, "beememorybank.db"), Path.Combine(anotherVault, "beememorybank.db"));
        VaultCopier.ValidateTarget(Source, anotherVault).Should().Contain("already contains");

        VaultCopier.ValidateTarget(Source, Path.Combine(_root, "brand-new")).Should().BeNull();
        VaultCopier.ValidateTarget(Source, Path.Combine(_root, "vault-2")).Should().BeNull("a sibling with a shared name prefix is not 'inside'");
    }

    [Fact]
    public void CopyVerified_RefusesInvalidTarget_WithoutWritingAnything()
    {
        var inside = Path.Combine(Source, "nested");

        var act = () => VaultCopier.CopyVerified(Source, inside, null, CancellationToken.None);

        act.Should().Throw<ArgumentException>();
        Directory.Exists(inside).Should().BeFalse();
    }
}
