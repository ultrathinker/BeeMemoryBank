using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Rekey;
using BeeMemoryBank.Rekey.Steps;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The chat step of the offline re-key (rekey-offline.md §2 step 3, §4): on the copy, every chat.db row ends under
/// the fresh chat key — rows under the old chat key, legacy rows under the master DEK, legacy plaintext — and reads
/// back identically; nothing opens under an old key; the raw file keeps no old ciphertext and no sentinel; the copy's
/// chat key row is wrapped under D_c. The live vault is the frozen one (<see cref="RekeyVaultTestBase"/>), the copy a
/// second directory, as the verb's <c>VACUUM INTO</c> makes it.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class ChatRekeyStepTests : RekeyVaultTestBase
{
    private string _work = null!;
    private byte[] _dek = null!;
    private byte[] _oldChatKey = null!;

    /// <summary>The sealed columns, spelled independently of the step's own list.</summary>
    private static readonly (string Table, string Cipher, string Iv, string Version, string? Plain, bool Bytes, byte[] Aad)[] Columns =
    [
        ("chat_message", "content_ciphertext", "content_iv", "content_key_v", "content_text", false, "bmb-chat-message-content-v1"u8.ToArray()),
        ("chat_message", "tool_calls_ciphertext", "tool_calls_iv", "tool_calls_key_v", "tool_calls_json", false, "bmb-chat-message-toolcalls-v1"u8.ToArray()),
        ("chat_attachment", "blob", "iv", "key_v", null, true, "bmb-chat-attachment-blob-v1"u8.ToArray()),
        ("chat_api_key", "ciphertext", "iv", "key_v", null, false, "bmb-openrouter-key-v1"u8.ToArray()),
    ];

    private static readonly string[] Sentinels =
    [
        "CHAT-SENTINEL-content", "CHAT-SENTINEL-tool", "CHAT-SENTINEL-attachment", "CHAT-SENTINEL-provider-key",
        "CHAT-SENTINEL-legacy-dek", "CHAT-SENTINEL-legacy-plain", "CHAT-SENTINEL-legacy-attachment",
    ];

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _dek = Session.GetMasterDek();
        using (var main = Open(Main, readOnly: true))
        {
            var row = main.QuerySingle<(byte[] Wrapped, byte[] Iv)>("SELECT wrapped_key, iv FROM tbl_node_data_key WHERE key_name = 'chat'");
            _oldChatKey = ChatDataKeyEnvelope.TryUnwrap(row.Wrapped, row.Iv, _dek)!;
        }
        _work = _factory.DataPath + "-rekey-new";
        Directory.CreateDirectory(_work);
        foreach (var db in new[] { "beememorybank.db", "chat.db" })
            File.Copy(Path.Combine(_vault, db), Path.Combine(_work, db));
    }

    public override async Task DisposeAsync()
    {
        _main?.Dispose();
        _chat?.Dispose();
        await base.DisposeAsync();
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    [Fact]
    public async Task EveryChatRow_ReadsBackIdentically_UnderTheNewChatKey()
    {
        CountRows().Should().OnlyContain(kv => kv.Value > 0, "the fixture must hold every kind of row it claims to");
        var before = Plaintexts(WorkChat, row => OpenOld(row));

        var (result, problems, keys) = await RunAsync();

        problems.Should().BeEmpty();
        Plaintexts(WorkChat, row => OpenUnder(row, keys.ChatKey)).Should().BeEquivalentTo(before);
        result.Counts.Should().ContainKey("chat_message.content_ciphertext (was plaintext)");
        result.Counts.Should().ContainKey("chat_attachment.blob (was plaintext)");
        using var chat = Open(WorkChat, readOnly: true);
        chat.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_message WHERE content_text IS NOT NULL OR tool_calls_json IS NOT NULL")
            .Should().Be(0, "legacy plaintext is sealed and its column cleared");
        chat.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_attachment WHERE iv IS NULL OR key_v IS NOT 1").Should().Be(0);
    }

    /// <summary>The D1 attacker: every old key against every row of the copy, and the raw file searched for every old
    /// ciphertext and every sentinel.</summary>
    [Fact]
    public async Task NothingOpensUnderAnOldKey_AndTheRawFileKeepsNoOldBytes()
    {
        var oldCiphertexts = Ciphertexts(WorkChat);
        oldCiphertexts.Should().NotBeEmpty();

        var (_, problems, _) = await RunAsync();

        problems.Should().BeEmpty();
        foreach (var old in new[] { _oldChatKey, _dek })
            Plaintexts(WorkChat, row => TryOpenUnder(row, old)).Values.Should().OnlyContain(v => v == null, "no row opens under an old key");
        SqliteConnection.ClearAllPools();
        var raw = RawBytes(WorkChat);
        foreach (var sentinel in Sentinels)
            raw.AsSpan().IndexOf(Encoding.UTF8.GetBytes(sentinel)).Should().BeLessThan(0, $"'{sentinel}' must not be left in chat.db");
        foreach (var (id, bytes) in oldCiphertexts)
            raw.AsSpan().IndexOf(bytes).Should().BeLessThan(0, $"the old ciphertext of {id} must not be left in chat.db");
    }

    [Fact]
    public async Task TheCopysChatKeyRow_IsUnderTheNewDek_AndNoOldOne()
    {
        var (_, _, keys) = await RunAsync();

        using var main = Open(WorkMain, readOnly: true);
        var row = main.QuerySingle<(byte[] Wrapped, byte[] Iv)>("SELECT wrapped_key, iv FROM tbl_node_data_key WHERE key_name = 'chat'");
        ChatDataKeyEnvelope.TryUnwrap(row.Wrapped, row.Iv, keys.CampaignDek).Should().Equal(keys.ChatKey);
        ChatDataKeyEnvelope.TryUnwrap(row.Wrapped, row.Iv, _dek).Should().BeNull();
    }

    [Fact]
    public async Task TheLiveVault_IsNotTouched()
    {
        var before = Fingerprint();

        await RunAsync();

        Fingerprint().Should().Equal(before);
    }

    /// <summary>The verification is not a formality: a row carried under its old key, and plaintext left behind,
    /// are both found.</summary>
    [Fact]
    public async Task Verify_FindsARowLeftUnderItsOldKey_AndPlaintextLeftBehind()
    {
        var old = Ciphertexts(WorkChat).First(c => c.Id.StartsWith("chat_message.content_ciphertext"));
        var message = old.Id.Split(' ')[1];
        using var keys = NewKeys();
        var step = new ChatRekeyStep();
        await step.RunAsync(Context(keys));

        MutateIn(WorkChat, $"UPDATE chat_message SET content_ciphertext = x'{Convert.ToHexString(old.Bytes)}' WHERE id = '{message}'");
        MutateIn(WorkChat, $"UPDATE chat_message SET tool_calls_json = 'CHAT-SENTINEL-put-back' WHERE id = '{message}'");
        MutateIn(WorkChat, $"UPDATE chat_attachment SET iv = NULL, key_v = NULL WHERE rowid = (SELECT MIN(rowid) FROM chat_attachment)");

        var problems = await step.VerifyAsync(Context(keys));

        problems.Should().Contain(p => p.Table == "chat_message.content_ciphertext" && p.RowKey == message);
        problems.Should().Contain(p => p.Table == "chat_message.tool_calls_ciphertext" && p.RowKey == message && p.Problem.Contains("plaintext"));
        problems.Should().Contain(p => p.Table == "chat_attachment.blob" && p.Problem.Contains("plaintext"));
    }

    [Fact]
    public async Task Verify_FindsPlaintextLeftInPlaceOfItsCiphertext()
    {
        using var keys = NewKeys();
        var step = new ChatRekeyStep();
        await step.RunAsync(Context(keys));
        var id = Upper(_data.ChatMessage);
        MutateIn(WorkChat, $"UPDATE chat_message SET content_ciphertext = NULL, content_iv = NULL, content_key_v = NULL, content_text = 'CHAT-SENTINEL-back' WHERE id = '{id}'");

        var problems = await step.VerifyAsync(Context(keys));

        problems.Should().ContainSingle(p => p.Table == "chat_message.content_ciphertext" && p.RowKey == id && p.Problem.Contains("plaintext"));
    }

    [Fact]
    public async Task Verify_FindsAChatKeyRowThatIsNotTheNewKey()
    {
        using var keys = NewKeys();
        var step = new ChatRekeyStep();
        await step.RunAsync(Context(keys));
        var (wrapped, iv) = ChatDataKeyEnvelope.Wrap(RandomNumberGenerator.GetBytes(32), keys.CampaignDek);
        using (var main = Open(WorkMain, readOnly: false))
            main.Execute("UPDATE tbl_node_data_key SET wrapped_key = @wrapped, iv = @iv WHERE key_name = 'chat'", new { wrapped, iv });

        var problems = await step.VerifyAsync(Context(keys));

        problems.Should().Contain(p => p.Table == "tbl_node_data_key" && p.RowKey == "chat");
    }

    [Fact]
    public async Task Verify_FindsARowThatStillOpensUnderTheOldChatKey()
    {
        using var keys = NewKeys();
        var step = new ChatRekeyStep();
        await step.RunAsync(Context(keys));
        var id = Upper(_data.ChatMessage);
        var (ct, iv) = ArticleEncryptor.Encrypt("CHAT-SENTINEL-old-key", _oldChatKey, "bmb-chat-message-content-v1"u8.ToArray());
        using (var chat = Open(WorkChat, readOnly: false))
            chat.Execute("UPDATE chat_message SET content_ciphertext = @ct, content_iv = @iv WHERE id = @id", new { ct, iv, id });

        var problems = await step.VerifyAsync(Context(keys));

        problems.Should().Contain(p => p.RowKey == id && p.Problem.Contains("old key"));
    }

    [Fact]
    public async Task ARowMarkedUnreadable_StopsTheStep()
    {
        MutateIn(WorkChat, $"UPDATE chat_message SET content_key_v = -1 WHERE id = '{Upper(_data.LegacyDekMessage)}'");

        var run = () => RunAsync();

        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*unreadable*");
    }

    [Fact]
    public async Task WithoutChatDb_OnlyTheKeyRowIsReplaced()
    {
        using var keys = NewKeys();
        using var main = Open(WorkMain, readOnly: false);
        var ctx = new RekeyContext(_vault, _work, main, null, keys, Guid.NewGuid(), new NoProgress(), CancellationToken.None);

        var step = new ChatRekeyStep();
        await step.RunAsync(ctx);

        (await step.VerifyAsync(ctx)).Should().BeEmpty();
    }

    [Fact]
    public void EveryChatDbTable_HasAFate()
    {
        using var chat = Open(Chat, readOnly: true);
        var tables = chat.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").ToList();

        tables.Should().NotBeEmpty();
        tables.Should().OnlyContain(t => RekeyTables.Chat.ContainsKey(t), "a chat.db table without a fate would be carried unexamined");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private sealed class NoProgress : IRekeyProgress
    {
        public void Report(string step, long done, long total, string? note = null) { }
    }

    private string WorkMain => Path.Combine(_work, "beememorybank.db");
    private string WorkChat => Path.Combine(_work, "chat.db");

    private RekeyKeys NewKeys() =>
        new((byte[])_dek.Clone(), [], RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

    private SqliteConnection? _main, _chat;

    private RekeyContext Context(RekeyKeys keys)
    {
        _main?.Dispose();
        _chat?.Dispose();
        _main = Open(WorkMain, readOnly: false);
        _chat = Open(WorkChat, readOnly: false);
        return new RekeyContext(_vault, _work, _main, _chat, keys, Guid.NewGuid(), new NoProgress(), CancellationToken.None);
    }

    /// <summary>Runs the step and its verification on the copy; the keys it used are returned (still live, as the
    /// orchestrator holds them until the end).</summary>
    private async Task<(RekeyStepResult Result, IReadOnlyList<RekeyProblem> Problems, RekeyKeys Keys)> RunAsync()
    {
        var keys = NewKeys();
        var step = new ChatRekeyStep();
        var result = await step.RunAsync(Context(keys));
        var problems = await step.VerifyAsync(Context(keys));
        _main?.Dispose();
        _chat?.Dispose();
        _main = _chat = null;
        return (result, problems, keys);
    }

    private sealed record Row(string Table, string Column, string Id, byte[]? Cipher, byte[]? Iv, long? Version, string? Plain, bool Bytes, byte[] Aad);

    private static List<Row> Rows(string chatPath)
    {
        using var chat = Open(chatPath, readOnly: true);
        var rows = new List<Row>();
        foreach (var c in Columns)
        {
            var plain = c.Plain ?? "NULL";
            foreach (var r in chat.Query<(string Id, byte[]? Cipher, byte[]? Iv, long? Version, string? Plain)>(
                         $"SELECT id, {c.Cipher}, {c.Iv}, {c.Version}, {plain} FROM {c.Table}"))
                rows.Add(new Row(c.Table, c.Cipher, r.Id, r.Cipher, r.Iv, r.Version, r.Plain, c.Bytes, c.Aad));
        }
        return rows;
    }

    /// <summary>What every sealed column of every row reads as, keyed by column and row.</summary>
    private static Dictionary<string, string?> Plaintexts(string chatPath, Func<Row, string?> open) =>
        Rows(chatPath).Where(r => r.Cipher != null || r.Plain is { Length: > 0 })
            .ToDictionary(r => $"{r.Table}.{r.Column} {r.Id}", open);

    private List<(string Id, byte[] Bytes)> Ciphertexts(string chatPath) =>
        Rows(chatPath).Where(r => r.Cipher != null && r.Iv != null).Select(r => ($"{r.Table}.{r.Column} {r.Id}", r.Cipher!)).ToList();

    /// <summary>As the product reads a row today: the chat key at version 1, the master DEK for a legacy ciphertext,
    /// the plaintext column (or an IV-less attachment) for legacy plaintext.</summary>
    private string? OpenOld(Row r)
    {
        if (r.Cipher is null) return r.Plain;
        if (r.Bytes && r.Iv is null) return Convert.ToBase64String(r.Cipher);
        return TryOpenUnder(r, r.Version == 1 ? _oldChatKey : _dek) ?? throw new InvalidOperationException($"{r.Id} does not open");
    }

    private static string? OpenUnder(Row r, byte[] key) =>
        TryOpenUnder(r, key) ?? throw new InvalidOperationException($"{r.Table}.{r.Column} {r.Id} does not open under the new chat key");

    private static string? TryOpenUnder(Row r, byte[] key)
    {
        if (r.Cipher is null || r.Iv is null) return null;
        try
        {
            return r.Bytes ? Convert.ToBase64String(MediaEncryptor.Decrypt(r.Cipher, r.Iv, key, r.Aad)) : ArticleEncryptor.Decrypt(r.Cipher, r.Iv, key, r.Aad);
        }
        catch (CryptographicException) { return null; }
    }

    private static byte[] RawBytes(string db)
    {
        using var ms = new MemoryStream();
        foreach (var file in new[] { db, db + "-wal", db + "-journal" })
            if (File.Exists(file))
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    fs.CopyTo(ms);
        return ms.ToArray();
    }
}
