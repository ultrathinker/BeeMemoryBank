using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Clears the Api-owned state a node reset must not leave behind. Runs as an
/// <see cref="INodeResetHook"/> because none of it belongs to Core's
/// <see cref="NodeResetService"/>, which only knows about the vault database and its media files.
///
/// <para>
/// <b>Issued sync tokens</b> — held in memory by <see cref="SyncTokenStore"/> and validated against
/// that table alone, without re-checking the whitelist. The wipe empties the whitelist, so a peer
/// that authenticated shortly before the reset would otherwise keep a working pull token against
/// the NEW vault until it expired.
/// </para>
/// <para>
/// <b>chat.db</b> — sits in the same data directory and holds this node's AI-chat history:
/// conversation transcripts and tool-result JSON that can include decrypted article bodies the AI
/// read during a turn (see McpResponseManager / ChatEndpoints). chat_model / chat_settings are left
/// alone: they are node-local operational config (the configured model catalogue, the global chat
/// toggle), not vault content.
/// </para>
/// </summary>
public sealed class ApiStateResetHook(ChatDbConnectionFactory chatDbConnFactory, SyncTokenStore syncTokens) : INodeResetHook
{
    public Task AfterVaultWipedAsync(CancellationToken ct)
    {
        // Issued sync tokens live in memory and are validated against that table alone — the
        // whitelist row that authorized them is not consulted again. The wipe empties the
        // whitelist, so without this a peer that authenticated just before the reset would keep a
        // working pull token against the NEW vault for the remainder of its hour.
        syncTokens.Clear();

        using var chatConn = chatDbConnFactory.CreateConnection();
        using var chatTx = chatConn.BeginTransaction();
        foreach (var chatTable in new[] { "chat_attachment", "chat_message", "chat_conversation", "chat_api_key" })
        {
            using var chatDel = chatConn.CreateCommand();
            chatDel.Transaction = chatTx;
            chatDel.CommandText = $"DELETE FROM [{chatTable}]";
            chatDel.ExecuteNonQuery();
        }
        chatTx.Commit();
        return Task.CompletedTask;
    }
}
