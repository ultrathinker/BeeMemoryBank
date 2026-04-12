using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Commands;

public static class JoinCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Joins a new node to an existing BeeMemoryBank network.
    /// Obtains Master DEK from the remote node, creates local identity and key slot.
    /// </summary>
    public static async Task<int> HandleAsync(
        string dataPath,
        string remoteUrl,
        string password,
        string displayName,
        TextWriter? output = null)
    {
        output ??= Console.Out;

        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();

        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var keySlotRepo = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
        var whitelistRepo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();

        // Check that the node is not already initialized
        if (await nodeRepo.GetAsync() != null)
        {
            await output.WriteLineAsync("Error: node is already initialized. Delete the DB to re-join.");
            return 1;
        }

        // 1. Generate Ed25519 key pair
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var nodeId = Guid.NewGuid();

        await output.WriteLineAsync($"Generated nodeId: {nodeId}");
        await output.WriteLineAsync($"Connecting to {remoteUrl}...");

        // 2. Call POST /api/join on the remote node
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

        // 3. Decrypt Master DEK from the received key slot
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

        // 4. Create a local password slot (new salt, same Master DEK)
        var localSalt = KeyDerivation.GenerateSalt();
        var localKek = KeyDerivation.DeriveKek(password, localSalt);
        var (localEncryptedDek, localIv) = MasterKeyManager.WrapMasterDek(masterDek, localKek);

        var now = DateTime.UtcNow;

        // Save identity
        var identity = new NodeIdentity
        {
            NodeId = nodeId,
            DisplayName = displayName,
            Ed25519PublicKey = publicKey,
            Ed25519PrivateKey = privateKey,
            CreatedAt = now
        };
        await nodeRepo.CreateAsync(identity);

        // Save key slot
        var keyStore = new MasterKeyStore
        {
            SlotType = "password",
            EncryptedMasterDek = localEncryptedDek,
            IV = localIv,
            Salt = localSalt,
            ArgonMemory = CryptoConstants.DefaultArgonMemory,
            ArgonIterations = CryptoConstants.DefaultArgonIterations,
            ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
            CreatedAt = now
        };
        await keySlotRepo.CreateAsync(keyStore);

        // Clear Master DEK from memory
        Array.Clear(masterDek);

        // 5. Add self to whitelist
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

        // 6. Add the remote node to the whitelist (with apiAddress for sync)
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

        await output.WriteLineAsync($"Node '{displayName}' successfully joined the network.");
        await output.WriteLineAsync($"Remote node: '{remote.DisplayName}' ({remote.NodeId})");
        await output.WriteLineAsync("Start the services — synchronization will begin automatically.");

        return 0;
    }

    // DTO for deserializing the response from /api/join
    private record JoinResponseDto(JoinRemoteNodeDto RemoteNode, JoinKeySlotDto KeySlot);

    private record JoinRemoteNodeDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64);

    private record JoinKeySlotDto(
        string EncryptedMasterDekB64,
        string IvB64,
        string SaltB64,
        int ArgonMemory,
        int ArgonIterations,
        int ArgonParallelism);
}
