using System.Data;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        TextWriter? output = null,
        string? joinCode = null)
    {
        output ??= Console.Out;

        // A join code from the other computer's Connect a device card (address, one-time token, certificate pin) replaces the
        // address: the join goes only to the computer whose key the code pins, and /api/join carries the code's token.
        JoinCode? code = null;
        if (!string.IsNullOrWhiteSpace(joinCode))
        {
            if (!JoinCode.TryParse(joinCode, out code))
            {
                await output.WriteLineAsync($"Error: {JoinCode.NotValidMessage}");
                return 2;
            }
            remoteUrl = code.Address;
        }
        else if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            await output.WriteLineAsync("Error: give the other node's address (--remote) or the join code it shows (--code).");
            return 2;
        }

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
        var syncPositionRepo = scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>();
        var clock = scope.ServiceProvider.GetRequiredService<ILamportClock>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

        if (await nodeRepo.GetAsync() != null)
        {
            await output.WriteLineAsync("Error: node is already initialized. Delete the DB to re-join.");
            return 1;
        }

        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var nodeId = Guid.NewGuid();

        await output.WriteLineAsync($"Generated nodeId: {nodeId}");
        await output.WriteLineAsync($"Connecting to {remoteUrl}...");

        // No redirects: the join carries the master password, and a 307/308 would resend it elsewhere.
        // With a code, TLS completes only with the key the code pins (the other computer's certificate is from its own local
        // authority, which nothing here trusts).
        using var http = JoinHttp.CreateClient(code?.SpkiPin);
        http.Timeout = TimeSpan.FromSeconds(30);
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
            using var joinMessage = new HttpRequestMessage(HttpMethod.Post, $"{remoteUrl.TrimEnd('/')}/api/join")
            {
                Content = JsonContent.Create(joinRequest, options: JsonOptions)
            };
            if (code?.Token != null) joinMessage.Headers.Add(JoinCode.TokenHeader, code.Token);
            response = await http.SendAsync(joinMessage);
        }
        catch (HttpRequestException ex) when (code != null)
        {
            await output.WriteLineAsync($"Error: {JoinCode.DescribeConnectionFailure(ex)}");
            return 1;
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Connection error: {ex.Message}");
            return 1;
        }

        if (!response.IsSuccessStatusCode)
        {
            // A blind node has no join door at all (only its sync and pairing surface is mapped): say what to do instead
            // of showing a bare 404.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound && await IsBlindNodeAsync(http, remoteUrl))
            {
                await output.WriteLineAsync(
                    "Error: the remote node is a blind node. It keeps only encrypted data and cannot give a new node the vault's key " +
                    "or its notes. Join a node that holds the data (a full node) instead.");
                return 1;
            }
            var errorBody = await response.Content.ReadAsStringAsync();
            // Through a code the other computer is the pinned one, and its door says why in a sentence made for the person.
            var doorSaid = code != null ? JoinCode.ReadDoorError(errorBody) : null;
            await output.WriteLineAsync(doorSaid != null
                ? $"Error from remote node ({(int)response.StatusCode}): {doorSaid}"
                : $"Error from remote node ({(int)response.StatusCode}): {errorBody}");
            return 1;
        }

        var joinResponse = await response.Content.ReadFromJsonAsync<JoinResponseDto>(JsonOptions);
        if (joinResponse == null)
        {
            await output.WriteLineAsync("Error: empty response from remote node");
            return 1;
        }

        await output.WriteLineAsync($"Received response from node '{joinResponse.RemoteNode.DisplayName}'");

        // The same gates as the Setup page's join (InitEndpoints), before anything is written here: this node starts
        // from the host's snapshot and log, so the host must speak this node's sync protocol.
        var hostProtocol = joinResponse.RemoteNode.ProtocolVersion;
        if (hostProtocol > SyncProtocolVersion.Current)
        {
            await output.WriteLineAsync(
                $"Error: cannot join: remote node protocol version ({hostProtocol}) is higher than local version ({SyncProtocolVersion.Current}). Update this node first.");
            return 1;
        }
        if (!SyncProtocolVersion.IsCompatiblePeer(hostProtocol))
        {
            await output.WriteLineAsync(
                $"Error: cannot join: remote node protocol version ({hostProtocol}) is below {SyncProtocolVersion.MinPeer}; update it first.");
            return 1;
        }

        var slot = joinResponse.KeySlot;
        var encryptedMasterDek = Convert.FromBase64String(slot.EncryptedMasterDekB64);
        var iv = Convert.FromBase64String(slot.IvB64);
        var remoteSalt = Convert.FromBase64String(slot.SaltB64);

        // The peer chose these numbers; refuse hostile ones before committing memory to them.
        try
        {
            KeyDerivation.ValidateUntrustedParameters(slot.ArgonMemory, slot.ArgonIterations, slot.ArgonParallelism);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            await output.WriteLineAsync($"Error: the remote node sent an invalid key slot. {ex.Message}");
            return 1;
        }

        byte[] masterDek;
        try
        {
            var kek = KeyDerivation.DeriveKek(password, remoteSalt,
                slot.ArgonMemory, slot.ArgonIterations, slot.ArgonParallelism);
            masterDek = MasterKeyManager.UnwrapMasterDek(encryptedMasterDek, iv, kek);
        }
        catch (KdfBusyException)
        {
            await output.WriteLineAsync("Error: too many password checks in progress, try again in a moment");
            return 1;
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

        // The raw private key stays in memory until the snapshot is downloaded: the host's challenge is signed with it.
        // Cleared in the finally below.
        var (wrappedPk, pkIv) = NodeIdentityVault.EncryptPrivateKey(privateKey, masterDek, nodeId);

        var identity = new NodeIdentity
        {
            NodeId = nodeId,
            DisplayName = displayName,
            Ed25519PublicKey = publicKey,
            Ed25519PrivateKey = wrappedPk,
            Ed25519PrivateKeyIV = pkIv,
            Ed25519PrivateKeyV = 1,
            // Set once the snapshot is in (SnapshotJoin.CompleteAsync).
            InitialSyncCompleted = false,
            CreatedAt = now
        };

        // From the first row written here to the end of the snapshot step, any failure takes the node back to "not
        // joined" (SnapshotJoin.RollbackPartialNode), the way the phone does: a retry then starts clean instead of hitting
        // "already initialized", and nothing half-made is left that says it joined.
        var remote = joinResponse.RemoteNode;
        long cpSeq;
        long articleCount;
        var step = "writing this node's own records";
        try
        {
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

            var remoteEntry = new WhitelistEntry
            {
                NodeId = remote.NodeId,
                DisplayName = remote.DisplayName,
                Ed25519PublicKey = Convert.FromBase64String(remote.Ed25519PublicKeyB64),
                ApiAddress = remoteUrl.TrimEnd('/'),
                Status = "A",
                CreatedAt = now,
                UpdatedAt = now,
                // The host just proved it holds the master password by handing over a slot it opens,
                // and it records this node as a superadmin for the same reason (JoinAuthority, BMB-42).
                IsSuperadmin = JoinAuthority.ForPasswordPeer(remote.NodeId),
                // The key the code pinned is the key this node dials it by from now on (SpkiPinRegistry).
                TlsSpki = code?.SpkiPin
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
                            UpdatedAt = now,
                            // Without it every other superadmin of the mesh is a plain peer here, and
                            // their whitelist/hard-delete/restore events are refused on this node only.
                            IsSuperadmin = JoinAuthority.ForInheritedPeer(entry.NodeId, entry.IsSuperadmin)
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

            // The snapshot (BMB-81): the vault as the host has it, signed by the host. Without it this node would start its
            // sync from sequence 0, which a host that ever compacted its log (or was re-keyed) answers with 410 forever: an
            // empty node that said it had joined. Same client as the phone's join, over the same http client, so a join
            // code's pin covers the challenge, the authentication and the download too. Its download is bounded by its own
            // 30-minute budget (ResponseHeadersRead), not by the 30-second timeout above.
            step = "taking the snapshot";
            await output.WriteLineAsync("Downloading the snapshot from the remote node...");
            var snapshotClient = new SnapshotJoinClient(
                http,
                scope.ServiceProvider.GetRequiredService<DbConnectionFactory>(),
                DataDirOf(scope.ServiceProvider),
                loggerFactory.CreateLogger<SnapshotJoinClient>());
            (cpSeq, var lamportTs) = await snapshotClient.DownloadAndImportAsync(
                remoteUrl, nodeId, privateKey, Convert.FromBase64String(remote.Ed25519PublicKeyB64));

            await SnapshotJoin.CompleteAsync(
                remote.NodeId, cpSeq, lamportTs, syncPositionRepo, clock, nodeRepo, dbFactory,
                loggerFactory.CreateLogger("BeeMemoryBank.Cli.Join"));

            using var countConn = dbFactory.CreateConnection();
            using var count = countConn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM tbl_article WHERE status = 'A'";
            articleCount = Convert.ToInt64(count.ExecuteScalar());
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Error: the key exchange succeeded, but the join failed while {step}: {ex.Message}");
            try
            {
                SnapshotJoin.RollbackPartialNode(dbFactory);
                await output.WriteLineAsync("Nothing was kept: this node is not joined, and `bmb join` can be run again.");
            }
            catch (Exception rollbackEx)
            {
                await output.WriteLineAsync(
                    $"The half-made node could not be removed ({rollbackEx.Message}); delete the data folder before joining again.");
            }
            return 1;
        }
        finally
        {
            Array.Clear(privateKey);
            Array.Clear(masterDek);
        }

        await output.WriteLineAsync($"Node '{displayName}' successfully joined the network.");
        await output.WriteLineAsync($"Remote node: '{remote.DisplayName}' ({remote.NodeId})");
        if (joinResponse.Whitelist != null && joinResponse.Whitelist.Count > 0)
            await output.WriteLineAsync($"Inherited {joinResponse.Whitelist.Count} peer(s) from remote's whitelist.");
        await output.WriteLineAsync($"Imported {articleCount} note(s) from the snapshot (checkpoint {cpSeq}).");
        await output.WriteLineAsync("Start the services — synchronization continues from the snapshot automatically.");

        return 0;
    }

    /// <summary>The node's data folder as the provider resolved it (a re-key swap can redirect it): where media goes.</summary>
    private static string DataDirOf(IServiceProvider services) =>
        Path.GetDirectoryName(services.GetRequiredService<MediaStorageOptions>().MediaDir)!;

    /// <summary>
    /// Whether the node at <paramref name="remoteUrl"/> is a blind node: its sync challenge (mapped in that role) names
    /// it, and a blind node's id carries the mark (BlindNodeId). Any failure means "cannot tell", never "blind".
    /// </summary>
    private static async Task<bool> IsBlindNodeAsync(HttpClient http, string remoteUrl)
    {
        try
        {
            using var resp = await http.PostAsync($"{remoteUrl.TrimEnd('/')}/api/sync/challenge", null);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("serverNodeId", out var id)
                && id.TryGetGuid(out var serverNodeId)
                && BlindNodeId.IsBlind(serverNodeId);
        }
        catch
        {
            return false;
        }
    }

    private record JoinResponseDto(
        JoinRemoteNodeDto RemoteNode,
        JoinKeySlotDto KeySlot,
        List<JoinWhitelistEntryDto>? Whitelist);

    private record JoinWhitelistEntryDto(
        Guid NodeId,
        string DisplayName,
        string Ed25519PublicKeyB64,
        string? ApiAddress,
        bool IsSuperadmin = false);

    // ProtocolVersion: a host too old to send one is below every version this node can sync with (0).
    private record JoinRemoteNodeDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, int ProtocolVersion = 0);

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
