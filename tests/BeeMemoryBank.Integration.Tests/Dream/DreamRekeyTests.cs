using System.Security.Cryptography;
using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Rekey;
using BeeMemoryBank.Rekey.Steps;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The offline re-key makes a new chat key (rekey-offline.md 2 step 3), so Dream's sealed rows must move with the chat's: the advice, rule and
/// journal payloads sealed under the old chat key with Dream's AADs (DreamAad) are re-sealed by <see cref="ChatRekeyStep"/>, open under the new chat key
/// with the same AADs, and under no old key. That the step opens them at all proves ChatColumns.All spells the AADs exactly as DreamAad does.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class DreamRekeyTests : RekeyVaultTestBase
{
    private string _work = null!;
    private byte[] _dek = null!;
    private byte[] _oldChatKey = null!;
    private readonly Dictionary<(string Table, string Id), string> _sealed = [];

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _dek = Session.GetMasterDek();
        using (var main = Open(Main, readOnly: true))
        {
            var row = main.QuerySingle<(byte[] Wrapped, byte[] Iv)>("SELECT wrapped_key, iv FROM tbl_node_data_key WHERE key_name = 'chat'");
            _oldChatKey = ChatDataKeyEnvelope.TryUnwrap(row.Wrapped, row.Iv, _dek)!;
        }
        _work = _factory.DataPath + "-rekey-dream";
        Directory.CreateDirectory(_work);
        foreach (var db in new[] { "beememorybank.db", "chat.db" })
            File.Copy(Path.Combine(_vault, db), Path.Combine(_work, db));

        // One sealed row of each Dream table, as the store writes them: JSON under the chat key, AAD from the row id, key_v 1.
        using var chat = Open(WorkChat, readOnly: false);
        var at = DateTime.UtcNow.ToString("o");
        foreach (var (table, aad, insert) in new (string, Func<Guid, byte[]>, string)[]
                 {
                     ("dream_advice", DreamAad.Advice,
                         "INSERT INTO dream_advice (id, run_id, seq, kind, source, apply_mode, score, created_at, payload_ciphertext, payload_iv, payload_key_v) "
                         + "VALUES (@id, 'run', 1, 'merge_tags', 'rule', 'code', 85, @at, @c, @iv, 1)"),
                     ("dream_rule", DreamAad.Rule,
                         "INSERT INTO dream_rule (id, scope, created_at, payload_ciphertext, payload_iv, payload_key_v) VALUES (@id, 'advice', @at, @c, @iv, 1)"),
                     ("dream_run_event", DreamAad.Event,
                         "INSERT INTO dream_run_event (id, seq, run_id, at, kind, payload_ciphertext, payload_iv, payload_key_v) VALUES (@id, 1, 'run', @at, 'info', @c, @iv, 1)"),
                 })
        {
            var id = Guid.NewGuid();
            var plaintext = $"{{\"v\":1,\"title\":\"DREAM-SENTINEL-{table}\"}}";
            var (c, iv) = ArticleEncryptor.Encrypt(plaintext, _oldChatKey, aad(id));
            chat.Execute(insert, new { id = id.ToString("D"), at, c, iv });
            _sealed[(table, id.ToString("D"))] = plaintext;
        }
    }

    public override async Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await base.DisposeAsync();
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private string WorkMain => Path.Combine(_work, "beememorybank.db");
    private string WorkChat => Path.Combine(_work, "chat.db");

    [Fact]
    public async Task DreamPayloads_AreResealedUnderTheNewChatKey_WithTheirAads()
    {
        var keys = new RekeyKeys((byte[])_dek.Clone(), [], RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        var step = new ChatRekeyStep();
        RekeyStepResult result;
        IReadOnlyList<RekeyProblem> problems;
        using (var main = Open(WorkMain, readOnly: false))
        using (var chat = Open(WorkChat, readOnly: false))
            result = await step.RunAsync(new RekeyContext(_vault, _work, main, chat, keys, Guid.NewGuid(), new NoProgress(), CancellationToken.None));
        using (var main = Open(WorkMain, readOnly: false))
        using (var chat = Open(WorkChat, readOnly: false))
            problems = await step.VerifyAsync(new RekeyContext(_vault, _work, main, chat, keys, Guid.NewGuid(), new NoProgress(), CancellationToken.None));

        problems.Should().BeEmpty();
        foreach (var table in new[] { "dream_advice", "dream_rule", "dream_run_event" })
            result.Counts.Should().ContainKey($"{table}.payload_ciphertext").WhoseValue.Should().Be(1, table);

        using var reread = Open(WorkChat, readOnly: true);
        foreach (var ((table, id), plaintext) in _sealed)
        {
            var row = reread.QuerySingle<(byte[] C, byte[] Iv, long V)>($"SELECT payload_ciphertext, payload_iv, payload_key_v FROM {table} WHERE id = @id", new { id });
            row.V.Should().Be(BeeMemoryBank.Api.Services.ChatDataProtector.ChatKeyVersion);
            var aad = table switch
            {
                "dream_advice" => DreamAad.Advice(Guid.Parse(id)),
                "dream_rule" => DreamAad.Rule(Guid.Parse(id)),
                _ => DreamAad.Event(Guid.Parse(id)),
            };
            ArticleEncryptor.Decrypt(row.C, row.Iv, keys.ChatKey, aad).Should().Be(plaintext, $"{table} {id}");
            FluentActions.Invoking(() => ArticleEncryptor.Decrypt(row.C, row.Iv, _oldChatKey, aad)).Should().Throw<CryptographicException>("no Dream row opens under the old chat key");
        }
    }

    private sealed class NoProgress : IRekeyProgress
    {
        public void Report(string step, long done, long total, string? note = null) { }
    }
}
