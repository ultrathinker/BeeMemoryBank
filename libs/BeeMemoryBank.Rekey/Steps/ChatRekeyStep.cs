using System.Security.Cryptography;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Rekey.Steps;

/// <summary>
/// chat.db on the copy, re-encrypted whole under the fresh chat key <see cref="RekeyKeys.ChatKey"/>, and the copy's
/// <c>chat</c> row of <c>tbl_node_data_key</c> wrapped under D_c (rekey-offline.md §2 step 3). Every sealed column
/// of every row: rows under the old chat key, legacy rows sealed directly under the master DEK, and legacy plaintext
/// (sealed now, its plaintext column cleared). The copy is thrown away if anything fails, so there are no
/// generations and no previous-key row: one transaction, all or nothing.
///
/// <para>A row the old keys do not open stops the step; the pre-flight lists such rows before anything is
/// created, so reaching one here means the vault changed under the lock.</para>
///
/// <para>Titles and key prefixes are plaintext columns on a schema without their sealed twins; they are carried
/// as they are (see <see cref="RekeyTables.Chat"/>). The old ciphertext left in free pages is the scrub's (step 5).</para>
/// </summary>
public sealed class ChatRekeyStep : IRekeyStep
{
    public const string StepName = "ChatRekey";

    private const string MainFile = "beememorybank.db";

    public string Name => StepName;

    public Task<RekeyStepResult> RunAsync(RekeyContext ctx)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var notes = new List<string>();
        var keys = ctx.Keys;

        if (ctx.Chat is null)
            notes.Add("no chat.db: only the chat key row is replaced");
        else
        {
            var oldChatKey = OldChatKey(ctx.Main, keys);
            try
            {
                using var secure = ctx.Chat.CreateCommand();
                secure.CommandText = "PRAGMA secure_delete = ON";
                secure.ExecuteNonQuery();

                using var tx = ctx.Chat.BeginTransaction();
                var columns = ChatColumns.All.Where(c => HasColumns(ctx.Chat, c)).ToList();
                long total = columns.Sum(c => Count(ctx.Chat, c)), done = 0;
                foreach (var column in columns)
                {
                    var (resealed, sealedPlain) = Reseal(ctx, tx, column, oldChatKey, ref done, total);
                    counts[column.Name] = resealed;
                    if (sealedPlain > 0) counts[column.Name + " (was plaintext)"] = sealedPlain;
                }
                tx.Commit();
            }
            finally
            {
                if (oldChatKey != null) CryptographicOperations.ZeroMemory(oldChatKey);
            }
        }

        var (wrapped, iv) = ChatDataKeyEnvelope.Wrap(keys.ChatKey, keys.CampaignDek);
        using (var cmd = ctx.Main.CreateCommand())
        {
            cmd.CommandText = $"""
                INSERT INTO {NodeDataKeyEnvelope.TableName} (key_name, wrapped_key, iv, created_at) VALUES ($name, $wrapped, $iv, $now)
                ON CONFLICT(key_name) DO UPDATE SET wrapped_key = excluded.wrapped_key, iv = excluded.iv, created_at = excluded.created_at
                """;
            cmd.Parameters.AddWithValue("$name", ChatDataKeyEnvelope.KeyName);
            cmd.Parameters.AddWithValue("$wrapped", wrapped);
            cmd.Parameters.AddWithValue("$iv", iv);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        counts[NodeDataKeyEnvelope.TableName + "." + ChatDataKeyEnvelope.KeyName] = 1;
        return Task.FromResult(new RekeyStepResult(Name, counts, notes));
    }

    /// <summary>
    /// The copy's chat.db after the step: every sealed column at the chat key version, opening under the new chat
    /// key and under no old one (the old chat key, read again from the live vault, or any old master DEK); no legacy
    /// plaintext left; the chat key row opening under D_c to the new chat key and under no old DEK.
    /// </summary>
    public Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx)
    {
        var problems = new List<RekeyProblem>();
        var keys = ctx.Keys;
        var table = NodeDataKeyEnvelope.TableName;

        var row = KeyRow(ctx.Main);
        var opened = row is null ? null : ChatDataKeyEnvelope.TryUnwrap(row.Value.Wrapped, row.Value.Iv, keys.CampaignDek);
        if (opened is null || !CryptographicOperations.FixedTimeEquals(opened, keys.ChatKey))
            problems.Add(new RekeyProblem(table, ChatDataKeyEnvelope.KeyName, "the chat key row does not open under the new DEK to the new chat key"));
        if (opened != null) CryptographicOperations.ZeroMemory(opened);
        if (row is not null)
            foreach (var old in keys.OldCandidates)
                if (ChatDataKeyEnvelope.TryUnwrap(row.Value.Wrapped, row.Value.Iv, old) is { } leaked)
                {
                    CryptographicOperations.ZeroMemory(leaked);
                    problems.Add(new RekeyProblem(table, ChatDataKeyEnvelope.KeyName, "the chat key row opens under an old DEK"));
                }

        if (ctx.Chat is null) return Task.FromResult<IReadOnlyList<RekeyProblem>>(problems);

        var oldKeys = new List<byte[]>(keys.OldCandidates);
        var liveChatKey = LiveChatKey(ctx.SourceDir, keys);
        if (liveChatKey != null) oldKeys.Add(liveChatKey);
        try
        {
            foreach (var column in ChatColumns.All.Where(c => HasColumns(ctx.Chat, c)))
            {
                ctx.Ct.ThrowIfCancellationRequested();
                foreach (var r in Rows(ctx.Chat, column))
                {
                    if (r.Cipher is null)
                    {
                        if (column.Plain != null && r.Plain is { Length: > 0 })
                            problems.Add(new RekeyProblem(column.Name, r.Id, "legacy plaintext is left"));
                        continue;
                    }
                    if (r.Plain is { Length: > 0 })
                        problems.Add(new RekeyProblem(column.Name, r.Id, "legacy plaintext is left beside the ciphertext"));
                    if (r.Iv is null && r.Version is null && column.Bytes)
                    {
                        problems.Add(new RekeyProblem(column.Name, r.Id, "legacy plaintext is left"));
                        continue;
                    }
                    if (r.Version != ChatColumns.ChatKeyVersion || r.Iv is null)
                    {
                        problems.Add(new RekeyProblem(column.Name, r.Id, $"is at key version {r.Version?.ToString() ?? "NULL"}, not the chat key's"));
                        continue;
                    }
                    var aad = column.Aad(r.Id);
                    if (!Opens(column, r.Cipher, r.Iv, keys.ChatKey, aad))
                        problems.Add(new RekeyProblem(column.Name, r.Id, "does not open under the new chat key"));
                    if (oldKeys.Any(k => Opens(column, r.Cipher, r.Iv, k, aad)))
                        problems.Add(new RekeyProblem(column.Name, r.Id, "opens under an old key"));
                }
            }
        }
        finally
        {
            if (liveChatKey != null) CryptographicOperations.ZeroMemory(liveChatKey);
        }
        return Task.FromResult<IReadOnlyList<RekeyProblem>>(problems);
    }

    // ─── Re-seal ────────────────────────────────────────────────────────────

    private static (long Resealed, long SealedPlain) Reseal(RekeyContext ctx, SqliteTransaction tx, ChatColumn column, byte[]? oldChatKey,
        ref long done, long total)
    {
        long resealed = 0, sealedPlain = 0;
        foreach (var r in Rows(ctx.Chat!, column))
        {
            ctx.Ct.ThrowIfCancellationRequested();
            byte[]? plaintext;
            var wasPlain = false;
            if (r.Cipher is null)
            {
                if (column.Plain is null || r.Plain is not { Length: > 0 }) continue; // nothing on this side
                plaintext = r.Plain;
                wasPlain = true;
            }
            else if (column.Bytes && r.Iv is null && r.Version is null)
            {
                plaintext = r.Cipher; // a legacy attachment: plaintext bytes in the blob column
                wasPlain = true;
            }
            else
                plaintext = Open(column, r, oldChatKey, ctx.Keys);

            try
            {
                var aad = column.Aad(r.Id);
                var (cipher, iv) = column.Bytes
                    ? MediaEncryptor.Encrypt(plaintext, ctx.Keys.ChatKey, aad)
                    : ArticleEncryptor.Encrypt(System.Text.Encoding.UTF8.GetString(plaintext), ctx.Keys.ChatKey, aad);
                using var cmd = ctx.Chat!.CreateCommand();
                cmd.Transaction = tx;
                var clearPlain = column.Plain != null && column.Plain != column.Cipher ? $", {column.Plain} = NULL" : "";
                cmd.CommandText = $"UPDATE {column.Table} SET {column.Cipher} = $c, {column.Iv} = $iv, {column.Version} = $v{clearPlain} WHERE id = $id";
                cmd.Parameters.AddWithValue("$c", cipher);
                cmd.Parameters.AddWithValue("$iv", iv);
                cmd.Parameters.AddWithValue("$v", ChatColumns.ChatKeyVersion);
                cmd.Parameters.AddWithValue("$id", r.Id);
                if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"{column.Name} {r.Id}: the row did not update");
                if (wasPlain) sealedPlain++; else resealed++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
            ctx.Progress.Report(StepName, ++done, total);
        }
        return (resealed, sealedPlain);
    }

    /// <summary>A sealed value opened with the key its version names, the way ChatDataProtector reads it.</summary>
    private static byte[] Open(ChatColumn column, ChatRow r, byte[]? oldChatKey, RekeyKeys keys)
    {
        if (r.Version == ChatColumns.LegacyUnreadable)
            throw new InvalidOperationException($"{column.Name} {r.Id}: marked unreadable; the pre-flight lists such rows");
        if (r.Iv is not { Length: > 0 })
            throw new InvalidOperationException($"{column.Name} {r.Id}: ciphertext without its IV");
        var aad = column.Aad(r.Id);
        IEnumerable<byte[]> candidates = r.Version switch
        {
            ChatColumns.ChatKeyVersion => oldChatKey is null ? [] : [oldChatKey],
            null => keys.OldCandidates,
            _ => throw new InvalidOperationException($"{column.Name} {r.Id}: key version {r.Version} is not known"),
        };
        foreach (var key in candidates)
        {
            try
            {
                return column.Bytes
                    ? MediaEncryptor.Decrypt(r.Cipher!, r.Iv, key, aad)
                    : System.Text.Encoding.UTF8.GetBytes(ArticleEncryptor.Decrypt(r.Cipher!, r.Iv, key, aad));
            }
            catch (CryptographicException) { /* the next candidate */ }
        }
        throw new InvalidOperationException($"{column.Name} {r.Id}: does not open under its old key");
    }

    // ─── Keys ───────────────────────────────────────────────────────────────

    /// <summary>The old chat key from the copy's row. The row may already be under D_c if an earlier step re-wrapped
    /// every node data key, so D_c is tried along with the old DEKs.</summary>
    private static byte[]? OldChatKey(SqliteConnection main, RekeyKeys keys)
    {
        if (KeyRow(main) is not { } row) return null;
        return new[] { keys.CampaignDek }.Concat(keys.OldCandidates)
                   .Select(k => ChatDataKeyEnvelope.TryUnwrap(row.Wrapped, row.Iv, k)).FirstOrDefault(v => v is not null)
               ?? throw new InvalidOperationException("the chat key does not open under the vault's keys; the pre-flight lists this");
    }

    /// <summary>The chat key of the live vault, for the check that nothing opens under it. Read-only.</summary>
    private static byte[]? LiveChatKey(string sourceDir, RekeyKeys keys)
    {
        var path = Path.Combine(sourceDir, MainFile);
        if (!File.Exists(path)) return null;
        using var live = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        live.Open();
        if (KeyRow(live) is not { } row) return null;
        return keys.OldCandidates.Select(k => ChatDataKeyEnvelope.TryUnwrap(row.Wrapped, row.Iv, k)).FirstOrDefault(v => v is not null);
    }

    private static (byte[] Wrapped, byte[] Iv)? KeyRow(SqliteConnection main)
    {
        using var cmd = main.CreateCommand();
        cmd.CommandText = $"SELECT wrapped_key, iv FROM {NodeDataKeyEnvelope.TableName} WHERE key_name = $name";
        cmd.Parameters.AddWithValue("$name", ChatDataKeyEnvelope.KeyName);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ((byte[])r.GetValue(0), (byte[])r.GetValue(1)) : null;
    }

    // ─── chat.db ────────────────────────────────────────────────────────────

    private sealed record ChatRow(string Id, byte[]? Cipher, byte[]? Iv, long? Version, byte[]? Plain);

    private static List<ChatRow> Rows(SqliteConnection chat, ChatColumn column)
    {
        var plain = column.Plain != null && column.Plain != column.Cipher ? column.Plain : "NULL";
        using var cmd = chat.CreateCommand();
        cmd.CommandText = $"SELECT id, {column.Cipher}, {column.Iv}, {column.Version}, {plain} FROM {column.Table}";
        using var r = cmd.ExecuteReader();
        var rows = new List<ChatRow>();
        while (r.Read())
            rows.Add(new ChatRow(r.GetString(0),
                r.IsDBNull(1) ? null : (byte[])r.GetValue(1),
                r.IsDBNull(2) ? null : (byte[])r.GetValue(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : System.Text.Encoding.UTF8.GetBytes(r.GetString(4))));
        return rows;
    }

    private static long Count(SqliteConnection chat, ChatColumn column)
    {
        using var cmd = chat.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {column.Table}";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static bool HasColumns(SqliteConnection chat, ChatColumn column)
    {
        using var cmd = chat.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{column.Table}') WHERE name IN ($c, $iv, $v)";
        cmd.Parameters.AddWithValue("$c", column.Cipher);
        cmd.Parameters.AddWithValue("$iv", column.Iv);
        cmd.Parameters.AddWithValue("$v", column.Version);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 3;
    }

    private static bool Opens(ChatColumn column, byte[] cipher, byte[] iv, byte[] key, byte[] aad)
    {
        try
        {
            if (column.Bytes) CryptographicOperations.ZeroMemory(MediaEncryptor.Decrypt(cipher, iv, key, aad));
            else ArticleEncryptor.Decrypt(cipher, iv, key, aad);
            return true;
        }
        catch (CryptographicException) { return false; }
    }
}
