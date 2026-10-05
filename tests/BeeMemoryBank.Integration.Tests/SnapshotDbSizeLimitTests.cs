using System.Text;
using BeeMemoryBank.Api.Services;

namespace BeeMemoryBank.Integration.Tests;

// The "2 GB encryption limit" on the snapshot database. Before the fix the guard compared
// dbBytes.Length (an int, so at most int.MaxValue) against the 2 GiB long constant - always
// false (CS0652) - and it only ran after File.ReadAllBytesAsync had already tried to load the
// whole file, so an oversized database died with OutOfMemoryException instead of the intended
// clear error, and the decryption path had no check at all. Both directions now look at the
// file length before any full read; the tests inject a tiny limit (no 2 GB fixture file) and
// also pin the real default on plain long values.
public class SnapshotDbSizeLimitTests
{
    private static readonly byte[] Dek = new byte[32];

    // ── The real default limit, exercised directly on lengths (no files needed) ──

    [Fact]
    public void EnsureDbSizeLimit_OverTwoGiB_Throws()
    {
        // Strictly-greater semantics preserved from the original guard: exactly 2 GiB is allowed.
        var act = () => SnapshotService.EnsureDbSizeWithinEncryptableLimit(2L * 1024 * 1024 * 1024 + 1);

        act.Should().Throw<InvalidOperationException>().WithMessage("*exceeds the 2 GB encryption limit*");
    }

    [Fact]
    public void EnsureDbSizeLimit_JustBelowTwoGiB_DoesNotThrow()
    {
        var act = () => SnapshotService.EnsureDbSizeWithinEncryptableLimit(2L * 1024 * 1024 * 1024 - 1);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureDbSizeLimit_LargestPossibleByteArray_DoesNotThrow()
    {
        // The old guard's dead comparison, made live: dbBytes.Length can never exceed int.MaxValue,
        // so a real byte[] always fits - the limit only bites on file sizes above it.
        var act = () => SnapshotService.EnsureDbSizeWithinEncryptableLimit(int.MaxValue);

        act.Should().NotThrow();
    }

    // ── Encrypt direction ────────────────────────────────────────────────────

    [Fact]
    public async Task EncryptDbFile_OversizedDatabase_ThrowsTheClearError_BeforeReadingIntoMemory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bmb_dbsize_{Guid.NewGuid():N}.db");
        try
        {
            // 4 KB file with an injected 1 KB limit: over the limit, nowhere near any real
            // memory pressure - the old code would have loaded it happily and thrown nothing.
            await File.WriteAllBytesAsync(dbPath, new byte[4096]);

            var act = async () => await SnapshotService.EncryptDbFileAsync(dbPath, Dek, maxDbSize: 1024);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*exceeds the 2 GB encryption limit*");
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task EncryptDbFile_WithinTheLimit_StillEncryptsInPlace()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bmb_dbsize_{Guid.NewGuid():N}.db");
        try
        {
            var plaintext = Encoding.ASCII.GetBytes("small-vault-db-payload");
            await File.WriteAllBytesAsync(dbPath, plaintext);

            await SnapshotService.EncryptDbFileAsync(dbPath, Dek, maxDbSize: 1024 * 1024);

            var header = Encoding.ASCII.GetString((await File.ReadAllBytesAsync(dbPath))[..6]);
            header.Should().Be("BMBDB2", "the file was encrypted in place");

            await SnapshotService.DecryptDbFileAsync(dbPath, Dek, maxDbSize: 1024 * 1024);
            (await File.ReadAllBytesAsync(dbPath)).Should().Equal(plaintext, "the round trip restores the bytes");
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    // ── Decrypt direction ────────────────────────────────────────────────────

    [Fact]
    public async Task DecryptDbFile_OversizedEncryptedDatabase_ThrowsTheClearError_BeforeReadingIntoMemory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bmb_dbsize_{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllBytesAsync(dbPath, new byte[4096]);
            // Encrypt with the default (large) limit, then decrypt with a tiny one.
            await SnapshotService.EncryptDbFileAsync(dbPath, Dek);

            var act = async () => await SnapshotService.DecryptDbFileAsync(dbPath, Dek, maxDbSize: 1024);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*exceeds the 2 GB encryption limit*");
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task DecryptDbFile_UnencryptedDatabaseOverTheLimit_PassesThroughUntouched()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bmb_dbsize_{Guid.NewGuid():N}.db");
        try
        {
            // Not an encrypted blob (no BMBDB magic) and larger than the injected limit:
            // decrypt has nothing to do with it and must leave it alone.
            var bytes = Encoding.ASCII.GetBytes("plain sqlite database, definitely not BMBDB-framed");
            await File.WriteAllBytesAsync(dbPath, bytes);

            await SnapshotService.DecryptDbFileAsync(dbPath, Dek, maxDbSize: 8);

            (await File.ReadAllBytesAsync(dbPath)).Should().Equal(bytes);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
