using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Rekey;
using BeeMemoryBank.Rekey.Steps;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The pre-flight of the offline re-key (rekey-offline.md §2 step 1), against a real vault: every row the re-key
/// touches must open with a key it holds, or the verb stops before it creates anything. The vault is built by the
/// product's own services and frozen (<see cref="RekeyVaultTestBase"/>); the pre-flight runs on it read-only.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class RekeyPreflightTests : RekeyVaultTestBase
{
    private string _osTemp = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _osTemp = _factory.DataPath + "-ostemp";
        Directory.CreateDirectory(_osTemp);
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_osTemp)) Directory.Delete(_osTemp, recursive: true);
    }

    [Fact]
    public async Task AVaultWithEveryKindOfRow_Passes()
    {
        CountRows().Should().OnlyContain(kv => kv.Value > 0, "the fixture must hold every kind of row it claims to");

        var report = await RunAsync();

        report.Blocking.Should().BeEmpty();
        report.BytesNeeded.Should().BeGreaterThan(0);
    }

    /// <summary>The floor: one key that does not open is listed with its row, the verb refuses, and the vault is left
    /// byte for byte as it was.</summary>
    [Fact]
    public async Task OneKeyThatDoesNotOpen_IsListed_AndTheVaultIsUntouched()
    {
        Mutate($"UPDATE tbl_article_version SET encrypted_dek = randomblob(length(encrypted_dek)) WHERE id = '{_data.Version}'");
        var before = Fingerprint();

        var report = await RunAsync();

        report.Blocking.Should().ContainSingle(p => p.Table == "tbl_article_version" && p.RowKey.Equals(_data.Version, StringComparison.OrdinalIgnoreCase));
        Fingerprint().Should().Equal(before, "the pre-flight writes nothing");
    }

    [Theory]
    [InlineData("tbl_article_body", "encrypted_dek")]
    [InlineData("tbl_conflict_version", "ciphertext")]
    [InlineData("tbl_media", "encrypted_dek")]
    [InlineData("tbl_remote_account", "encrypted_token")]
    [InlineData("tbl_sealed_secret", "wrapped")]
    public async Task EachKindOfRow_ThatDoesNotOpen_Blocks(string table, string column)
    {
        Mutate($"UPDATE {table} SET {column} = randomblob(length({column}))");

        var report = await RunAsync();

        report.Blocking.Should().Contain(p => p.Table == table);
    }

    [Fact]
    public async Task EveryComment_IsOpened_NotOnlyTheFirst()
    {
        Mutate($"UPDATE tbl_comment SET ciphertext = randomblob(length(ciphertext)) WHERE comment_id = '{_data.LastComment}' COLLATE NOCASE");

        var report = await RunAsync();

        report.Blocking.Should().ContainSingle(p => p.Table == "tbl_comment" && p.RowKey.Equals(_data.LastComment.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A purged body whose versions remain: its sealed comments are dropped from the copy (option C), so they
    /// are a warning, not a block.</summary>
    [Fact]
    public async Task ACommentOfAPurgedBody_WithVersionsLeft_IsAWarning()
    {
        Mutate($"DELETE FROM tbl_article_body WHERE article_id = '{Upper(_data.Purged)}' COLLATE NOCASE");

        var report = await RunAsync();

        report.Blocking.Should().BeEmpty();
        report.Warnings.Should().ContainSingle(w => w.Contains(_data.Purged.ToString(), StringComparison.OrdinalIgnoreCase) && w.Contains("dropped"));
    }

    /// <summary>A purged article keeps its comments; its key survives only in the event log, which the pre-flight
    /// reads as a key source. Without it the comment cannot be proven openable, and that blocks.</summary>
    [Fact]
    public async Task ACommentOfAPurgedArticle_OpensThroughItsEvent_AndBlocksWithoutIt()
    {
        Mutate($"DELETE FROM tbl_article_version WHERE article_id = '{Upper(_data.Purged)}' COLLATE NOCASE",
               $"DELETE FROM tbl_article_body WHERE article_id = '{Upper(_data.Purged)}' COLLATE NOCASE");
        var report = await RunAsync();
        report.Blocking.Should().BeEmpty("the article's key is still in its events");
        report.Warnings.Should().Contain(w => w.Contains(_data.Purged.ToString(), StringComparison.OrdinalIgnoreCase) && w.Contains("dropped"),
            "the product no longer shows these comments, so the report must name them");

        Mutate($"DELETE FROM tbl_event WHERE article_id = '{Upper(_data.Purged)}' COLLATE NOCASE");
        (await RunAsync()).Blocking.Should().Contain(p => p.Table == "tbl_comment");
    }

    [Fact]
    public async Task SoftDeletedRows_AreInventoriedToo()
    {
        Mutate($"UPDATE tbl_comment SET ciphertext = randomblob(length(ciphertext)) WHERE comment_id = '{_data.DeletedComment}' COLLATE NOCASE",
               $"UPDATE tbl_media SET encrypted_dek = randomblob(length(encrypted_dek)) WHERE id = '{Upper(_data.DeletedMedia)}'");

        var report = await RunAsync();

        report.Blocking.Should().Contain(p => p.Table == "tbl_comment" && p.RowKey.Equals(_data.DeletedComment.ToString(), StringComparison.OrdinalIgnoreCase));
        report.Blocking.Should().Contain(p => p.Table == "tbl_media" && p.RowKey.Equals(_data.DeletedMedia.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MediaOnlyInItsEncFile_IsOpenedFromTheFile()
    {
        var media = Upper(_data.Media);
        byte[] blob;
        using (var conn = Open(Main, readOnly: true))
            blob = conn.ExecuteScalar<byte[]>($"SELECT b.data FROM tbl_media m JOIN tbl_blob b ON b.hash = m.ciphertext_sha256 WHERE m.id = '{media}'")!;
        Mutate($"UPDATE tbl_media SET ciphertext_sha256 = NULL WHERE id = '{media}'");
        Directory.CreateDirectory(Path.Combine(_vault, "media"));
        var file = Path.Combine(_vault, "media", $"{media}.enc");
        File.WriteAllBytes(file, blob);
        (await RunAsync()).Blocking.Should().BeEmpty();

        blob[^1] ^= 0xFF;
        File.WriteAllBytes(file, blob);
        (await RunAsync()).Blocking.Should().ContainSingle(p => p.Table == "tbl_media");
    }

    [Fact]
    public async Task CiphertextThatIsAlreadyGone_IsAWarning_NotABlock()
    {
        Mutate($"DELETE FROM tbl_blob WHERE hash = (SELECT ciphertext_hash FROM tbl_article_version WHERE id = '{_data.Version}')");

        var report = await RunAsync();

        report.Blocking.Should().BeEmpty();
        report.Warnings.Should().Contain(w => w.Contains(_data.Version, StringComparison.OrdinalIgnoreCase) && w.Contains("gone"));
    }

    [Fact]
    public async Task ChatRows_ThatDoNotOpen_Block()
    {
        MutateIn(Chat, $"UPDATE chat_message SET content_ciphertext = randomblob(length(content_ciphertext)) WHERE id = '{Upper(_data.ChatMessage)}'",
                     $"UPDATE chat_message SET content_key_v = -1 WHERE id = '{Upper(_data.LegacyDekMessage)}'");

        var report = await RunAsync();

        report.Blocking.Should().Contain(p => p.Table == "chat_message.content_ciphertext" && p.RowKey.Equals(_data.ChatMessage.ToString(), StringComparison.OrdinalIgnoreCase));
        report.Blocking.Should().Contain(p => p.RowKey.Equals(_data.LegacyDekMessage.ToString(), StringComparison.OrdinalIgnoreCase) && p.Problem.Contains("unreadable"));
    }

    [Fact]
    public async Task ALegacyChatRowUnderTheMasterDek_ThatDoesNotOpen_Blocks()
    {
        MutateIn(Chat, $"UPDATE chat_message SET content_ciphertext = randomblob(length(content_ciphertext)) WHERE id = '{Upper(_data.LegacyDekMessage)}'");

        var report = await RunAsync();

        report.Blocking.Should().ContainSingle(p => p.RowKey.Equals(_data.LegacyDekMessage.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AChatKeyThatDoesNotOpen_Blocks()
    {
        Mutate($"UPDATE tbl_node_data_key SET wrapped_key = randomblob(length(wrapped_key)) WHERE key_name = 'chat'");

        var report = await RunAsync();

        report.Blocking.Should().Contain(p => p.Table == "tbl_node_data_key" && p.RowKey == "chat");
    }

    [Theory]
    [InlineData("PROPOSED")]
    [InlineData("COMMITTING")]
    public async Task ARotationInFlight_Refuses(string state)
    {
        Mutate($"INSERT INTO tbl_dek_rotation_state (event_id, state, rotation_ts, created_at, updated_at) VALUES ('r1', '{state}', 'now', 'now', 'now')");

        (await RunAsync()).Blocking.Should().ContainSingle(p => p.Table == "tbl_dek_rotation_state");
    }

    [Fact]
    public async Task AFinishedRotation_DoesNotRefuse()
    {
        Mutate("INSERT INTO tbl_dek_rotation_state (event_id, state, rotation_ts, created_at, updated_at) VALUES ('r1', 'APPLIED', 'now', 'now', 'now')");

        (await RunAsync()).Blocking.Should().BeEmpty();
    }

    [Fact]
    public async Task APendingRestore_Refuses_InEachOfItsForms()
    {
        Mutate("INSERT INTO tbl_restore_event_state (event_id, state, created_at, updated_at) VALUES ('e1', 'PENDING', 'now', 'now')");
        (await RunAsync()).Blocking.Should().ContainSingle(p => p.Table == "tbl_restore_event_state");
        Mutate("DELETE FROM tbl_restore_event_state");

        Directory.CreateDirectory(Path.Combine(_vault, "snapshots", "restore-pending"));
        File.WriteAllText(Path.Combine(_vault, "snapshots", "restore-pending", "upload.bmbsnap"), "x");
        (await RunAsync()).Blocking.Should().ContainSingle(p => p.RowKey.EndsWith("restore-pending"));
        Directory.Delete(Path.Combine(_vault, "snapshots", "restore-pending"), recursive: true);

        File.WriteAllText(Path.Combine(_vault, "beememorybank.db.standalone-staging"), "x");
        (await RunAsync()).Blocking.Should().ContainSingle(p => p.RowKey.EndsWith("standalone-staging"));
    }

    [Fact]
    public async Task NotEnoughSpace_Refuses_AndSoDoesSpaceThatCannotBeMeasured()
    {
        var needed = (await RunAsync()).BytesNeeded;
        var sizes = new[] { "beememorybank.db", "chat.db" }.Sum(f => new FileInfo(Path.Combine(_vault, f)).Length)
                    + Directory.EnumerateFiles(Path.Combine(_vault, "media"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        needed.Should().Be((long)Math.Ceiling(sizes * RekeyPreflight.SpaceFactor));

        (await RunAsync(free: needed - 1)).Blocking.Should().ContainSingle(p => p.Table == "(volume)" && p.Problem.Contains("not enough"));
        (await RunAsync(free: needed)).Blocking.Should().BeEmpty();
        (await RunAsync(unmeasurable: true)).Blocking.Should().ContainSingle(p => p.Table == "(volume)" && p.Problem.Contains("cannot be measured"));
    }

    /// <summary>A vault copy left in the OS temp folder is found whatever its extension, and only listed: the
    /// pre-flight deletes nothing.</summary>
    [Fact]
    public async Task VaultCopiesInTheOsTempFolder_AndBlindFolders_AreListed_NeverDeleted()
    {
        var copy = Path.Combine(_osTemp, "tmp5A3F.tmp");
        File.Copy(Path.Combine(_vault, "beememorybank.db"), copy);
        var notAVault = Path.Combine(_osTemp, "other.tmp");
        File.WriteAllText(notAVault, "not sqlite");
        var restoreDir = Directory.CreateDirectory(Path.Combine(_osTemp, "bmb-restore-1234")).FullName;
        var blind = Directory.CreateDirectory(Path.Combine(_vault, "blind-tmp")).FullName;

        var report = await RunAsync();

        report.Blocking.Should().BeEmpty();
        report.Warnings.Should().Contain(w => w.StartsWith(copy));
        report.Warnings.Should().Contain(w => w.StartsWith(restoreDir));
        report.Warnings.Should().Contain(w => w.StartsWith(blind));
        report.Warnings.Should().NotContain(w => w.StartsWith(notAVault));
        File.Exists(copy).Should().BeTrue();
        Directory.Exists(restoreDir).Should().BeTrue();
        Directory.Exists(blind).Should().BeTrue();
    }

    /// <summary>
    /// A FIFO in the OS temp folder is passed over, not opened: opening one blocks until a writer comes, and on Linux
    /// every running .NET process keeps clr-debug-pipe-* FIFOs in /tmp. The pre-flight hung on E480 before this.
    /// </summary>
    [Fact]
    public async Task AFifoInTheOsTempFolder_DoesNotStallThePreflight()
    {
        if (OperatingSystem.IsWindows()) return; // no FIFOs in a Windows temp folder
        var fifo = Path.Combine(_osTemp, "clr-debug-pipe-1-1-in");
        mkfifo(fifo, Convert.ToUInt32("600", 8)).Should().Be(0, "the test needs a FIFO");
        var copy = Path.Combine(_osTemp, "tmp5A3F.tmp");
        File.Copy(Path.Combine(_vault, "beememorybank.db"), copy);

        // On a pool thread: the open of a FIFO blocks synchronously, before the pre-flight's first await.
        var run = Task.Run(() => RunAsync());
        (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(run, "a FIFO must not stall the pre-flight");

        var report = await run;
        report.Warnings.Should().Contain(w => w.StartsWith(copy), "the scan goes on past the FIFO");
        report.Warnings.Should().NotContain(w => w.StartsWith(fifo));
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

    /// <summary>
    /// Legacy plaintext in chat.db (a conversation title and a provider-key prefix from before they were sealed) is
    /// detected and named: the re-key seals it, so it does not block, but the owner is told it was there.
    /// </summary>
    [Fact]
    public async Task LegacyPlaintextTitlesAndKeyPrefixes_AreNamed_WithoutBlocking()
    {
        MutateIn(Chat,
            "UPDATE chat_conversation SET title = 'legacy title', title_ciphertext = NULL, title_iv = NULL, title_key_v = NULL",
            "UPDATE chat_api_key SET key_prefix = 'sk-legacy', key_prefix_ciphertext = NULL, key_prefix_iv = NULL, key_prefix_key_v = NULL");

        var report = await RunAsync();

        report.Blocking.Should().BeEmpty();
        report.Warnings.Should().Contain(w => w.StartsWith("chat_conversation.title_ciphertext:") && w.Contains("plaintext"));
        report.Warnings.Should().Contain(w => w.StartsWith("chat_api_key.key_prefix_ciphertext:") && w.Contains("plaintext"));
    }

    /// <summary>The keys belong to the verb; the pre-flight neither clears nor keeps any of them.</summary>
    [Fact]
    public async Task TheCandidateKeys_AreLeftAsTheyWere()
    {
        var dek = Session.GetMasterDek();
        var retired = RandomNumberGenerator.GetBytes(32);
        using var keys = new RekeyKeys((byte[])dek.Clone(), [(byte[])retired.Clone()], new byte[32], new byte[32]);

        using (var main = Open(Main, readOnly: true))
        using (var chat = Open(Chat, readOnly: true))
            await new RekeyPreflight { OsTempDir = _osTemp, FreeBytes = _ => long.MaxValue }
                .RunAsync(_vault, main, chat, keys, CancellationToken.None);

        keys.OldCandidates[0].Should().Equal(dek);
        keys.OldCandidates[1].Should().Equal(retired);
    }

    [Fact]
    public async Task AKeyThatOnlyARetiredCandidateOpens_Passes()
    {
        var report = await RunAsync(retiredFirst: true);

        report.Blocking.Should().BeEmpty("every candidate is tried, not only the predecessor");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private async Task<RekeyPreflightReport> RunAsync(long? free = long.MaxValue, bool unmeasurable = false, bool retiredFirst = false)
    {
        var dek = Session.GetMasterDek();
        using var keys = retiredFirst
            ? new RekeyKeys(RandomNumberGenerator.GetBytes(32), [dek], new byte[32], new byte[32])
            : new RekeyKeys(dek, [], new byte[32], new byte[32]);
        using var main = Open(Main, readOnly: true);
        using var chat = Open(Chat, readOnly: true);
        return await new RekeyPreflight { OsTempDir = _osTemp, FreeBytes = _ => unmeasurable ? null : free }
            .RunAsync(_vault, main, chat, keys, CancellationToken.None);
    }
}
