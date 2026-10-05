using System.Text;

namespace BeeMemoryBank.Rekey.Tests;

/// <summary>
/// On a Mac a vault's secrets in the Keychain are scoped by the id in <c>&lt;data&gt;/.secret-scope</c>. The re-keyed vault takes the old data
/// directory's place, so the id must be carried over with the other "carried over" entries, or the CA key, the ACME keys and the DDNS tokens
/// kept in the Keychain become unreachable (and the local CA is minted again). The file is not secret, and on Windows and Linux it does
/// not exist.
/// </summary>
public sealed class RekeyScopeFileCarryOverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-scope-carry-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private (string Old, string New) Dirs()
    {
        var old = Path.Combine(_root, "vault");
        var next = Path.Combine(_root, "vault.rekey-new");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(next);
        return (old, next);
    }

    [Fact]
    public void TheScopeFile_IsCarriedOver_ByteForByte()
    {
        var (old, next) = Dirs();
        var content = Encoding.ASCII.GetBytes("0123456789abcdef");
        File.WriteAllBytes(Path.Combine(old, ".secret-scope"), content);

        var copied = RekeySwap.CarryOver(old, next);

        copied.Should().Contain(".secret-scope");
        File.ReadAllBytes(Path.Combine(next, ".secret-scope")).Should().Equal(content);
    }

    [Fact]
    public void WithoutTheFile_NothingIsMade_ForIt()
    {
        var (old, next) = Dirs();

        var copied = RekeySwap.CarryOver(old, next);

        copied.Should().NotContain(".secret-scope");
        File.Exists(Path.Combine(next, ".secret-scope")).Should().BeFalse("a vault that never had an id does not get one from the swap");
    }
}
