using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Mobile.Services;

public class NodeSetupService
{
    private readonly InitializationService _initSvc;
    private readonly IWhitelistRepository _whitelistRepo;
    private readonly INodeIdentityRepository _nodeRepo;
    private readonly IKeySlotRepository _keySlotRepo;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public NodeSetupService(
        InitializationService initSvc,
        IWhitelistRepository whitelistRepo,
        INodeIdentityRepository nodeRepo,
        IKeySlotRepository keySlotRepo,
        HttpClient http)
    {
        _initSvc = initSvc;
        _whitelistRepo = whitelistRepo;
        _nodeRepo = nodeRepo;
        _keySlotRepo = keySlotRepo;
        _http = http;
    }

    public async Task<NodeIdentity> InitAsync(string name, string password)
    {
        await _initSvc.InitializeAsync(name, password, canGenerateEmbeddings: false);

        var identity = await _nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node identity not found after init.");

        return identity;
    }

    /// <summary>
    /// Joins this node to an existing BeeMemoryBank network.
    /// Receives the Master DEK from the remote node, saves a local key slot.
    /// </summary>
    public async Task<NodeIdentity> JoinAsync(string name, string remoteUrl, string password)
    {
        if (await _initSvc.IsInitializedAsync())
            throw new InvalidOperationException("Node already initialized. Delete the database to re-join.");

        // 1. Generate Ed25519 key pair
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var nodeId = Guid.NewGuid();

        // 2. POST /api/join to remote node
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

        // 3. Decrypt Master DEK from remote key slot
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

        // 4. Create local password slot (new salt, same Master DEK)
        var localSalt = KeyDerivation.GenerateSalt();
        var localKek = KeyDerivation.DeriveKek(password, localSalt);
        var (localEncryptedDek, localIv) = MasterKeyManager.WrapMasterDek(masterDek, localKek);

        var now = DateTime.UtcNow;

        var identity = new NodeIdentity
        {
            NodeId = nodeId,
            DisplayName = name,
            Ed25519PublicKey = publicKey,
            Ed25519PrivateKey = privateKey,
            CreatedAt = now
        };
        await _nodeRepo.CreateAsync(identity);

        await _keySlotRepo.CreateAsync(new MasterKeyStore
        {
            SlotType = "password",
            EncryptedMasterDek = localEncryptedDek,
            IV = localIv,
            Salt = localSalt,
            ArgonMemory = CryptoConstants.DefaultArgonMemory,
            ArgonIterations = CryptoConstants.DefaultArgonIterations,
            ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
            CreatedAt = now
        });

        // 4b. Store sentinel for cross-node DEK compatibility verification (must be before Array.Clear!)
        var sentinel = MasterKeyManager.ComputeSentinel(masterDek);
        await _nodeRepo.StoreSentinelAsync(sentinel);
        Array.Clear(masterDek);

        // 5. Add self to whitelist
        await _whitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId,
            DisplayName = name,
            Ed25519PublicKey = publicKey,
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        });

        // 6. Import full whitelist from server (bootstrap: learn all nodes in the network)
        foreach (var entry in joinResponse.Whitelist ?? [])
        {
            // Skip self and the remote node (already added above)
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
            catch { /* skip invalid entries */ }
        }

        // Add the direct remote node (with correct ApiAddress from user input)
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

        return identity;
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
