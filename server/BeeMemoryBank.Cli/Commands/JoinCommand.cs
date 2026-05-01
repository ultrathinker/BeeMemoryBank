using System.Data;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Commands;

public static class JoinCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> HandleAsync(
        string dataPath,
        string remoteUrl,
        string password,
        string displayName,
        bool allowInsecureHttp = false,
        TextWriter? output = null)
    {
        output ??= Console.Out;

        // Reject plain http:// for non-loopback unless operator explicitly opts in.
        // Threat: a network attacker can MITM a plain-HTTP join, swap pubkeys in the
        // JoinResponse.Whitelist, and have us add their key with status='A'. They
        // can't forge events from real cluster nodes (don't have the private keys),
        // but they can DoS our sync (real events fail signature verify against
        // attacker pubkeys) and could later exfiltrate signed challenges if their
        // injected URL becomes a seeder. Forcing HTTPS closes the join-time MITM.
        // Local LAN deployments can opt in via --allow-insecure-http.
        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var parsedUrl)
            && parsedUrl.Scheme == Uri.UriSchemeHttp
            && !allowInsecureHttp
            && !IsLoopbackOrPrivateLan(parsedUrl.Host))
        {
            await output.WriteLineAsync($"Error: refusing to join over plain HTTP to non-loopback/non-LAN host '{parsedUrl.Host}'.");
            await output.WriteLineAsync("Reasons: plain HTTP allows a network attacker to MITM the JoinResponse, including the");
            await output.WriteLineAsync("inherited peer pubkeys. Use https:// (TLS) for any join over an untrusted network.");
            await output.WriteLineAsync("If you really want to join over plain HTTP (e.g. trusted lab/LAN), pass --allow-insecure-http.");
            return 2;
        }

        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();

        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var keySlotRepo = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
        var whitelistRepo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();

        if (await nodeRepo.GetAsync() != null)
        {
            await output.WriteLineAsync("Error: node is already initialized. Delete the DB to re-join.");
            return 1;
        }

        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var nodeId = Guid.NewGuid();

        await output.WriteLineAsync($"Generated nodeId: {nodeId}");
        await output.WriteLineAsync($"Connecting to {remoteUrl}...");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var joinRequest = new
        {
            masterPassword = password,
            nodeId = nodeId,
            displayName = displayName,
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        };

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(
                $"{remoteUrl.TrimEnd('/')}/api/join", joinRequest, JsonOptions);
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Connection error: {ex.Message}");
            return 1;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            await output.WriteLineAsync($"Error from remote node ({(int)response.StatusCode}): {errorBody}");
            return 1;
        }

        var joinResponse = await response.Content.ReadFromJsonAsync<JoinResponseDto>(JsonOptions);
        if (joinResponse == null)
        {
            await output.WriteLineAsync("Error: empty response from remote node");
            return 1;
        }

        await output.WriteLineAsync($"Received response from node '{joinResponse.RemoteNode.DisplayName}'");

        var slot = joinResponse.KeySlot;
        var encryptedMasterDek = Convert.FromBase64String(slot.EncryptedMasterDekB64);
        var iv = Convert.FromBase64String(slot.IvB64);
        var remoteSalt = Convert.FromBase64String(slot.SaltB64);

        byte[] masterDek;
        try
        {
            var kek = KeyDerivation.DeriveKek(password, remoteSalt,
                slot.ArgonMemory, slot.ArgonIterations, slot.ArgonParallelism);
            masterDek = MasterKeyManager.UnwrapMasterDek(encryptedMasterDek, iv, kek);
        }
        catch
        {
            await output.WriteLineAsync("Error: failed to decrypt Master DEK (incorrect password?)");
            return 1;
        }

        await output.WriteLineAsync("Master DEK received and decrypted");

        var localSalt = KeyDerivation.GenerateSalt();
        var localKek = KeyDerivation.DeriveKek(password, localSalt);
        var (localEncryptedDek, localIv) = MasterKeyManager.WrapMasterDek(masterDek, localKek);

        var now = DateTime.UtcNow;

        var (wrappedPk, pkIv) = NodeIdentityCrypto.EncryptPrivateKey(privateKey, masterDek, nodeId);
        Array.Clear(privateKey);

        var identity = new NodeIdentity
        {
            NodeId = nodeId,
            DisplayName = displayName,
            Ed25519PublicKey = publicKey,
            Ed25519PrivateKey = wrappedPk,
            Ed25519PrivateKeyIV = pkIv,
            Ed25519PrivateKeyV = 1,
            CreatedAt = now
        };
        await nodeRepo.CreateAsync(identity);

        var localSlot = new MasterKeyStore
        {
            SlotType = "user",
            EncryptedMasterDek = localEncryptedDek,
            IV = localIv,
            Salt = localSalt,
            ArgonMemory = CryptoConstants.DefaultArgonMemory,
            ArgonIterations = CryptoConstants.DefaultArgonIterations,
            ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
            CreatedAt = now
        };
        var localSlotId = await keySlotRepo.CreateAsync(localSlot);

        var user = new User
        {
            Username = displayName,
            DisplayName = displayName,
            PasswordHash = UserService.HashPassword(password),
            Role = UserRoles.Superadmin,
            KeySlotId = localSlotId,
            IsActive = true,
            CreatedAt = now
        };
        await userRepo.CreateAsync(user);

        var sentinel = MasterKeyManager.ComputeSentinel(masterDek);
        await nodeRepo.StoreSentinelAsync(sentinel);

        using (var conn = dbFactory.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO tbl_migration_marker (key, value, set_at)
                VALUES (@k, '1', @ts)";
            var p1 = cmd.CreateParameter();
            p1.ParameterName = "k";
            p1.Value = "legacy_password_unified";
            cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter();
            p2.ParameterName = "ts";
            p2.Value = DateTime.UtcNow.ToString("O");
            cmd.Parameters.Add(p2);
            cmd.ExecuteNonQuery();
        }

        Array.Clear(masterDek);

        var selfEntry = new WhitelistEntry
        {
            NodeId = nodeId,
            DisplayName = displayName,
            Ed25519PublicKey = publicKey,
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        };
        await whitelistRepo.CreateAsync(selfEntry);

        var remote = joinResponse.RemoteNode;
        var remoteEntry = new WhitelistEntry
        {
            NodeId = remote.NodeId,
            DisplayName = remote.DisplayName,
            Ed25519PublicKey = Convert.FromBase64String(remote.Ed25519PublicKeyB64),
            ApiAddress = remoteUrl.TrimEnd('/'),
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        };
        await whitelistRepo.CreateAsync(remoteEntry);

        // Inherit other peers from the remote's whitelist (for transitive trust in
        // multi-hop topologies: when joining B, get A's pubkey too so events relayed
        // by B can be signature-verified). Mirrors mobile NodeSetupService behaviour.
        if (joinResponse.Whitelist != null)
        {
            foreach (var entry in joinResponse.Whitelist)
            {
                if (entry.NodeId == nodeId) continue;
                if (entry.NodeId == remote.NodeId) continue;
                if (await whitelistRepo.GetByNodeIdAsync(entry.NodeId) != null) continue;
                try
                {
                    await whitelistRepo.CreateAsync(new WhitelistEntry
                    {
                        NodeId = entry.NodeId,
                        DisplayName = entry.DisplayName,
                        Ed25519PublicKey = Convert.FromBase64String(entry.Ed25519PublicKeyB64),
                        ApiAddress = entry.ApiAddress,
                        Status = "A",
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex)
                {
                    // SQLITE_CONSTRAINT (19) — duplicate node_id from a prior partial join.
                    // Tolerate; everything else surfaces so a malformed pubkey or schema drift
                    // doesn't silently produce a peer with garbled key (events would later fail
                    // signature verification with no visible root cause).
                    if (ex.SqliteErrorCode != 19)
                        await output.WriteLineAsync($"  Skipping peer {entry.DisplayName} ({entry.NodeId}): SQLite error {ex.SqliteErrorCode} — {ex.Message}");
                }
                catch (FormatException ex)
                {
                    await output.WriteLineAsync($"  Skipping peer {entry.DisplayName} ({entry.NodeId}): bad base64 pubkey — {ex.Message}");
                }
                catch (Exception ex)
                {
                    await output.WriteLineAsync($"  Skipping peer {entry.DisplayName} ({entry.NodeId}): {ex.GetType().Name} — {ex.Message}");
                }
            }
        }

        await output.WriteLineAsync($"Node '{displayName}' successfully joined the network.");
        await output.WriteLineAsync($"Remote node: '{remote.DisplayName}' ({remote.NodeId})");
        if (joinResponse.Whitelist != null && joinResponse.Whitelist.Count > 0)
            await output.WriteLineAsync($"Inherited {joinResponse.Whitelist.Count} peer(s) from remote's whitelist.");
        await output.WriteLineAsync("Start the services — synchronization will begin automatically.");

        return 0;
    }

    private record JoinResponseDto(
        JoinRemoteNodeDto RemoteNode,
        JoinKeySlotDto KeySlot,
        List<JoinWhitelistEntryDto>? Whitelist);

    private record JoinWhitelistEntryDto(
        Guid NodeId,
        string DisplayName,
        string Ed25519PublicKeyB64,
        string? ApiAddress);

    private record JoinRemoteNodeDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64);

    private record JoinKeySlotDto(
        string EncryptedMasterDekB64,
        string IvB64,
        string SaltB64,
        int ArgonMemory,
        int ArgonIterations,
        int ArgonParallelism);

    private static bool IsLoopbackOrPrivateLan(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!System.Net.IPAddress.TryParse(host, out var ip)) return false;
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;  // IPv6 LAN ranges not handled here; explicit opt-in required
        if (bytes[0] == 10) return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        return false;
    }
}
