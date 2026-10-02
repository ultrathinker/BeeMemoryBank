using System.Text.Json;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Recovery;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The open header of a phone's backup (plan 6.8, section 10) is the recovery set of the PHONE's own database —
/// what it received by sync — in the format the Windows restore parses. The sealed secret it must hold,
/// <c>android-backup:&lt;phone&gt;</c>, is what <c>BlindPhoneBackupRunner</c> checks before it writes a file.
/// </summary>
public sealed class BlindPhoneRecoverySetSourceTests
{
    /// <summary>
    /// Found on a real phone (stage 5): the first backup was "made" while the network had published no recovery box
    /// yet (boxes are built when a superadmin signs in on a computer), and the Windows restore then said "The master
    /// password opens none of the recovery boxes." A file nobody can open is not a backup: the header refuses to be
    /// built until a box has arrived by sync.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutAnActiveBox_TheHeaderIsNotBuilt_ItWaits(bool onlyARetiredBox)
    {
        DapperConfig.Configure();
        var dir = Path.Combine(Path.GetTempPath(), "bmb-s5-recset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var factory = new DbConnectionFactory(Path.Combine(dir, "beememorybank.db"));
        await new MigrationRunner(factory).RunMigrationsAsync();
        if (onlyARetiredBox)
        {
            using var conn = factory.CreateConnection();
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_recovery_box (box_id, kind, author_node_id, dek_fingerprint, epoch_hint, kdf_preset, salt, wrapped, iv, created_at, status, lamport_ts)
                  VALUES ('box-retired', 'strong', 'a1', 'fp', 1, 's512t6', x'01', x'02', x'03', '2026-09-01T00:00:00Z', 'R', 1)");
        }

        var build = async () => await new BlindPhoneRecoverySetSource(factory).BuildJsonAsync(CancellationToken.None);

        (await build.Should().ThrowAsync<BlindFeaturePendingException>())
            .Which.Message.Should().Contain("recovery box");
    }

    [Fact]
    public async Task TheSet_IsBuiltFromThePhonesDatabase_ActiveRowsOnly_InTheFormatTheRestoreParses()
    {
        DapperConfig.Configure();
        var dir = Path.Combine(Path.GetTempPath(), "bmb-s4-recset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var factory = new DbConnectionFactory(Path.Combine(dir, "beememorybank.db"));
        await new MigrationRunner(factory).RunMigrationsAsync();
        var phone = Guid.NewGuid();
        using (var conn = factory.CreateConnection())
        {
            foreach (var (id, status) in new[] { ("box-active", "A"), ("box-retired", "R") })
                await conn.ExecuteAsync(
                    @"INSERT INTO tbl_recovery_box (box_id, kind, author_node_id, dek_fingerprint, epoch_hint, kdf_preset, salt, wrapped, iv, created_at, status, lamport_ts)
                      VALUES (@Id, 'strong', @Author, 'fp', 1, 's512t6', x'01', x'02', x'03', '2026-09-01T00:00:00Z', @Status, 1)",
                    new { Id = id, Author = Guid.NewGuid().ToString(), Status = status });
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_dek_retired_link (commit_id, author_node_id, old_fingerprint, new_fingerprint, wrapped, iv, created_at)
                  VALUES ('c1', 'a1', 'old', 'new', x'04', x'05', '2026-09-01T00:00:00Z')");
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_state_anchor (anchor_id, author_node_id, dek_fingerprint, position_vector, digest, hmac, created_at, lamport_ts)
                  VALUES ('anchor-1', 'a1', 'fp', '{""n"":5}', 'd', 'h', '2026-09-01T00:00:00Z', 1)");
            foreach (var (name, status) in new[] { ($"android-backup:{phone}", "A"), ("restic:old", "R") })
                await conn.ExecuteAsync(
                    @"INSERT INTO tbl_sealed_secret (name, dek_fingerprint, wrapped, iv, updated_at, status, lamport_ts)
                      VALUES (@Name, 'fp', x'06', x'07', '2026-09-01T00:00:00Z', @Status, 1)", new { Name = name, Status = status });
        }

        var json = await new BlindPhoneRecoverySetSource(factory).BuildJsonAsync(CancellationToken.None);

        var set = RecoverySet.Parse(json);
        set.Format.Should().Be("bmb-recovery-set-v1");
        set.Boxes.Select(b => b.BoxId).Should().Equal("box-active");
        set.Links.Should().ContainSingle(l => l.CommitId == "c1");
        set.Anchors.Should().ContainSingle(a => a.AnchorId == "anchor-1");
        set.SealedSecrets.Select(s => s.Name).Should().Equal($"android-backup:{phone}");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("sealed_secrets")[0].GetProperty("name").GetString().Should().Be($"android-backup:{phone}",
            "the runner finds the key's sealed copy by this name in the JSON");
    }
}
