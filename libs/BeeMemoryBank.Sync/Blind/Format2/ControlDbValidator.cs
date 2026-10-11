using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Sync.Blind.Format2;

/// <summary>
/// The row-level check of a package's <c>control.db</c> (docs/design/step-a-spec.md 8.4, 8.6 step 4): before anything is
/// adopted, every row of every control table is held against the rules the live appliers apply to the same data
/// (<c>EventApplier.Metadata.cs</c>, <c>EventApplier.Recovery.cs</c>), so the import that follows the adoption cannot
/// meet a row the node would never have accepted. The package is signed, which makes it authentic, not usable: an
/// import is "insert the rows as they are", and a row that no applier would have written (a key id of the wrong shape, a box whose
/// preset its kind may not have, a Lamport time no clock reaches) would otherwise land in the node's tables and
/// fail the first seed halfway.
///
/// <para>The whole table set must be there with the columns the appliers write; a column's storage class must be the
/// one the applier gives it (SQLite is dynamically typed, a text value in a blob column is not a blob); a primary key
/// must not repeat (the copy has no constraints); an activation names a key the package carries, with that key's
/// generation. A retired box or secret has no material to judge (the appliers clear it), so its blobs are only required
/// to be blobs. Messages name the table, the row's position and the column, never a value: the rows hold wrapped keys.</para>
/// </summary>
public static class ControlDbValidator
{
    // The shapes of EventApplier.Metadata.cs / EventApplier.Recovery.cs; the parity tests apply real events and validate the rows.
    private const int MetadataKeyIvBytes = 12;
    private const int MetadataKeyWrappedBytes = 32 + 16;
    private const int MaxSealedSecretBytes = 4096;
    private const int MaxAnchorVectorEntries = 4096;
    private static readonly Regex HexSha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex StateDigestValue = new(
        "^sd[0-9]{1,4}:[0-9a-f]{64,8192}(/[0-9a-f]{64,8192}(/([0-9a-f]{32}){0,1024})?)?$", RegexOptions.CultureInvariant);
    private static readonly Regex SealedSecretName = new(
        "^(restic|android-backup):[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly IReadOnlyDictionary<string, string[]> Columns = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["tbl_metadata_key"] = ["key_id", "generation", "dek_fingerprint", "epoch_hint", "wrapped", "iv", "author_node_id", "created_at", "lamport_ts", "event_id"],
        ["tbl_metadata_activation"] = ["activation_id", "key_id", "generation", "activated_at", "author_node_id", "event_id", "lamport_ts", "applied_at"],
        ["tbl_recovery_box"] = ["box_id", "kind", "author_node_id", "dek_fingerprint", "epoch_hint", "kdf_preset", "salt", "wrapped", "iv", "created_at",
            "status", "retired_by_box_id", "lamport_ts", "source_node_id"],
        ["tbl_dek_retired_link"] = ["commit_id", "author_node_id", "old_fingerprint", "new_fingerprint", "wrapped", "iv", "created_at"],
        ["tbl_state_anchor"] = ["anchor_id", "author_node_id", "dek_fingerprint", "position_vector", "digest", "hmac", "created_at", "lamport_ts"],
        ["tbl_sealed_secret"] = ["name", "dek_fingerprint", "wrapped", "iv", "updated_at", "status", "lamport_ts", "source_node_id"],
    };

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> unless every control table is present and every row is one the appliers could have
    /// written. Returns the highest Lamport time of any row (0 for none): what the first seed's grant time has to stay above.
    /// </summary>
    public static long Validate(string controlDbPath)
    {
        using var conn = new SqliteConnection($"Data Source={controlDbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();

        long max = 0;
        var keys = new Dictionary<string, long>(StringComparer.Ordinal);
        Read(conn, "tbl_metadata_key", row =>
        {
            var keyId = row.Text("key_id");
            if (!SealedEnvelope.IsKeyId(keyId)) throw row.Fail("key_id", "is not 32 lowercase hex characters");
            var generation = row.Integer("generation");
            if (generation < 1) throw row.Fail("generation", "is below 1");
            row.Hex("dek_fingerprint");
            row.Integer("epoch_hint");
            row.Blob("wrapped", MetadataKeyWrappedBytes);
            row.Blob("iv", MetadataKeyIvBytes);
            row.Id("author_node_id");
            row.Time("created_at");
            max = Math.Max(max, row.Lamport("lamport_ts"));
            row.Id("event_id");
            if (!keys.TryAdd(keyId, generation)) throw row.Fail("key_id", "repeats");
        });

        var activations = 0;
        Read(conn, "tbl_metadata_activation", row =>
        {
            row.Id("activation_id");
            var keyId = row.Text("key_id");
            if (!SealedEnvelope.IsKeyId(keyId)) throw row.Fail("key_id", "is not 32 lowercase hex characters");
            var generation = row.Integer("generation");
            row.Time("activated_at");
            row.Id("author_node_id");
            row.Id("event_id");
            max = Math.Max(max, row.Lamport("lamport_ts"));
            row.Time("applied_at");
            // The applier needs the key first and the same generation; and one activation is all a node ever holds.
            if (!keys.TryGetValue(keyId, out var keyGeneration)) throw row.Fail("key_id", "names a key the package does not carry");
            if (keyGeneration != generation) throw row.Fail("generation", "is not the generation of the key it names");
            if (++activations > 1) throw row.Fail("activation_id", "is a second activation");
        });

        var boxes = new HashSet<string>(StringComparer.Ordinal);
        Read(conn, "tbl_recovery_box", row =>
        {
            var boxId = row.Id("box_id");
            var kind = row.Text("kind");
            if (kind is not (RecoveryBoxKdf.KindStrong or RecoveryBoxKdf.KindDevice)) throw row.Fail("kind", "is neither strong nor device");
            row.Id("author_node_id");
            row.Hex("dek_fingerprint");
            row.Integer("epoch_hint");
            if (!RecoveryBoxKdf.IsAllowed(kind, row.Text("kdf_preset"))) throw row.Fail("kdf_preset", "is not allowed for the box's kind");
            var salt = row.Blob("salt");
            var wrapped = row.Blob("wrapped");
            var iv = row.Blob("iv");
            row.Time("created_at");
            var status = row.Status();
            if (row.OptionalText("retired_by_box_id") is { } retiredBy && !Guid.TryParseExact(retiredBy, "D", out _))
                throw row.Fail("retired_by_box_id", "is not a box id");
            max = Math.Max(max, row.Lamport("lamport_ts"));
            row.OptionalNode("source_node_id");
            if (status == "A" && !RecoveryBoxKdf.IsWellFormed(kind, salt, wrapped, iv)) throw row.Fail("wrapped", "is not the material of an active box");
            if (!boxes.Add(boxId.ToLowerInvariant())) throw row.Fail("box_id", "repeats");
        });

        var links = new HashSet<string>(StringComparer.Ordinal);
        Read(conn, "tbl_dek_retired_link", row =>
        {
            var commit = row.Id("commit_id");
            var author = row.Id("author_node_id");
            var oldFingerprint = row.Hex("old_fingerprint");
            var newFingerprint = row.Hex("new_fingerprint");
            if (oldFingerprint == newFingerprint) throw row.Fail("new_fingerprint", "is the old fingerprint");
            var wrapped = row.Blob("wrapped");
            if (!RecoveryBoxKdf.IsWellFormed(new byte[RecoveryBoxKdf.SaltSize], wrapped, row.Blob("iv"))) throw row.Fail("wrapped", "is not link material");
            row.Time("created_at");
            if (!links.Add(commit.ToLowerInvariant() + "/" + author.ToLowerInvariant())) throw row.Fail("commit_id", "repeats for its author");
        });

        var anchors = new HashSet<string>(StringComparer.Ordinal);
        Read(conn, "tbl_state_anchor", row =>
        {
            var anchor = row.Id("anchor_id");
            row.Id("author_node_id");
            row.Hex("dek_fingerprint");
            if (!PositionVectorIsValid(row.Text("position_vector"))) throw row.Fail("position_vector", "is not a map of node ids to positions");
            if (!StateDigestValue.IsMatch(row.Text("digest"))) throw row.Fail("digest", "is not a state digest");
            row.Hex("hmac");
            row.Time("created_at");
            max = Math.Max(max, row.Lamport("lamport_ts"));
            if (!anchors.Add(anchor.ToLowerInvariant())) throw row.Fail("anchor_id", "repeats");
        });

        var secrets = new HashSet<string>(StringComparer.Ordinal);
        Read(conn, "tbl_sealed_secret", row =>
        {
            var name = row.Text("name");
            if (!SealedSecretName.IsMatch(name)) throw row.Fail("name", "is not the name of a sealed secret");
            row.Hex("dek_fingerprint");
            var wrapped = row.Blob("wrapped");
            var iv = row.Blob("iv");
            row.Time("updated_at");
            var status = row.Status();
            max = Math.Max(max, row.Lamport("lamport_ts"));
            row.OptionalNode("source_node_id");
            if (status == "A" && (wrapped.Length is <= CryptoConstants.TagSize or > MaxSealedSecretBytes || iv.Length != CryptoConstants.IvSize))
                throw row.Fail("wrapped", "is not the material of an active sealed secret");
            if (!secrets.Add(name.ToLowerInvariant())) throw row.Fail("name", "repeats");
        });
        return max;
    }

    private static void Read(SqliteConnection conn, string table, Action<Row> check)
    {
        var expected = Columns[table];
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var type = conn.CreateCommand())
        {
            type.CommandText = "SELECT type FROM sqlite_master WHERE name = @t";
            type.Parameters.AddWithValue("@t", table);
            var objectType = type.ExecuteScalar() as string;
            if (objectType is null) throw Bad(table, "is missing");
            if (!string.Equals(objectType, "table", StringComparison.Ordinal)) throw Bad(table, "is not a table");
        }
        using (var info = conn.CreateCommand())
        {
            // A table that is not there has no columns: the same refusal, whichever way it is missing.
            info.CommandText = "SELECT name FROM pragma_table_info(@t)";
            info.Parameters.AddWithValue("t", table);
            using var names = info.ExecuteReader();
            while (names.Read()) present.Add(names.GetString(0));
        }
        if (present.Count == 0) throw Bad(table, "is missing");
        foreach (var column in expected)
            if (!present.Contains(column)) throw Bad(table, $"has no column {column}");

        using var select = conn.CreateCommand();
        select.CommandText = $"SELECT {string.Join(", ", expected.Select(c => $"\"{c}\""))} FROM \"{table}\"";
        using var reader = select.ExecuteReader();
        var number = 0;
        while (reader.Read())
            check(new Row(table, ++number, expected, reader));
    }

    private static bool PositionVectorIsValid(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var count = 0;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (++count > MaxAnchorVectorEntries) return false;
                if (!Guid.TryParseExact(entry.Name, "D", out _)) return false;
                if (entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetInt64(out var position) || position < 0) return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static InvalidDataException Bad(string table, string what) =>
        new($"The package's control database is not usable: {table} {what}.");

    /// <summary>One row, read by column name; every accessor throws a refusal that names the table, the row and the column.</summary>
    private sealed class Row(string table, int number, string[] columns, SqliteDataReader reader)
    {
        public InvalidDataException Fail(string column, string why) =>
            new($"The package's control database is not usable: {table} row {number}, column {column} {why}.");

        private object? Value(string column)
        {
            var value = reader.GetValue(Array.IndexOf(columns, column));
            return value is DBNull ? null : value;
        }

        public string Text(string column) => Value(column) as string ?? throw Fail(column, "is not text");

        public string? OptionalText(string column) => Value(column) switch
        {
            null => null,
            string s => s,
            _ => throw Fail(column, "is not text"),
        };

        public long Integer(string column) => Value(column) is long l ? l : throw Fail(column, "is not an integer");

        public byte[] Blob(string column) => Value(column) as byte[] ?? throw Fail(column, "is not a blob");

        public byte[] Blob(string column, int length) =>
            Blob(column) is { } blob && blob.Length == length ? blob : throw Fail(column, $"is not {length} bytes");

        /// <summary>A node, box or event id as the appliers read one: the "D" form (they write it lowercase; the case is not judged).</summary>
        public string Id(string column) =>
            Text(column) is var text && Guid.TryParseExact(text, "D", out _) ? text : throw Fail(column, "is not an id");

        /// <summary>A source node: an id, or nothing (rows written before a source was known).</summary>
        public void OptionalNode(string column)
        {
            if (OptionalText(column) is { } text && !Guid.TryParse(text, out _)) throw Fail(column, "is not a node id");
        }

        public string Hex(string column) =>
            Text(column) is var text && HexSha256.IsMatch(text) ? text : throw Fail(column, "is not 64 lowercase hex characters");

        public void Time(string column)
        {
            if (!DateTime.TryParse(Text(column), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                throw Fail(column, "is not a time");
        }

        public long Lamport(string column) =>
            Integer(column) is var time and >= 0 and <= BlindManifestV2.MaximumLamport ? time : throw Fail(column, "is not a Lamport time any clock reaches");

        public string Status() =>
            Text("status") is var text && text is "A" or "R" ? text : throw Fail("status", "is neither A nor R");
    }
}
