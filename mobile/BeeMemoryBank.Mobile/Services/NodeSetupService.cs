using System.Data;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Mobile.Services;

public class NodeSetupService
{
    private readonly InitializationService _initSvc;
    private readonly IWhitelistRepository _whitelistRepo;
    private readonly INodeIdentityRepository _nodeRepo;
    private readonly IKeySlotRepository _keySlotRepo;
    private readonly IUserRepository _userRepo;
    private readonly IDbConnectionFactory _dbFactory;
    private readonly ISyncPositionRepository _syncPositionRepo;
    private readonly ILamportClock _clock;
    private readonly Func<HttpClient, SnapshotJoinClient> _snapshotJoinClientOver;
    private readonly ILogger<NodeSetupService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public NodeSetupService(
        InitializationService initSvc,
        IWhitelistRepository whitelistRepo,
        INodeIdentityRepository nodeRepo,
        IKeySlotRepository keySlotRepo,
        IUserRepository userRepo,
        IDbConnectionFactory dbFactory,
        ISyncPositionRepository syncPositionRepo,
        ILamportClock clock,
        Func<HttpClient, SnapshotJoinClient> snapshotJoinClientOver,
        ILogger<NodeSetupService> logger)
    {
        _initSvc = initSvc;
        _whitelistRepo = whitelistRepo;
        _nodeRepo = nodeRepo;
        _keySlotRepo = keySlotRepo;
        _userRepo = userRepo;
        _dbFactory = dbFactory;
        _syncPositionRepo = syncPositionRepo;
        _clock = clock;
        _snapshotJoinClientOver = snapshotJoinClientOver;
        _logger = logger;
    }

    public async Task<NodeIdentity> InitAsync(string name, string password)
    {
        await _initSvc.InitializeAsync(name, name, password, canGenerateEmbeddings: false);

        var identity = await _nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node identity not found after init.");

        return identity;
    }

    /// <summary>
    /// Joins the network through <paramref name="remoteUrl"/>. With a <paramref name="code"/> from a
    /// computer's Connect page, every request of the join — the master password first of all — goes
    /// only to a server holding the key the code pins, and <c>/api/join</c> carries the code's token.
    /// </summary>
    public async Task<NodeIdentity> JoinAsync(string name, string remoteUrl, string password, JoinCode? code = null)
    {
        if (await _initSvc.IsInitializedAsync())
            throw new InvalidOperationException("Node already initialized. Delete the database to re-join.");

        // Never the app's default client: it follows redirects, and a 307 would resend the password.
        using var http = JoinHttp.CreateClient(code?.SpkiPin);
        var snapshotJoinClient = _snapshotJoinClientOver(http);

        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var nodeId = Guid.NewGuid();

        var joinRequest = new
        {
            masterPassword = password,
            nodeId = nodeId,
            displayName = name,
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        };

        // The host writes this phone's row as soon as it accepts the join. From there on a failure leaves that row behind unless the host
        // is told (POST /api/join/abort). Best effort: a host or proxy that does not know the route, or does not answer, keeps the
        // sentence that names the row, and nothing here replaces the error that made the join fail. The sentence is returned to be
        // added to that error; rowCertain is false when the join never got an answer, so the host may or may not have written the row.
        var hostTold = false;
        async Task<string> AbortNoteAsync(bool rowCertain = true)
        {
            hostTold = true;
            var outcome = await JoinAbortClient.TryAbortAsync(http, remoteUrl, password, nodeId, publicKey, code?.Token);
            if (outcome.HostForgot())
                return $"The other computer was told and no longer lists this phone ('{name}').";
            return rowCertain
                ? $"The other computer still lists this phone ('{name}') as a member that has never synced: revoke it there " +
                  "(Admin page, Nodes), or it holds back compaction of that computer's event log."
                : $"If the other computer recorded this phone ('{name}') before the connection failed, it lists it as a member that has never synced: " +
                  "revoke it there (Admin page, Nodes), or it holds back compaction of that computer's event log.";
        }

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{remoteUrl.TrimEnd('/')}/api/join")
            {
                Content = JsonContent.Create(joinRequest, options: _jsonOptions)
            };
            if (code?.Token != null) request.Headers.Add(JoinCode.TokenHeader, code.Token);
            // Bounded here, not by the client (JoinHttp): the snapshot request of this join waits for the host to build the snapshot.
            response = await JoinHttp.BoundAsync(t => http.SendAsync(request, t));
        }
        catch (HttpRequestException ex) when (code != null && ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            throw new InvalidOperationException(
                "The computer at this address is not the one the join code belongs to (its key does not match), " +
                "so the password was not sent. Open Connect a device on the computer again and use the new code.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cannot reach remote node: {ex.Message}"
                + (ex is HttpRequestException unanswered && JoinAbortClient.MayHaveReachedHost(unanswered)
                    ? " " + await AbortNoteAsync(rowCertain: false) : ""));
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            // A server-side failure (a proxy that gave up while the host went on) may have come after the host wrote the row.
            var rejectedNote = (int)response.StatusCode >= 500 ? " " + await AbortNoteAsync(rowCertain: false) : "";
            throw new InvalidOperationException($"Join rejected ({(int)response.StatusCode}): {errorBody}{rejectedNote}");
        }

        // From the host's answer on, the host holds this phone's row, so any failure before the snapshot step (an answer that cannot be
        // read or lacks a part, a field that is not base64, a local write that throws) must take that row back as well, and the
        // phone back to "uninitialized". The checks inside that already told the host throw their own sentence, which stays.
        JoinResponseDto joinResponse;
        JoinRemoteNodeDto remote;
        NodeIdentity identity;
        byte[] masterDek = [];
        try
        {
            var answer = await response.Content.ReadFromJsonAsync<JoinResponseDto>(_jsonOptions)
                ?? throw new InvalidOperationException("Empty response from remote node. " + await AbortNoteAsync());
            if (answer.RemoteNode == null || answer.KeySlot == null)
                throw new InvalidOperationException("The answer of the remote node is incomplete.");
            joinResponse = answer;

            var slot = joinResponse.KeySlot;
            var encryptedMasterDek = Convert.FromBase64String(slot.EncryptedMasterDekB64);
            var remoteIv = Convert.FromBase64String(slot.IvB64);
            var remoteSalt = Convert.FromBase64String(slot.SaltB64);

            // The peer chose these numbers; refuse hostile ones before the phone commits memory to them.
            try
            {
                KeyDerivation.ValidateUntrustedParameters(slot.ArgonMemory, slot.ArgonIterations, slot.ArgonParallelism);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                throw new InvalidOperationException($"The remote node sent an invalid key slot. {ex.Message} " + await AbortNoteAsync());
            }

            try
            {
                var remoteKek = KeyDerivation.DeriveKek(password, remoteSalt,
                    slot.ArgonMemory, slot.ArgonIterations, slot.ArgonParallelism);
                masterDek = MasterKeyManager.UnwrapMasterDek(encryptedMasterDek, remoteIv, remoteKek);
            }
            catch (KdfBusyException)
            {
                throw new InvalidOperationException("Too many password checks in progress, try again in a moment. " + await AbortNoteAsync());
            }
            catch
            {
                throw new InvalidOperationException("Could not decrypt Master DEK — wrong password? " + await AbortNoteAsync());
            }

            var localSalt = KeyDerivation.GenerateSalt();
            var localKek = KeyDerivation.DeriveKek(password, localSalt);
            var (localEncryptedDek, localIv) = MasterKeyManager.WrapMasterDek(masterDek, localKek);

            var now = DateTime.UtcNow;

            // Wrap the Ed25519 private key with the master DEK (v=1) right away
            // instead of storing it raw. Without this, an attacker with file-system
            // access (rooted device, lost-phone scenario) reads the seed straight
            // out of beememorybank.db and can sign arbitrary sync events as this
            // node — effectively destroying the user's network state.
            var (wrappedPrivKey, privKeyIv) = NodeIdentityVault.EncryptPrivateKey(privateKey, masterDek, nodeId);
            // Do NOT zero `privateKey` here — the snapshot-join handshake below still
            // signs the /api/sync/challenge response with it. Cleared in the finally
            // after the handshake. Forgetting this made every join fail with HTTP 401.

            identity = new NodeIdentity
            {
                NodeId = nodeId,
                DisplayName = name,
                Ed25519PublicKey = publicKey,
                Ed25519PrivateKey = wrappedPrivKey,
                Ed25519PrivateKeyIV = privKeyIv,
                Ed25519PrivateKeyV = 1,
                InitialSyncCompleted = false,
                CreatedAt = now
            };
            await _nodeRepo.CreateAsync(identity);

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
            var localSlotId = await _keySlotRepo.CreateAsync(localSlot);

            var user = new User
            {
                Username = name,
                DisplayName = name,
                PasswordHash = UserService.HashPassword(password),
                Role = UserRoles.Superadmin,
                KeySlotId = localSlotId,
                IsActive = true,
                CreatedAt = now
            };
            await _userRepo.CreateAsync(user);

            var sentinel = MasterKeyManager.ComputeSentinel(masterDek);
            await _nodeRepo.StoreSentinelAsync(sentinel);

            WriteMigrationMarker();

            Array.Clear(masterDek);

            foreach (var entry in joinResponse.Whitelist ?? [])
            {
                if (entry.NodeId == nodeId) continue;
                if (entry.NodeId == joinResponse.RemoteNode.NodeId) continue;

                try
                {
                    var existing = await _whitelistRepo.GetByNodeIdAsync(entry.NodeId);
                    if (existing != null) continue;

                    // The key the host dials this peer by: without it the peer is checked through the public CAs, which a
                    // self-signed one never passes (SpkiPinRegistry). A pin this build cannot use leaves the peer out.
                    if (!JoinTls.TryInherit(entry.TlsTrust, entry.TlsSpki, out var tlsTrust, out var tlsSpki))
                    {
                        _logger.LogWarning("Not importing peer {NodeId}: the host sent a TLS pin that is not a pin.", entry.NodeId);
                        continue;
                    }

                    await _whitelistRepo.CreateAsync(new WhitelistEntry
                    {
                        NodeId = entry.NodeId,
                        DisplayName = entry.DisplayName,
                        Ed25519PublicKey = Convert.FromBase64String(entry.Ed25519PublicKeyB64),
                        ApiAddress = entry.ApiAddress,
                        Status = "A",
                        CreatedAt = now,
                        UpdatedAt = now,
                        // Propagate IsSuperadmin from the bootstrap node's whitelist so this
                        // new node knows which transitively-discovered peers are Superadmins.
                        // Without this, every other Superadmin in the cluster gets demoted to
                        // plain peer locally → their whitelist_*/hard_delete/restore_network
                        // events get rejected once a 3rd node joins.
                        IsSuperadmin = JoinAuthority.ForInheritedPeer(entry.NodeId, entry.IsSuperadmin),
                        TlsTrust = tlsTrust,
                        TlsSpki = tlsSpki
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to import whitelist entry for node {NodeId}", entry.NodeId);
                }
            }

            remote = joinResponse.RemoteNode;
            await _whitelistRepo.CreateAsync(new WhitelistEntry
            {
                NodeId = remote.NodeId,
                DisplayName = remote.DisplayName,
                Ed25519PublicKey = Convert.FromBase64String(remote.Ed25519PublicKeyB64),
                ApiAddress = remoteUrl.TrimEnd('/'),
                Status = "A",
                CreatedAt = now,
                UpdatedAt = now,
                // The host just proved it holds the master password by handing over a slot it opens,
                // and it records this phone as a superadmin for the same reason (JoinAuthority, BMB-42).
                IsSuperadmin = JoinAuthority.ForPasswordPeer(remote.NodeId),
                // The key the code pinned is the key this phone dials the node by from now on (SpkiPinRegistry), as the desktop
                // setup and `bmb join --code` record it: the node's certificate is self-signed or from its own local CA, which no
                // ordinary check on the phone trusts, so without the pin every sync after the join would fail.
                TlsSpki = code?.SpkiPin
            });
        }
        catch (Exception ex) when (!hostTold)
        {
            Array.Clear(privateKey);
            Array.Clear(masterDek);
            _logger.LogError(ex, "The answer of the host could not be used — rolling back the partial node so a retry can start clean.");
            try { RollbackPartialNode(); }
            catch (Exception rbEx) { _logger.LogError(rbEx, "Rollback of partial node failed; a manual reset may be required."); }
            throw new InvalidOperationException($"The join could not be completed: {ex.Message}. " + await AbortNoteAsync(), ex);
        }


        _logger.LogInformation("Starting snapshot import from {Url}", remoteUrl);
        try
        {
            var (cpSeq, lamportTs) = await snapshotJoinClient.DownloadAndImportAsync(
                remoteUrl,
                nodeId,
                privateKey,
                Convert.FromBase64String(remote.Ed25519PublicKeyB64));

            // The same tail as bmb join and the Setup page: the pull position at the checkpoint, the clock past the imported rows
            // (durably: LamportFloor, or a restarted phone begins at 0 and loses every edit it makes), the initial sync done.
            var capped = await SnapshotJoin.CompleteAsync(
                remote.NodeId, cpSeq, lamportTs, _syncPositionRepo, _clock, _nodeRepo, _dbFactory, _logger);

            _logger.LogInformation("Snapshot import done. CP={Cp}, Lamport={Lamport}", cpSeq, capped);
        }
        catch (Exception ex)
        {
            // Roll the node back to "uninitialized" so a retry starts clean instead of hitting
            // "Node already initialized". Safe to do here: the snapshot table import is wrapped in
            // a transaction that already rolled back on failure, so NO content rows were committed —
            // only the identity/slot/user/whitelist/sync-position rows created above persist.
            _logger.LogError(ex, "Snapshot import failed — rolling back the partial node so a retry can start clean.");
            try { RollbackPartialNode(); }
            catch (Exception rbEx) { _logger.LogError(rbEx, "Rollback of partial node failed; a manual reset may be required."); }
            // The host recorded this phone when the key exchange succeeded: ask it to take the row back.
            throw new InvalidOperationException(
                $"Key exchange succeeded but snapshot import failed: {ex.Message}. Please try again. " +
                await AbortNoteAsync(), ex);
        }
        finally
        {
            Array.Clear(privateKey);
        }

        return identity;
    }

    // Deletes the rows a failed join leaves behind, returning the node to "uninitialized".
    // FK order matters (tbl_user -> tbl_key_slot). Content tables are intentionally untouched:
    // the snapshot import is transactional, so on failure nothing was committed there.
    private void RollbackPartialNode()
    {
        using var conn = _dbFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // All-or-nothing: a crash mid-rollback must not leave a half-deleted "Frankenstein" node.
        cmd.CommandText = @"
            DELETE FROM tbl_sync_position;
            DELETE FROM tbl_whitelist;
            DELETE FROM tbl_user;
            DELETE FROM tbl_key_slot;
            DELETE FROM tbl_node_identity;";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private void WriteMigrationMarker()
    {
        using var conn = _dbFactory.CreateConnection();
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

    private sealed record JoinResponseDto(JoinRemoteNodeDto RemoteNode, JoinKeySlotDto KeySlot, List<JoinWhitelistEntryDto>? Whitelist);
    private sealed record JoinRemoteNodeDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64);
    private sealed record JoinWhitelistEntryDto(
        Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, string? ApiAddress, bool IsSuperadmin = false,
        string? TlsTrust = null, string? TlsSpki = null);
    private sealed record JoinKeySlotDto(
        string EncryptedMasterDekB64,
        string IvB64,
        string SaltB64,
        int ArgonMemory,
        int ArgonIterations,
        int ArgonParallelism);
}
