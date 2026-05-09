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
    private readonly SnapshotJoinClient _snapshotJoinClient;
    private readonly HttpClient _http;
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
        SnapshotJoinClient snapshotJoinClient,
        HttpClient http,
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
        _snapshotJoinClient = snapshotJoinClient;
        _http = http;
        _logger = logger;
    }

    public async Task<NodeIdentity> InitAsync(string name, string password)
    {
        await _initSvc.InitializeAsync(name, name, password, canGenerateEmbeddings: false);

        var identity = await _nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node identity not found after init.");

        return identity;
    }

    public async Task<NodeIdentity> JoinAsync(string name, string remoteUrl, string password)
    {
        if (await _initSvc.IsInitializedAsync())
            throw new InvalidOperationException("Node already initialized. Delete the database to re-join.");

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

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(
                $"{remoteUrl.TrimEnd('/')}/api/join", joinRequest, _jsonOptions);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cannot reach remote node: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Join rejected ({(int)response.StatusCode}): {errorBody}");
        }

        var joinResponse = await response.Content.ReadFromJsonAsync<JoinResponseDto>(_jsonOptions)
            ?? throw new InvalidOperationException("Empty response from remote node");

        var slot = joinResponse.KeySlot;
        var encryptedMasterDek = Convert.FromBase64String(slot.EncryptedMasterDekB64);
        var remoteIv = Convert.FromBase64String(slot.IvB64);
        var remoteSalt = Convert.FromBase64String(slot.SaltB64);

        byte[] masterDek;
        try
        {
            var remoteKek = KeyDerivation.DeriveKek(password, remoteSalt,
                slot.ArgonMemory, slot.ArgonIterations, slot.ArgonParallelism);
            masterDek = MasterKeyManager.UnwrapMasterDek(encryptedMasterDek, remoteIv, remoteKek);
        }
        catch
        {
            throw new InvalidOperationException("Could not decrypt Master DEK — wrong password?");
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
        var (wrappedPrivKey, privKeyIv) = NodeIdentityCrypto.EncryptPrivateKey(privateKey, masterDek, nodeId);
        Array.Clear(privateKey);

        var identity = new NodeIdentity
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

                await _whitelistRepo.CreateAsync(new WhitelistEntry
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
            catch { }
        }

        var remote = joinResponse.RemoteNode;
        await _whitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = remote.NodeId,
            DisplayName = remote.DisplayName,
            Ed25519PublicKey = Convert.FromBase64String(remote.Ed25519PublicKeyB64),
            ApiAddress = remoteUrl.TrimEnd('/'),
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        });

        _logger.LogInformation("Starting snapshot import from {Url}", remoteUrl);
        try
        {
            var (cpSeq, lamportTs) = await _snapshotJoinClient.DownloadAndImportAsync(
                remoteUrl,
                nodeId,
                privateKey,
                Convert.FromBase64String(remote.Ed25519PublicKeyB64));

            await _syncPositionRepo.UpsertAsync(new SyncPosition
            {
                RemoteNodeId = remote.NodeId,
                LastSequenceNum = cpSeq,
                UpdatedAt = DateTime.UtcNow
            });

            const long MAX_CLOCK_ADVANCE = 1_000_000;
            var capped = Math.Min(lamportTs, _clock.Current + MAX_CLOCK_ADVANCE);
            if (lamportTs > capped)
                _logger.LogWarning("Producer Lamport {L} exceeds local+MAX_CLOCK_ADVANCE, capping at {Cap}", lamportTs, capped);
            _clock.Update(capped);

            await _nodeRepo.MarkInitialSyncCompletedAsync();

            _logger.LogInformation("Snapshot import done. CP={Cp}, Lamport={Lamport}", cpSeq, capped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot import failed. Node is in partial state — user may need to wipe and retry.");
            throw new InvalidOperationException(
                $"Key exchange succeeded but snapshot import failed: {ex.Message}. Delete local data and try again.", ex);
        }

        return identity;
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
    private sealed record JoinWhitelistEntryDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, string? ApiAddress);
    private sealed record JoinKeySlotDto(
        string EncryptedMasterDekB64,
        string IvB64,
        string SaltB64,
        int ArgonMemory,
        int ArgonIterations,
        int ArgonParallelism);
}
