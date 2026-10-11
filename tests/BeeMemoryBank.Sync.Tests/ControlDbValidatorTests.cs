using BeeMemoryBank.Sync.Blind.Format2;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The row-level check of a package's control.db (docs/design/step-a-spec.md 8.4, 8.6 step 4; review round 3): one valid row in
/// every control table is accepted, and every kind of row the live appliers would never write is refused with the table, the
/// row and the column named - never a value. The reader-level cases (a signed package with such a row) are in
/// <see cref="Format2PackageTests"/>; the rows the real appliers write are checked in their own test classes.
/// </summary>
public sealed class ControlDbValidatorTests : IAsyncLifetime
{
    private Format2TestNode _node = null!;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bmb_ctlval_" + Guid.NewGuid().ToString("N"));
    private string _valid = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_work);
        _node = await Format2TestNode.CreateAsync(name: "hub");
        var author = Guid.NewGuid();
        await Format2TestKit.ActivateAsync(_node.Factory, author);

        var now = DateTime.UtcNow.ToString("O");
        var strong = Guid.NewGuid().ToString();
        var fingerprint = new string('c', 64);
        byte[] versioned = [1, .. new byte[48]];
        using (var conn = _node.Factory.CreateConnection())
        {
            const string box = @"INSERT INTO tbl_recovery_box
                (box_id, kind, author_node_id, dek_fingerprint, epoch_hint, kdf_preset, salt, wrapped, iv, created_at, status, retired_by_box_id, lamport_ts, source_node_id)
                VALUES (@id, @kind, @author, @fingerprint, 1, @preset, @salt, @wrapped, @iv, @now, @status, @retiredBy, @lamport, @source)";
            // The active strong box (versioned wrap), an active device box (a slot copy may carry the unversioned wrap) and a retired
            // one whose material the appliers cleared; a position vector keyed by an upper-case id is what the anchor applier stores as sent.
            conn.Execute(box, new
            {
                id = strong, kind = "strong", author = author.ToString(), fingerprint, preset = "s1024t4", salt = new byte[32],
                wrapped = versioned, iv = new byte[12], now, status = "A", retiredBy = (string?)null, lamport = 20, source = author.ToString(),
            });
            conn.Execute(box, new
            {
                id = Guid.NewGuid().ToString(), kind = "device", author = author.ToString(), fingerprint, preset = "d64t3", salt = new byte[32],
                wrapped = new byte[48], iv = new byte[12], now, status = "A", retiredBy = (string?)null, lamport = 11, source = (string?)null,
            });
            conn.Execute(box, new
            {
                id = Guid.NewGuid().ToString(), kind = "device", author = author.ToString(), fingerprint, preset = "d64t3", salt = Array.Empty<byte>(),
                wrapped = Array.Empty<byte>(), iv = Array.Empty<byte>(), now, status = "R", retiredBy = strong, lamport = 10, source = author.ToString(),
            });
            conn.Execute(
                @"INSERT INTO tbl_dek_retired_link (commit_id, author_node_id, old_fingerprint, new_fingerprint, wrapped, iv, created_at)
                  VALUES (@commit, @author, @old, @new, @wrapped, @iv, @now)",
                new
                {
                    commit = Guid.NewGuid().ToString(), author = author.ToString(), old = new string('a', 64), @new = new string('b', 64),
                    wrapped = versioned, iv = new byte[12], now,
                });
            conn.Execute(
                @"INSERT INTO tbl_state_anchor (anchor_id, author_node_id, dek_fingerprint, position_vector, digest, hmac, created_at, lamport_ts)
                  VALUES (@id, @author, @fingerprint, @vector, @digest, @hmac, @now, 30)",
                new
                {
                    id = Guid.NewGuid().ToString(), author = author.ToString(), fingerprint, vector = $"{{\"{author.ToString().ToUpperInvariant()}\":42}}",
                    digest = "sd2:" + new string('b', 64), hmac = new string('c', 64), now,
                });
            conn.Execute(
                @"INSERT INTO tbl_sealed_secret (name, dek_fingerprint, wrapped, iv, updated_at, status, lamport_ts, source_node_id)
                  VALUES (@name, @fingerprint, @wrapped, @iv, @now, @status, @lamport, @source)",
                new
                {
                    name = $"restic:{Guid.NewGuid()}", fingerprint, wrapped = new byte[48], iv = new byte[12], now, status = "A", lamport = 12, source = author.ToString(),
                });
            conn.Execute(
                @"INSERT INTO tbl_sealed_secret (name, dek_fingerprint, wrapped, iv, updated_at, status, lamport_ts, source_node_id)
                  VALUES (@name, @fingerprint, X'', X'', @now, 'R', 3, NULL)",
                new { name = $"android-backup:{Guid.NewGuid()}", fingerprint, now });
        }

        _valid = Path.Combine(_work, "valid.db");
        Format2TestKit.CopyControlTables(_node.Factory, _valid);
    }

    public Task DisposeAsync()
    {
        _node.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    /// <summary>A copy of the valid control database with <paramref name="sql"/> run against it.</summary>
    private string Edited(string sql)
    {
        var file = Path.Combine(_work, "case-" + Guid.NewGuid().ToString("N") + ".db");
        File.Copy(_valid, file);
        using var conn = new SqliteConnection($"Data Source={file};Pooling=False");
        conn.Open();
        conn.Execute(sql);
        return file;
    }

    [Fact]
    public void OneValidRowInEveryControlTable_IsAccepted_AndTheHighestLamportTimeIsReturned()
    {
        ControlDbValidator.Validate(_valid).Should().Be(30);
    }

    /// <summary>
    /// A control row must leave one Lamport tick for the first-seed grant: restore accepts values below 2^53,
    /// and the grant is one above the package's highest carried value.
    /// </summary>
    [Theory]
    [InlineData(9007199254740990L, true)]
    [InlineData(9007199254740991L, false)]
    [InlineData(9007199254740992L, false)]
    [InlineData(9007199254740993L, false)]
    [InlineData(long.MaxValue, false)]
    [InlineData(-1L, false)]
    [InlineData(0L, true)]
    public void ALamportTime_IsAcceptedOnlyWhenAFirstSeedCanStillGrantAndRestore(long lamport, bool accepted)
    {
        var file = Edited($"UPDATE tbl_state_anchor SET lamport_ts = {lamport}");

        var validate = () => ControlDbValidator.Validate(file);

        if (accepted) validate.Should().NotThrow();
        else validate.Should().Throw<InvalidDataException>().WithMessage("*tbl_state_anchor*column lamport_ts*");
    }

    public static IEnumerable<object[]> BadRows() =>
    [
        ["a key at the maximum Lamport time", "UPDATE tbl_metadata_key SET lamport_ts = 9223372036854775807", "tbl_metadata_key*column lamport_ts"],
        ["a key with a negative Lamport time", "UPDATE tbl_metadata_key SET lamport_ts = -1", "tbl_metadata_key*column lamport_ts"],
        ["a key id in capitals", "UPDATE tbl_metadata_key SET key_id = upper('abcdefabcdefabcdefabcdefabcdefab')", "tbl_metadata_key*column key_id"],
        ["a key id of 31 characters", "UPDATE tbl_metadata_key SET key_id = substr(key_id, 1, 31)", "tbl_metadata_key*column key_id"],
        ["a key id stored as an integer", "UPDATE tbl_metadata_key SET key_id = 7", "tbl_metadata_key*column key_id"],
        ["a key of generation 0", "UPDATE tbl_metadata_key SET generation = 0", "tbl_metadata_key*column generation"],
        ["a key fingerprint that is not hex", "UPDATE tbl_metadata_key SET dek_fingerprint = 'zz'", "tbl_metadata_key*column dek_fingerprint"],
        ["key material of 47 bytes", "UPDATE tbl_metadata_key SET wrapped = zeroblob(47)", "tbl_metadata_key*column wrapped"],
        ["key material stored as text", "UPDATE tbl_metadata_key SET wrapped = 'not a blob'", "tbl_metadata_key*column wrapped"],
        ["an iv of 11 bytes", "UPDATE tbl_metadata_key SET iv = zeroblob(11)", "tbl_metadata_key*column iv"],
        ["a key author that is no node id", "UPDATE tbl_metadata_key SET author_node_id = 'me'", "tbl_metadata_key*column author_node_id"],
        ["a key creation time that is no time", "UPDATE tbl_metadata_key SET created_at = 'yesterday'", "tbl_metadata_key*column created_at"],
        ["a key event id that is empty", "UPDATE tbl_metadata_key SET event_id = ''", "tbl_metadata_key*column event_id"],
        ["a key row twice", "INSERT INTO tbl_metadata_key SELECT * FROM tbl_metadata_key", "tbl_metadata_key*column key_id*repeats"],
        ["an activation naming a key the package lacks",
            "UPDATE tbl_metadata_activation SET key_id = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'", "tbl_metadata_activation*column key_id*key the package does not carry"],
        ["an activation of another generation", "UPDATE tbl_metadata_activation SET generation = 2", "tbl_metadata_activation*column generation"],
        ["a second activation",
            "INSERT INTO tbl_metadata_activation SELECT '11111111-1111-1111-1111-111111111111', key_id, generation, activated_at, author_node_id, event_id, lamport_ts, applied_at FROM tbl_metadata_activation",
            "tbl_metadata_activation*second activation"],
        ["an activation at the maximum Lamport time", "UPDATE tbl_metadata_activation SET lamport_ts = 9223372036854775807", "tbl_metadata_activation*column lamport_ts"],
        ["an activation id that is no id", "UPDATE tbl_metadata_activation SET activation_id = 'one'", "tbl_metadata_activation*column activation_id"],
        ["a box of a kind that is neither", "UPDATE tbl_recovery_box SET kind = 'weird' WHERE kdf_preset = 's1024t4'", "tbl_recovery_box*column kind"],
        ["a strong box with the device preset", "UPDATE tbl_recovery_box SET kdf_preset = 'd64t3' WHERE kind = 'strong'", "tbl_recovery_box*column kdf_preset"],
        ["an active box with a salt of 8 bytes", "UPDATE tbl_recovery_box SET salt = zeroblob(8) WHERE kind = 'strong'", "tbl_recovery_box*column wrapped"],
        ["an active strong box with the unversioned wrap", "UPDATE tbl_recovery_box SET wrapped = zeroblob(48) WHERE kind = 'strong'", "tbl_recovery_box*column wrapped"],
        ["a box salt stored as text", "UPDATE tbl_recovery_box SET salt = 'salt' WHERE kind = 'strong'", "tbl_recovery_box*column salt"],
        ["a box with a status of its own", "UPDATE tbl_recovery_box SET status = 'X' WHERE kind = 'strong'", "tbl_recovery_box*column status"],
        ["a box id that is no id", "UPDATE tbl_recovery_box SET box_id = 'box-1' WHERE kind = 'strong'", "tbl_recovery_box*column box_id"],
        ["a box row twice", "INSERT INTO tbl_recovery_box SELECT * FROM tbl_recovery_box WHERE kind = 'strong'", "tbl_recovery_box*column box_id*repeats"],
        ["a box at the maximum Lamport time", "UPDATE tbl_recovery_box SET lamport_ts = 9223372036854775807 WHERE kind = 'strong'", "tbl_recovery_box*column lamport_ts"],
        ["a box retired by something that is no box", "UPDATE tbl_recovery_box SET retired_by_box_id = 'x' WHERE status = 'R'", "tbl_recovery_box*column retired_by_box_id"],
        ["a box source that is no node", "UPDATE tbl_recovery_box SET source_node_id = 'x' WHERE kind = 'strong'", "tbl_recovery_box*column source_node_id"],
        ["a link whose fingerprints are the same", "UPDATE tbl_dek_retired_link SET new_fingerprint = old_fingerprint", "tbl_dek_retired_link*column new_fingerprint"],
        ["a link with material of 5 bytes", "UPDATE tbl_dek_retired_link SET wrapped = zeroblob(5)", "tbl_dek_retired_link*column wrapped"],
        ["a link row twice", "INSERT INTO tbl_dek_retired_link SELECT * FROM tbl_dek_retired_link", "tbl_dek_retired_link*column commit_id*repeats"],
        ["an anchor whose digest is no digest", "UPDATE tbl_state_anchor SET digest = 'd'", "tbl_state_anchor*column digest"],
        ["an anchor whose hmac is no hmac", "UPDATE tbl_state_anchor SET hmac = 'ab'", "tbl_state_anchor*column hmac"],
        ["an anchor vector with a negative position", "UPDATE tbl_state_anchor SET position_vector = '{\"11111111-1111-1111-1111-111111111111\": -1}'", "tbl_state_anchor*column position_vector"],
        ["an anchor vector keyed by no node", "UPDATE tbl_state_anchor SET position_vector = '{\"n\": 1}'", "tbl_state_anchor*column position_vector"],
        ["an anchor vector that is not JSON", "UPDATE tbl_state_anchor SET position_vector = 'x'", "tbl_state_anchor*column position_vector"],
        ["an anchor vector that is an array", "UPDATE tbl_state_anchor SET position_vector = '[1]'", "tbl_state_anchor*column position_vector"],
        ["an anchor at the maximum Lamport time", "UPDATE tbl_state_anchor SET lamport_ts = 9223372036854775807", "tbl_state_anchor*column lamport_ts"],
        ["a secret whose name is none of the secrets", "UPDATE tbl_sealed_secret SET name = 'restic:old' WHERE status = 'A'", "tbl_sealed_secret*column name"],
        ["an active secret with an iv of 11 bytes", "UPDATE tbl_sealed_secret SET iv = zeroblob(11) WHERE status = 'A'", "tbl_sealed_secret*column wrapped"],
        ["an active secret with an empty value", "UPDATE tbl_sealed_secret SET wrapped = zeroblob(0) WHERE status = 'A'", "tbl_sealed_secret*column wrapped"],
        ["a secret with a status of its own", "UPDATE tbl_sealed_secret SET status = 'Z' WHERE status = 'A'", "tbl_sealed_secret*column status"],
        ["a secret name twice that differs in case",
            "INSERT INTO tbl_sealed_secret SELECT upper(name), dek_fingerprint, wrapped, iv, updated_at, status, lamport_ts, source_node_id FROM tbl_sealed_secret WHERE status = 'A'",
            "tbl_sealed_secret*column name*repeats"],
        ["a table that is missing", "DROP TABLE tbl_state_anchor", "tbl_state_anchor is missing"],
        ["a column that is missing", "ALTER TABLE tbl_sealed_secret DROP COLUMN status", "tbl_sealed_secret has no column status"],
    ];

    [Theory]
    [MemberData(nameof(BadRows))]
    public void ARowNoApplierWouldWrite_IsRefused_WithTheTableAndTheColumnNamed(string what, string sql, string message)
    {
        var file = Edited(sql);

        var validate = () => ControlDbValidator.Validate(file);

        validate.Should().Throw<InvalidDataException>(what).WithMessage($"The package's control database is not usable: *{message}*");
    }

    /// <summary>A message names the table, the row and the column - the rows hold wrapped keys, and no value may reach a log line.</summary>
    [Fact]
    public void TheRefusal_NamesNoValueOfTheRow()
    {
        var secret = new string('9', 48);
        var file = Edited($"UPDATE tbl_metadata_key SET dek_fingerprint = '{secret}'");

        var validate = () => ControlDbValidator.Validate(file);

        validate.Should().Throw<InvalidDataException>().Which.Message.Should().NotContain(secret).And.Contain("row 1").And.Contain("column dek_fingerprint");
    }
}
