using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// Takes over a database that already ran the 39 migrations of 2.6.0 / 2.6.1 as the baseline's: no migration SQL is executed, the database's
/// structure is checked against the baseline's, what is safely repairable is repaired, and the 39 ledger rows are replaced by the baseline's
/// one row, all in ONE transaction. Whatever goes wrong rolls everything back, so the database is exactly as it was; a structure that is
/// not the baseline's beyond repair (a column is missing) is refused before anything is written.
///
/// <para>The reference is the baseline itself: its statements are run once in a private in-memory database and that database is read with
/// <see cref="SchemaShape"/>. So the check follows the baseline file, there is no second list to keep in step with it.</para>
///
/// <para>What is tolerated, because real databases have it and the program has always run on it: extra tables, indexes, triggers and views
/// (the partial unique indexes the auto-unlock services create at run time, leftovers of the keyword-tag generation), extra columns that the
/// program's INSERTs cannot trip over, a column whose NOT NULL is looser than the baseline's, another spelling of the same type, and any
/// difference of text only (spacing, case, comments, where a column sits). What is repaired: a missing table, full-text index (rebuilt from
/// the table it indexes), index, view or trigger, and one of those that is defined differently (they hold no data of their own). What is
/// refused: a missing column, a column of another type affinity or primary key, one that is NOT NULL where the program writes NULL, a
/// column whose DEFAULT, collation or generated-ness differs, a table whose CHECK constraints, WITHOUT ROWID, STRICT or AUTOINCREMENT differ,
/// an extra column that is NOT NULL without a default, a missing foreign key or uniqueness rule, a differently defined full-text index.
/// The comparison itself is <see cref="SchemaShape.Compare"/>.</para>
/// </summary>
internal static class LegacyChainAdoption
{
    private static readonly object ReferenceLock = new();
    private static SchemaShape? _reference;
    private static string? _referenceOf;

    /// <summary>The structure of the baseline, built once per process from the baseline's own text.</summary>
    private static SchemaShape ReferenceOf(string baselineSql)
    {
        lock (ReferenceLock)
        {
            if (_reference is not null && _referenceOf == baselineSql) return _reference;
            using var scratch = new SqliteConnection("Data Source=:memory:");
            scratch.Open();
            foreach (var statement in MigrationRunner.PrepareStatements(baselineSql))
                scratch.Execute(statement);
            _reference = SchemaShape.Read(scratch);
            _referenceOf = baselineSql;
            return _reference;
        }
    }

    /// <summary>
    /// The structure of the database behind <paramref name="connection"/> against the baseline's, read only: null when an adoption would go
    /// on (what is missing is repairable), the refusal when it would not. The runner asks it on its read-only probe, before it opens the
    /// normal connection, and the restore asks it of a staged backup before it replaces anything.
    /// </summary>
    public static MigrationRefusedException? Judge(IDbConnection connection, string baselineSql)
    {
        var diff = SchemaShape.Compare(ReferenceOf(baselineSql), SchemaShape.Read(connection));
        return diff.Problems.Count > 0 ? MigrationRefusedException.Incompatible(diff.Problems) : null;
    }

    /// <summary>
    /// The same question for a database that carries the baseline's ledger already: the runner does not repair such a database (no adoption
    /// runs for it), so what an adoption would have repaired (a missing table, index, view or trigger, one defined differently) is a
    /// reason to refuse here, together with everything <see cref="Judge"/> refuses.
    /// </summary>
    public static MigrationRefusedException? JudgeAsIs(IDbConnection connection, string baselineSql)
    {
        var diff = SchemaShape.Compare(ReferenceOf(baselineSql), SchemaShape.Read(connection));
        var problems = diff.Problems.Concat(diff.Repaired.Select(name => $"{name} is missing or defined differently")).ToList();
        return problems.Count > 0 ? MigrationRefusedException.Incompatible(problems) : null;
    }

    /// <summary>How long a second program waits for the first one's adoption before it gives up (the busy timeout of a connection is shorter).</summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The write lock, taken before anything is read: Microsoft.Data.Sqlite begins an IMMEDIATE transaction. When another program holds it, most
    /// likely an adoption that is under way, the wait is the connection's busy timeout; if that runs out the ledger is looked at again (readers
    /// are not blocked in WAL mode) and the wait goes on, up to <see cref="LockWait"/>. Null: the database turned out to be adopted already.
    /// </summary>
    private static IDbTransaction? TakeWriteLock(IDbConnection connection)
    {
        var until = DateTime.UtcNow + LockWait;
        while (true)
        {
            try
            {
                return connection.BeginTransaction();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && DateTime.UtcNow < until)
            {
                try
                {
                    if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_migration WHERE version = @version AND filename = @filename",
                            new { version = MigrationRunner.BaselineVersion, filename = MigrationRunner.BaselineResourceName }) > 0)
                        return null;
                }
                catch (SqliteException) { /* the lock may block this read too (a rollback-journal file): wait on */ }
            }
        }
    }

    /// <summary>
    /// Runs the repairs in order. A UNIQUE index (with the DROP of the index it replaces) is tried inside a savepoint: when the rows already
    /// repeat what the index forbids, that one repair is undone and the index stays as the database had it (a missing one stays missing).
    /// An index holds no data of its own and the program has always run without the uniqueness, so this is not a reason to refuse a
    /// database. Returns the names of the indexes that were left.
    /// </summary>
    private static async Task<List<string>> RunRepairsAsync(IDbConnection connection, IDbTransaction transaction, List<string> repairs)
    {
        var kept = new List<string>();
        for (var i = 0; i < repairs.Count; i++)
        {
            var statement = repairs[i];
            var group = 1;
            if (statement.StartsWith("DROP INDEX", StringComparison.OrdinalIgnoreCase) && i + 1 < repairs.Count
                && SchemaShape.UniqueIndexName(repairs[i + 1]) is not null)
                group = 2;
            var unique = SchemaShape.UniqueIndexName(repairs[i + group - 1]);
            if (unique is null)
            {
                await connection.ExecuteAsync(statement, transaction: transaction);
                continue;
            }

            await connection.ExecuteAsync("SAVEPOINT repair_unique_index", transaction: transaction);
            try
            {
                for (var k = 0; k < group; k++) await connection.ExecuteAsync(repairs[i + k], transaction: transaction);
                await connection.ExecuteAsync("RELEASE repair_unique_index", transaction: transaction);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // constraint: the rows repeat what the unique index forbids
            {
                await connection.ExecuteAsync("ROLLBACK TO repair_unique_index", transaction: transaction);
                await connection.ExecuteAsync("RELEASE repair_unique_index", transaction: transaction);
                kept.Add(unique);
            }
            i += group - 1;
        }
        return kept;
    }

    public static async Task AdoptAsync(IDbConnection connection, string baselineSql, ILogger? logger)
    {
        var reference = ReferenceOf(baselineSql);
        SchemaShape.Diff diff;

        // Two programs may both have read the 39 old rows and decided to adopt (the CLI next to a running node). The write lock goes first and the
        // decision is made again under it, so the second one finds the first one's work done and carries on. Disposing the transaction without
        // committing it (a refusal, an exception) rolls it back.
        IDbTransaction? transaction = null;
        try
        {
            transaction = TakeWriteLock(connection);
            if (transaction is null) return;

            switch (MigrationRunner.ClassifyEmbedded(connection, transaction))
            {
                case MigrationRunner.LedgerVerdict.Adopt: break;
                case MigrationRunner.LedgerVerdict.Older: throw MigrationRefusedException.Older();
                case MigrationRunner.LedgerVerdict.Unknown: throw MigrationRefusedException.Unknown();
                default: return; // Current: another program adopted it (or it was never legacy)
            }

            diff = SchemaShape.Compare(reference, SchemaShape.Read(connection, transaction));
            if (diff.Problems.Count > 0) throw MigrationRefusedException.Incompatible(diff.Problems);

            var keptNonUnique = await RunRepairsAsync(connection, transaction, diff.Repairs);
            if (diff.Repairs.Count > 0)
            {
                // What the repair made must be what the baseline has; otherwise the whole attempt is undone. A unique index the rows could not
                // satisfy was left as it was (above): it is the one thing that may still differ.
                var after = SchemaShape.Compare(reference, SchemaShape.Read(connection, transaction));
                if (after.Problems.Count > 0 || after.Repaired.Any(name => !keptNonUnique.Contains(name, StringComparer.OrdinalIgnoreCase)))
                    throw MigrationRefusedException.Incompatible(after.Problems.Count > 0 ? after.Problems : ["a repaired object does not have the expected definition"]);
            }
            foreach (var name in keptNonUnique) diff.Tolerated.Add($"the index {name} could not be made unique");

            // The guard (if the ledger has one: a tool turned a 2.6.2 ledger back into the old rows) must let this one delete through, and
            // it is created again in the same transaction, so the baseline row never commits without it.
            await connection.ExecuteAsync($"DROP TRIGGER IF EXISTS {MigrationRunner.DowngradeGuardName}", transaction: transaction);
            await connection.ExecuteAsync("DELETE FROM tbl_migration", transaction: transaction);
            var now = DateTime.UtcNow.ToString("o");
            await connection.ExecuteAsync(
                "INSERT INTO tbl_migration (version, filename, applied_at, updated_at) VALUES (@version, @filename, @now, @now)",
                new { version = MigrationRunner.BaselineVersion, filename = MigrationRunner.BaselineResourceName, now },
                transaction: transaction);
            await connection.ExecuteAsync(MigrationRunner.DowngradeGuardSql, transaction: transaction);
            transaction.Commit();
        }
        catch (SqliteException ex)
        {
            throw MigrationRefusedException.Failed(ex);
        }
        finally
        {
            transaction?.Dispose();
        }

        // One line, after the commit, so it is only ever written for an adoption that happened. Names of objects, no values, no paths.
        logger?.LogInformation(
            "Adopted a database of version 2.6 into the baseline schema (migration {Version}); {Repaired} object(s) repaired{RepairedNames}; " +
            "{Extras} extra object(s) left in place{ExtraNames}; {Tolerated} column difference(s) tolerated.",
            MigrationRunner.BaselineVersion,
            diff.Repaired.Count, diff.Repaired.Count > 0 ? ": " + string.Join(", ", diff.Repaired) : "",
            diff.Extras.Count, diff.Extras.Count > 0 ? ": " + string.Join(", ", diff.Extras) : "",
            diff.Tolerated.Count);
    }
}
