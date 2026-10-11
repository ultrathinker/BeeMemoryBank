using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Vault.Sealed;

/// <param name="VaultSource">The live plain vault database.</param>
/// <param name="VaultTarget">The sealed copy (<c>beememorybank.db.seal-new</c>).</param>
/// <param name="ChatSource">The live plain chat database, or null when the node has none.</param>
/// <param name="ChatTarget">The sealed copy of the chat database, or null.</param>
/// <param name="BootTarget">The boot store written for the targets.</param>
/// <param name="DatabaseKey">The database key the targets are sealed under.</param>
/// <param name="Generation">Its generation (1 at the migration).</param>
/// <param name="CandidateDeks">The master DEKs of the session, current first; the verifier copies what it needs and never clears them.</param>
public sealed record SealVerifyInput(string VaultSource, string VaultTarget, string? ChatSource, string? ChatTarget, string BootTarget,
    byte[] DatabaseKey, int Generation, IReadOnlyList<byte[]> CandidateDeks);

/// <summary>
/// Verification of the migration targets before the journal says <c>verified</c> (docs/design/step-b-spec.md 10.5, MUST): the targets are read
/// through keyed connections and compared with the live files. Any difference is a <see cref="SealMigrationException"/> with the code of the check
/// that failed (<c>verify_integrity</c>, <c>verify_counts</c>, <c>verify_sentinel</c>, <c>verify_bodies</c>, <c>verify_tree</c>, <c>verify_scan</c>,
/// <c>verify_boot</c>); the node is unchanged. Every connection and pool it opens is closed before it returns, so the targets can be renamed.
/// </summary>
public static class SealVerifier
{
    private const int SampleCount = 20;
    private const int MinNeedleBytes = 8;

    private static ReadOnlySpan<byte> SqliteMagic => "SQLite format 3\0"u8;

    public static async Task VerifyAsync(SealVerifyInput input, CancellationToken ct = default)
    {
        var tdek = new IDEK(input.CandidateDeks);
        var service = new DatabaseKeyService(new BootStore(input.BootTarget), new BootDbKeyStore(new BootStoreConnectionFactory(input.BootTarget)));
        byte[] vaultKey = service.DerivePageKey(input.DatabaseKey, DbPurposes.Vault);
        byte[] chatKey = service.DerivePageKey(input.DatabaseKey, DbPurposes.Chat);
        try
        {
            var vaultKeys = new InMemoryVaultKeySource(vaultKey, chatKey);

            await VerifyFileAsync(input.VaultSource, input.VaultTarget, vaultKey, "vault");
            if (input.ChatSource is not null && input.ChatTarget is not null)
                await VerifyFileAsync(input.ChatSource, input.ChatTarget, chatKey, "chat");

            // The vault's own checks: log position, migration ledger, sentinel, bodies, tree, search.
            using (var source = OpenPlain(input.VaultSource))
            using (var target = SealedSqlite.OpenKeyed(input.VaultTarget, vaultKey, readOnly: true))
            {
                Counts(source, target, input.VaultTarget);
                LogPosition(source, target);
                Sentinel(target, tdek);
                var sample = Tree(source, target);
                await Bodies(input.VaultTarget, vaultKeys, target, tdek);
                Search(source, target, sample.Titles);
                RawScan(input.VaultTarget, sample);
            }
            if (input.ChatTarget is not null) RawScan(input.ChatTarget, new Sample([], []));

            await BootAsync(input, service, tdek, vaultKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultKey);
            CryptographicOperations.ZeroMemory(chatKey);
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class IDEK(IReadOnlyList<byte[]> candidates)
    {
        public IReadOnlyList<byte[]> Candidates { get; } = candidates;
    }

    private sealed record Sample(IReadOnlyList<string> Titles, IReadOnlyList<string> Paths);

    private static SqliteConnection OpenPlain(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    // ---- 1. integrity -----------------------------------------------------------------------------------------------------------------

    private static async Task VerifyFileAsync(string sourcePath, string targetPath, byte[] key, string label)
    {
        try
        {
            using var target = SealedSqlite.OpenKeyed(targetPath, key, readOnly: true);
            var integrity = (await target.QueryAsync<string>("PRAGMA integrity_check")).ToList();
            if (integrity.Count != 1 || integrity[0] != "ok")
                throw new SealMigrationException(SealFailures.VerifyIntegrity, $"The encrypted copy of the {label} database failed its integrity check.");
            var foreignKeys = (await target.QueryAsync("PRAGMA foreign_key_check")).ToList();
            if (foreignKeys.Count > 0)
                throw new SealMigrationException(SealFailures.VerifyIntegrity, $"The encrypted copy of the {label} database has broken references.");
            using var source = OpenPlain(sourcePath);
            var sourceTables = Tables(source);
            var targetTables = Tables(target);
            if (!sourceTables.SequenceEqual(targetTables, StringComparer.Ordinal))
                throw new SealMigrationException(SealFailures.VerifyCounts, $"The encrypted copy of the {label} database does not have the tables of the original.");
            foreach (var table in sourceTables)
            {
                var (inSource, inTarget) = (RowCount(source, table), RowCount(target, table));
                if (inSource != inTarget)
                    throw new SealMigrationException(SealFailures.VerifyCounts,
                        $"The encrypted copy of the {label} database holds a different number of rows than the original (table {table}: {inSource} and {inTarget}).");
            }
        }
        catch (VaultKeyMismatchException)
        {
            throw new SealMigrationException(SealFailures.VerifyIntegrity, $"The encrypted copy of the {label} database does not open with its key.");
        }
        catch (SqliteException ex)
        {
            throw new SealMigrationException(SealFailures.VerifyIntegrity, $"The encrypted copy of the {label} database could not be read (SQLite result {ex.SqliteErrorCode}).");
        }
    }

    private static List<string> Tables(SqliteConnection c) =>
        c.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name").ToList();

    private static long RowCount(SqliteConnection c, string table)
    {
        try { return c.ExecuteScalar<long>($"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\""); }
        catch (SqliteException) { return -1; }   // a virtual table whose module is not loaded: -1 on both sides compares equal
    }

    // ---- 2. counts, the ledger and the log position -------------------------------------------------------------------------------------

    private static void Counts(SqliteConnection source, SqliteConnection target, string targetPath)
    {
        string Ledger(SqliteConnection c) => Hash(c.Query<(long, string)>("SELECT version, filename FROM tbl_migration ORDER BY version, filename").Select(r => $"{r.Item1}|{r.Item2}"));
        if (Ledger(source) != Ledger(target))
            throw new SealMigrationException(SealFailures.VerifyCounts, "The encrypted copy has a different migration history than the original.");
    }

    private static void LogPosition(SqliteConnection source, SqliteConnection target)
    {
        long? Max(SqliteConnection c) => c.ExecuteScalar<long?>("SELECT MAX(sequence_num) FROM tbl_event");
        long? Seq(SqliteConnection c) => c.ExecuteScalar<long?>("SELECT seq FROM sqlite_sequence WHERE name = 'tbl_event'");
        if (Max(source) != Max(target) || Seq(source) != Seq(target))
            throw new SealMigrationException(SealFailures.VerifyCounts, "The log position of the encrypted copy differs from the original: no log position may be lowered or reused.");
    }

    // ---- 3. the sentinel --------------------------------------------------------------------------------------------------------------

    private static void Sentinel(SqliteConnection target, IDEK deks)
    {
        var sentinel = target.ExecuteScalar<byte[]?>("SELECT sentinel_value FROM tbl_node_identity LIMIT 1");
        if (sentinel is null) return;   // a vault that never had one has nothing to check
        if (!deks.Candidates.Any(dek => MasterKeyManager.VerifySentinel(sentinel, dek)))
            throw new SealMigrationException(SealFailures.VerifySentinel, "The master key of this node does not open the encrypted copy.");
    }

    // ---- 4. article bodies --------------------------------------------------------------------------------------------------------------

    private static async Task Bodies(string targetPath, IVaultKeySource keys, SqliteConnection target, IDEK deks)
    {
        var ids = target.Query<string>("SELECT id FROM tbl_article ORDER BY RANDOM() LIMIT @n", new { n = SampleCount }).ToList();
        if (ids.Count == 0) return;
        using var factory = new DbConnectionFactory(targetPath, keys);
        var repository = new ArticleBodyRepository(factory);
        foreach (var text in ids)
        {
            if (!Guid.TryParse(text, out var id)) continue;
            var body = await repository.GetByArticleIdAsync(id);
            if (body is null) continue;   // a body that was never written (or hard-deleted): nothing to decrypt
            var opened = false;
            foreach (var dek in deks.Candidates)
            {
                try
                {
                    var articleDek = EnvelopeFraming.Article.UnwrapDek(id, body.EncryptedDek, body.DekIV, dek);
                    try
                    {
                        _ = EnvelopeFraming.Article.DecryptBodyText(id, body.EncryptedDek, articleDek, body.Ciphertext, body.IV);
                        opened = true;
                    }
                    finally { CryptographicOperations.ZeroMemory(articleDek); }
                    if (opened) break;
                }
                catch (CryptographicException) { }
            }
            if (!opened)
                throw new SealMigrationException(SealFailures.VerifyBodies, "An article of the encrypted copy cannot be decrypted with this node's master key.");
        }
    }

    // ---- 5. folders, titles and search ----------------------------------------------------------------------------------------------------

    private static Sample Tree(SqliteConnection source, SqliteConnection target)
    {
        string Folders(SqliteConnection c) => Hash(c.Query<(string, string)>("SELECT id, path FROM tbl_folder ORDER BY id").Select(r => $"{r.Item1}|{r.Item2}"));
        string Titles(SqliteConnection c) => Hash(c.Query<(string, string?)>("SELECT id, title FROM tbl_article ORDER BY id").Select(r => $"{r.Item1}|{r.Item2}"));
        if (Folders(source) != Folders(target) || Titles(source) != Titles(target))
            throw new SealMigrationException(SealFailures.VerifyTree, "The folders or titles of the encrypted copy differ from the original.");

        var titles = source.Query<string?>("SELECT title FROM tbl_article WHERE title IS NOT NULL ORDER BY RANDOM() LIMIT @n", new { n = SampleCount })
            .Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList();
        var paths = source.Query<string?>("SELECT path FROM tbl_folder WHERE path IS NOT NULL ORDER BY RANDOM() LIMIT @n", new { n = SampleCount })
            .Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList();
        return new Sample(titles, paths);
    }

    private static void Search(SqliteConnection source, SqliteConnection target, IReadOnlyList<string> titles)
    {
        foreach (var title in titles.Take(5))
        {
            var word = title.Split([' ', '-', '_', '.', ',', ':', ';', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(w => w.Length >= 3 && w.All(char.IsLetterOrDigit));
            if (word is null) continue;
            var query = "\"" + word + "\"";
            List<long>? Ids(SqliteConnection c)
            {
                try { return c.Query<long>("SELECT rowid FROM fts_article WHERE fts_article MATCH @q ORDER BY rowid", new { q = query }).ToList(); }
                catch (SqliteException) { return null; }
            }
            var a = Ids(source);
            var b = Ids(target);
            if (a is null != b is null || (a is not null && !a.SequenceEqual(b!)))
                throw new SealMigrationException(SealFailures.VerifyTree, "A search in the encrypted copy finds other articles than in the original.");
        }
    }

    // ---- 6. raw bytes: no magic, no title ----------------------------------------------------------------------------------------------------

    private static void RawScan(string targetPath, Sample sample)
    {
        using var fs = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> head = stackalloc byte[16];
        if (fs.Read(head) == head.Length && head.SequenceEqual(SqliteMagic))
            throw new SealMigrationException(SealFailures.VerifyScan, "The encrypted copy starts like a plain SQLite file.");

        var needles = sample.Titles.Concat(sample.Paths).Select(Encoding.UTF8.GetBytes).Where(b => b.Length >= MinNeedleBytes).Distinct(new BytesComparer()).ToList();
        if (needles.Count == 0) return;
        fs.Position = 0;
        var overlap = needles.Max(n => n.Length) - 1;
        var buffer = new byte[1 << 20];
        var carried = 0;
        int read;
        while ((read = fs.Read(buffer, carried, buffer.Length - carried)) > 0)
        {
            var span = buffer.AsSpan(0, carried + read);
            foreach (var needle in needles)
                if (span.IndexOf(needle) >= 0)
                    throw new SealMigrationException(SealFailures.VerifyScan, "A title or a folder name is readable in the encrypted copy.");
            carried = Math.Min(overlap, span.Length);
            span[^carried..].CopyTo(buffer);
        }
    }

    private sealed class BytesComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => obj.Length == 0 ? 0 : HashCode.Combine(obj.Length, obj[0], obj[^1]);
    }

    // ---- 7. the boot store ----------------------------------------------------------------------------------------------------------------

    private static async Task BootAsync(SealVerifyInput input, DatabaseKeyService service, IDEK deks, byte[] vaultKey)
    {
        try
        {
            if (!VaultStoreFiles.HasSqliteMagic(input.BootTarget) || VaultStoreFiles.ReadApplicationId(input.BootTarget) != VaultStoreFiles.BootApplicationId)
                throw new SealMigrationException(SealFailures.VerifyBoot, "The boot store of the encrypted copy is not a boot store.");

            // One door per key slot and per agent that can unlock.
            using var source = OpenPlain(input.VaultSource);
            var slotIds = source.Query<long>("SELECT slot_id FROM tbl_key_slot").OrderBy(x => x).ToList();
            var agentIds = source.Query<long>("SELECT id FROM tbl_agent WHERE encrypted_dek IS NOT NULL AND status = 'A'").OrderBy(x => x).ToList();
            var doors = await new BootDoorStore(new BootStoreConnectionFactory(input.BootTarget)).ListAsync(ct: default);
            if (!doors.Where(d => d.Kind == BootDoorKinds.Slot).Select(d => d.SourceId).Distinct().OrderBy(x => x).SequenceEqual(slotIds)
                || !doors.Where(d => d.Kind == BootDoorKinds.Agent).Select(d => d.SourceId).Distinct().OrderBy(x => x).SequenceEqual(agentIds))
                throw new SealMigrationException(SealFailures.VerifyBoot, "The boot store of the encrypted copy lacks a door of the original.");

            // The database key unwraps with the session's master key, and the page key derived from it is the one the targets were sealed with.
            var unwrapped = await service.TryUnwrapAsync(deks.Candidates[0]);
            if (unwrapped is null)
                throw new SealMigrationException(SealFailures.VerifyBoot, "The database key in the boot store does not open with this node's master key.");
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(unwrapped, input.DatabaseKey))
                    throw new SealMigrationException(SealFailures.VerifyBoot, "The database key in the boot store is not the key the copy was sealed with.");
                var derived = service.DerivePageKey(unwrapped, DbPurposes.Vault);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(derived, vaultKey))
                        throw new SealMigrationException(SealFailures.VerifyBoot, "The page key of the boot store does not open the encrypted copy.");
                }
                finally { CryptographicOperations.ZeroMemory(derived); }
            }
            finally { CryptographicOperations.ZeroMemory(unwrapped); }
        }
        catch (SqliteException ex)
        {
            throw new SealMigrationException(SealFailures.VerifyBoot, $"The boot store of the encrypted copy could not be read (SQLite result {ex.SqliteErrorCode}).");
        }
    }

    private static string Hash(IEnumerable<string> rows)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in rows)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(row));
            sha.AppendData("\n"u8);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
