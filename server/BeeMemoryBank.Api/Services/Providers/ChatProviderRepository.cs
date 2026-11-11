using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace BeeMemoryBank.Api.Services.Providers;

/// <summary>
/// The provider registry (<c>chat_provider</c> in chat.db, node-local like the rest of the chat's settings: never synced, never in a
/// snapshot). A provider is a name, a protocol, a base URL, a "local" flag, wire options and an approval.
///
/// <para><b>The approval is sealed.</b> Approving writes a stamp - the protocol, the normalized base URL and the local flag - sealed under the
/// node chat key (<see cref="ChatDataProtector"/>) with the provider's id in the AAD. A provider is approved only while that stamp opens
/// and names exactly what the row says now: a new base URL, protocol or local flag withdraws the approval (the update clears the stamp, and
/// a stamp left behind would no longer match). Somebody who can write chat.db but does not hold the vault password can neither approve a
/// provider nor point an approved one at another host: 2.9.0 had the OpenRouter address compiled in, and a plaintext "approved" flag
/// would have been a way to send the keys and the notes elsewhere that 2.9.0 did not have. The built-in OpenRouter provider
/// (<see cref="BuiltInOpenRouter"/>) needs no stamp: its address is fixed in code, exactly as in 2.9.0, and a row that says anything else is
/// not approved.</para>
///
/// <para><see cref="ApprovedHost"/>, <c>approved_by</c> and <c>approved_at</c> are plaintext for display only; nothing trusts them.</para>
/// </summary>
public sealed class ChatProviderRepository(ChatDbConnectionFactory factory, ChatDataProtector protector) : ChatRepositoryBase(factory)
{
    private const string Cols = @"id AS Id, name AS Name, preset_id AS PresetId, protocol AS Protocol, base_url AS BaseUrl,
        is_local AS IsLocal, options_json AS OptionsJson, approval_ciphertext AS ApprovalCiphertext, approval_iv AS ApprovalIv,
        approval_key_v AS ApprovalKeyVersion, approved_host AS ApprovedHost, approved_by AS ApprovedBy, approved_at AS ApprovedAt,
        created_at AS CreatedAt, updated_at AS UpdatedAt";

    /// <summary>The AAD of a provider's approval stamp (also spelled in BeeMemoryBank.Rekey's ChatColumns; a test keeps them equal).</summary>
    public static byte[] ApprovalAad(Guid id) => Encoding.UTF8.GetBytes($"bmb-chat-provider-approval-v1:{id:D}");

    /// <summary>What an approval stamps: the three things that decide where the content goes and how.</summary>
    internal static string ApprovalStamp(string protocol, string baseUrl, bool isLocal) =>
        $"bmb-provider-approval-v1\n{protocol}\n{baseUrl}\n{(isLocal ? 1 : 0)}";

    /// <summary>
    /// The built-in OpenRouter row, created when the node has keys or models of 2.9.0 (or one is added the old way) and it is missing. The
    /// statement is idempotent; the startup schema step (<see cref="ChatDbInitializer"/>) runs it guarded by "the legacy tables are not
    /// empty", so a node upgraded from 2.9.0 has the provider with nobody doing anything, and a node that never used the chat has none.
    /// </summary>
    internal const string EnsureBuiltInSql = @"INSERT OR IGNORE INTO chat_provider
        (id, name, preset_id, protocol, base_url, is_local, options_json, created_at, updated_at)
        VALUES (@id, @name, @presetId, @protocol, @baseUrl, 0, @options, @now, @now)";

    internal static object BuiltInParameters() => new
    {
        id = BuiltInOpenRouter.Id,
        name = BuiltInOpenRouter.Name,
        presetId = BuiltInOpenRouter.PresetId,
        protocol = ProviderProtocols.OpenAiChat,
        baseUrl = BuiltInOpenRouter.BaseUrl,
        options = ProviderWireOptions.OpenRouter.ToJson(),
        now = DateTime.UtcNow.ToString("o"),
    };

    public async Task<List<ChatProvider>> ListAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<ChatProvider>($"SELECT {Cols} FROM chat_provider ORDER BY created_at ASC, name ASC")).ToList();
    }

    public async Task<ChatProvider?> GetAsync(Guid id)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<ChatProvider>($"SELECT {Cols} FROM chat_provider WHERE id = @id", new { id });
    }

    /// <summary>The built-in provider's row, created if it is missing (a key or a model added the 2.9.0 way names it).</summary>
    public async Task EnsureBuiltInAsync()
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(EnsureBuiltInSql, BuiltInParameters());
    }

    /// <summary>A new provider, not approved. The caller has normalized its base URL (<see cref="ProviderUrl"/>).</summary>
    public async Task CreateAsync(ChatProvider p)
    {
        if (p.IsBuiltIn) throw new InvalidOperationException("The built-in provider is created by the schema step, not by hand.");
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO chat_provider (id, name, preset_id, protocol, base_url, is_local, options_json, created_at, updated_at)
              VALUES (@Id, @Name, @PresetId, @Protocol, @BaseUrl, @IsLocal, @OptionsJson, @CreatedAt, @UpdatedAt)", p);
    }

    /// <summary>Saves name, protocol, base URL, local flag and options. When the protocol, base URL or local flag changed, the approval is
    /// withdrawn in the same statement (the stamp and its display columns cleared).</summary>
    public async Task UpdateAsync(ChatProvider before, ChatProvider after)
    {
        var withdraw = before.Protocol != after.Protocol || before.BaseUrl != after.BaseUrl || before.IsLocal != after.IsLocal;
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE chat_provider SET name = @Name, protocol = @Protocol, base_url = @BaseUrl, is_local = @IsLocal, options_json = @OptionsJson,
                     updated_at = @now" +
            (withdraw ? ", approval_ciphertext = NULL, approval_iv = NULL, approval_key_v = NULL, approved_host = NULL, approved_by = NULL, approved_at = NULL" : "") +
            " WHERE id = @Id",
            new { after.Id, after.Name, after.Protocol, after.BaseUrl, after.IsLocal, after.OptionsJson, now = UtcNow() });
    }

    /// <summary>Seals the approval of the provider as it is now. Needs an unlocked vault (the chat key).</summary>
    public async Task ApproveAsync(ChatProvider p, int? userId)
    {
        if (p.IsBuiltIn) return;
        using var lease = await protector.AcquireAsync();
        var (ciphertext, iv) = lease.EncryptText(ApprovalStamp(p.Protocol, p.BaseUrl, p.IsLocal), ApprovalAad(p.Id));
        using var conn = OpenConnection();
        // The WHERE pins the row to what was stamped: a concurrent edit of the address leaves the provider unapproved, never approved
        // for an address nobody confirmed.
        var updated = await conn.ExecuteAsync(
            @"UPDATE chat_provider SET approval_ciphertext = @ciphertext, approval_iv = @iv, approval_key_v = @version,
                     approved_host = @host, approved_by = @userId, approved_at = @now
              WHERE id = @Id AND protocol = @Protocol AND base_url = @BaseUrl AND is_local = @IsLocal",
            new
            {
                p.Id, p.Protocol, p.BaseUrl, p.IsLocal, ciphertext, iv, version = ChatDataProtector.ChatKeyVersion,
                host = ProviderUrl.DisplayHost(p.BaseUrl), userId, now = UtcNow(),
            });
        if (updated != 1)
            throw new InvalidOperationException("The provider changed while it was being approved; check its address and approve it again.");
    }

    /// <summary>Withdraws an approval (the provider stays, nothing is sent to it until it is approved again).</summary>
    public async Task WithdrawApprovalAsync(Guid id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE chat_provider SET approval_ciphertext = NULL, approval_iv = NULL, approval_key_v = NULL, approved_host = NULL,
                     approved_by = NULL, approved_at = NULL, updated_at = @now WHERE id = @id", new { id, now = UtcNow() });
    }

    /// <summary>
    /// Whether each provider is approved, index-aligned with <paramref name="providers"/>. Null for a provider whose stamp cannot be checked
    /// because the vault is locked (or the chat key is not available right now); the caller treats that as not approved.
    /// </summary>
    public async Task<bool?[]> CheckApprovalsAsync(IReadOnlyList<ChatProvider> providers)
    {
        var result = new bool?[providers.Count];
        ChatKeyLease? lease = null;
        var needsKey = providers.Any(p => !p.IsBuiltIn && p.ApprovalCiphertext is { Length: > 0 });
        var locked = false;
        if (needsKey)
        {
            try { lease = await protector.TryAcquireForReadAsync(); }
            catch (BeeMemoryBank.Core.Exceptions.SessionLockedException) { locked = true; }
        }
        using (lease)
        {
            for (var i = 0; i < providers.Count; i++)
            {
                var p = providers[i];
                if (p.IsBuiltIn) { result[i] = IsBuiltInIntact(p); continue; }
                if (p.ApprovalCiphertext is not { Length: > 0 } || p.ApprovalIv is not { Length: > 0 }) { result[i] = false; continue; }
                if (locked || lease is null) { result[i] = null; continue; }
                var stamp = p.ApprovalKeyVersion == ChatDataProtector.ChatKeyVersion
                    ? protector.TryDecryptText(lease, p.ApprovalCiphertext, p.ApprovalIv, p.ApprovalKeyVersion, ApprovalAad(p.Id))
                    : null;
                result[i] = stamp is not null && FixedEquals(stamp, ApprovalStamp(p.Protocol, p.BaseUrl, p.IsLocal));
            }
        }
        return result;
    }

    /// <summary>The built-in row is approved only while it says what 2.9.0 had compiled in.</summary>
    private static bool IsBuiltInIntact(ChatProvider p) =>
        p.Protocol == ProviderProtocols.OpenAiChat && p.BaseUrl == BuiltInOpenRouter.BaseUrl && !p.IsLocal;

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>
    /// The egress target of an approved provider, or the reason there is none (not approved, the vault locked, a stored address that no
    /// longer passes the rules). Nothing is ever sent to a provider this does not return a target for.
    /// </summary>
    /// <exception cref="Core.Exceptions.SessionLockedException">The vault is locked: no approval can be checked.</exception>
    public async Task<(ProviderEgressTarget? Target, string? Problem)> ResolveTargetAsync(ChatProvider p)
    {
        var approved = (await CheckApprovalsAsync([p]))[0];
        if (approved is null)
            throw new Core.Exceptions.SessionLockedException("The vault is locked: the approval of a provider cannot be checked.");
        if (approved != true)
            return (null, $"Provider '{p.Name}' is not approved. A superadmin approves it under Admin, AI / Chat.");
        var (url, error) = ProviderUrl.Normalize(p.BaseUrl, p.IsLocal);
        if (url is null || url != p.BaseUrl)
            return (null, $"The address of provider '{p.Name}' is not valid ({error ?? "it is not in its normalized form"}).");
        return (new ProviderEgressTarget(p.Id, p.Name, p.Protocol, new Uri(url), p.IsLocal, p.Options), null);
    }

    /// <summary>
    /// Deletes the provider with its keys and models, in one transaction. For the built-in provider that means the rows of
    /// <c>chat_api_key</c> and <c>chat_model</c> too: a node rolled back to 2.9.0 must not find a key that was deleted here.
    /// </summary>
    public async Task DeleteAsync(Guid id)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        if (id == BuiltInOpenRouter.Id)
        {
            await conn.ExecuteAsync("DELETE FROM chat_api_key", transaction: tx);
            await conn.ExecuteAsync("DELETE FROM chat_model", transaction: tx);
        }
        await conn.ExecuteAsync("DELETE FROM chat_provider_key WHERE provider_id = @id", new { id }, tx);
        await conn.ExecuteAsync("DELETE FROM chat_provider_model WHERE provider_id = @id", new { id }, tx);
        await conn.ExecuteAsync("DELETE FROM chat_provider WHERE id = @id", new { id }, tx);
        tx.Commit();
    }
}
