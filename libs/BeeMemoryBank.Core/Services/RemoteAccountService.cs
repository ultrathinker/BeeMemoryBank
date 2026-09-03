using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Friend-side CRUD for Remote Accounts (configured pointers to other BMB
/// nodes). Handles the one-time login → token exchange, encrypts the bearer
/// token with this node's master DEK, and exposes accessor methods that the
/// pull-engine uses to talk to the owner node.
/// </summary>
public class RemoteAccountService(
    IRemoteAccountRepository accountRepo,
    IRemoteSubscriptionRepository subscriptionRepo,
    IFolderRepository folderRepo,
    INodeIdentityRepository nodeRepo,
    ILamportClock clock,
    SessionService session,
    HttpClient httpClient,
    CallerScopeHolder scopeHolder)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly byte[] TokenAad = "bmb-remote-token"u8.ToArray();

    public async Task<RemoteAccount> CreateAsync(string displayName, string baseUrl, string username, string password, string? label = null)
    {
        baseUrl = baseUrl.TrimEnd('/');
        ValidateBaseUrl(baseUrl);

        // Exchange password for a long-lived remote token. We never persist the
        // password — only the encrypted token.
        using var resp = await PostJsonAsync($"{baseUrl}/api/auth/remote-token",
            new { username, password, label });
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Remote login failed (HTTP {(int)resp.StatusCode})");

        var body = await ReadJsonAsync<TokenIssueResponse>(resp)
            ?? throw new InvalidOperationException("Remote token endpoint returned an empty body.");

        var (encrypted, iv) = EncryptToken(body.Token);

        var account = new RemoteAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName,
            BaseUrl = baseUrl,
            RemoteUsername = username,
            EncryptedToken = encrypted,
            TokenIv = iv,
            TokenExpiresAt = body.ExpiresAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await accountRepo.CreateAsync(account);
        return account;
    }

    public async Task RefreshCredentialsAsync(Guid accountId, string username, string password)
    {
        var account = await accountRepo.GetByIdAsync(accountId)
            ?? throw new KeyNotFoundException($"Remote account {accountId} not found");

        using var resp = await PostJsonAsync($"{account.BaseUrl}/api/auth/remote-token",
            new { username, password });
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Remote login failed (HTTP {(int)resp.StatusCode})");

        var body = await ReadJsonAsync<TokenIssueResponse>(resp)
            ?? throw new InvalidOperationException("Remote token endpoint returned an empty body.");

        var (encrypted, iv) = EncryptToken(body.Token);
        await accountRepo.UpdateTokenAsync(accountId, encrypted, iv, body.ExpiresAt);
    }

    public string DecryptToken(RemoteAccount account)
    {
        var masterDek = session.GetMasterDek();
        try
        {
            return ArticleEncryptor.Decrypt(account.EncryptedToken, account.TokenIv, masterDek, TokenAad);
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    private (byte[] cipher, byte[] iv) EncryptToken(string token)
    {
        var masterDek = session.GetMasterDek();
        try
        {
            return ArticleEncryptor.Encrypt(token, masterDek, TokenAad);
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    public Task<List<RemoteAccount>> ListAccountsAsync() => accountRepo.ListAllAsync();
    public Task<RemoteAccount?> GetAccountAsync(Guid id) => accountRepo.GetByIdAsync(id);

    public async Task<List<RemoteFolderInfo>> ListAccessibleAsync(Guid accountId)
    {
        var account = await accountRepo.GetByIdAsync(accountId)
            ?? throw new KeyNotFoundException($"Remote account {accountId} not found");

        var token = DecryptToken(account);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{account.BaseUrl}/api/folders/accessible");
        req.Headers.Add("Authorization", $"Bearer {token}");
        using var resp = await httpClient.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"List accessible folders failed (HTTP {(int)resp.StatusCode})");

        var doc = await ReadJsonAsync<AccessibleFoldersResponse>(resp);
        return doc?.Folders ?? [];
    }

    public async Task<RemoteSubscription> AddSubscriptionAsync(Guid accountId, Guid remoteFolderId, string remoteFolderPath, string mountPath)
    {
        // Mount path must be unique and not collide with any local folder.
        var existing = await subscriptionRepo.GetByMountPathAsync(mountPath);
        if (existing != null)
            throw new InvalidOperationException($"Mount path '{mountPath}' is already used by another subscription.");

        // SECURITY: refuse to hijack an existing local folder. The poller runs
        // under SystemCallerScope and would otherwise convert /AdminSecrets into
        // a read-only remote mirror simply because a guest user subscribed with
        // that mountPath.
        var collidingFolder = await folderRepo.GetByPathAsync(mountPath);
        if (collidingFolder != null)
            throw new InvalidOperationException(
                $"Mount path '{mountPath}' already exists locally. Pick a fresh path under (e.g.) /Shared/.");

        var sub = new RemoteSubscription
        {
            Id = Guid.NewGuid(),
            RemoteAccountId = accountId,
            RemoteFolderId = remoteFolderId.ToString(),
            RemoteFolderPath = remoteFolderPath,
            MountPath = mountPath,
            CreatedAt = DateTime.UtcNow
        };
        await subscriptionRepo.CreateAsync(sub);

        // TOCTOU close-out (kilo security review): synchronously stake out the
        // mount path with the subscription's tag, so another user can't race
        // between the CreateAsync above and the first poll cycle to create
        // /MountPath manually and have the poller adopt their folder.
        // Run under system scope so the create itself isn't refused by
        // repo-level ACL checks.
        //
        // Authorize the mount path against the CALLER's scope first — everything below runs as
        // System, so a check placed inside the swapped block would be evaluated against
        // SystemCallerScope and could never refuse anything. (The endpoint is superadmin-gated
        // today, so this is defence in depth; it stops being so the moment mirror administration
        // is delegated.)
        folderRepo.ThrowIfWriteDenied(mountPath);

        using (scopeHolder.ElevateToSystem())
        {
            var existingLocal = await folderRepo.GetByPathAsync(mountPath);
            if (existingLocal == null)
            {
                var identity = await nodeRepo.GetAsync();
                var now = DateTime.UtcNow;
                var stakeOut = new Folder
                {
                    Id = Guid.NewGuid(),
                    Path = mountPath,
                    Name = mountPath.TrimEnd('/').Split('/').Last(),
                    ParentPath = ParentOf(mountPath),
                    Status = "A",
                    LamportTs = clock.Tick(),
                    SourceNodeId = identity?.NodeId,
                    CreatedAt = now,
                    UpdatedAt = now,
                    RemoteSubscriptionId = sub.Id,
                    RemoteOriginId = remoteFolderId.ToString()
                };
                // Ensure ancestors exist before staking the leaf.
                var ancestor = stakeOut.ParentPath;
                if (!string.IsNullOrEmpty(ancestor) && ancestor != "/")
                {
                    var existingAncestor = await folderRepo.GetByPathAsync(ancestor);
                    if (existingAncestor == null)
                    {
                        // Unchecked variant: this whole block runs as System by design, and the
                        // leaf was authorized against the caller's real scope before the swap.
                        // Passing an ancestor to the leaf-checking EnsureExistsAsync would refuse
                        // mount paths whose parent lies outside an allow-list caller's scope.
                        await folderRepo.EnsureAncestorsExistAsync(ancestor, identity?.NodeId);
                    }
                }
                await folderRepo.CreateAsync(stakeOut);
            }
        }
        return sub;
    }

    private static string? ParentOf(string path)
    {
        if (path == "/" || string.IsNullOrEmpty(path)) return null;
        var t = path.TrimEnd('/');
        var i = t.LastIndexOf('/');
        return i <= 0 ? null : t[..i];
    }

    public Task<List<RemoteSubscription>> ListSubscriptionsForAccountAsync(Guid accountId)
        => subscriptionRepo.ListByAccountAsync(accountId);

    public Task<List<RemoteSubscription>> ListAllSubscriptionsAsync()
        => subscriptionRepo.ListAllAsync();

    public Task DeleteSubscriptionAsync(Guid id) => subscriptionRepo.DeleteAsync(id);
    public Task DeleteAccountAsync(Guid id) => accountRepo.DeleteAsync(id);

    // Reject anything that isn't a well-formed http/https absolute URL. http://
    // is only accepted for localhost so dev/test setups still work; everything
    // else must be https to keep the bearer token and snapshot bodies (which
    // contain decrypted article content) off the wire in plaintext.
    // Mitigates SSRF + plaintext-traffic risk.
    //
    // Additionally blocks private and link-local IP literals (10/8, 172.16/12,
    // 192.168/16, 169.254/16, etc.) to prevent the friend node's background
    // scheduler from being used as a probe / metadata-endpoint attacker once
    // an account is configured. Loopback stays allowed (Claude+gemini+kilo).
    private static void ValidateBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Remote base URL must be an absolute http(s) URL.");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Remote base URL scheme '{uri.Scheme}' not allowed; use http(s).");

        var host = uri.Host;
        var isLoopback = host is "localhost" or "127.0.0.1" or "::1";

        if (uri.Scheme == Uri.UriSchemeHttp && !isLoopback)
            throw new InvalidOperationException(
                "Plain http:// is only allowed for localhost. Use https:// for any remote host.");

        // Block private + link-local IP literals (DNS names are not blocked —
        // we cannot resolve from a service-layer call without re-introducing
        // an SSRF surface of its own; relying on TLS + admin-only RemoteAccount
        // creation as the next line of defence).
        if (!isLoopback && System.Net.IPAddress.TryParse(host, out var ip) && IsBlockedAddress(ip))
            throw new InvalidOperationException(
                $"Remote base URL host '{host}' is in a private / link-local / multicast range — refused.");
    }

    private static bool IsBlockedAddress(System.Net.IPAddress ip)
    {
        if (System.Net.IPAddress.IsLoopback(ip)) return false; // loopback handled separately
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && (b[1] & 0xF0) == 16) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 169.254.0.0/16 (link-local, cloud metadata endpoints)
            if (b[0] == 169 && b[1] == 254) return true;
            // 100.64.0.0/10 (CGNAT)
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return true;
            // 0.0.0.0/8 (this network)
            if (b[0] == 0) return true;
            // multicast 224.0.0.0/4
            if ((b[0] & 0xF0) == 224) return true;
            // broadcast / reserved 240.0.0.0/4
            if ((b[0] & 0xF0) == 240) return true;
        }
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            if (ip.IsIPv6SiteLocal) return true;
            if (ip.IsIPv6Multicast) return true;
            // Unique local fc00::/7
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;
        }
        return false;
    }

    private async Task<HttpResponseMessage> PostJsonAsync<T>(string url, T body)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return await httpClient.SendAsync(req);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    private record TokenIssueResponse(string Token, DateTime ExpiresAt, int UserId, string Username);
    private record AccessibleFoldersResponse(List<RemoteFolderInfo> Folders);
}

public record RemoteFolderInfo(Guid Id, string Path, string Name, bool IsReadOnly, int ArticleCount);
