using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A standalone restore gives the node a NEW identity (node id and Ed25519 key pair). The new private
/// key has to be stored the way a freshly initialized node stores it — wrapped under the master DEK of
/// the restored database, with the node id as AAD (v=1) — or the node comes back unable to sign: the
/// sync authentication handshake, pairing and every signed event would fail with a cryptographic
/// error that has nothing to do with the restore.
///
/// These tests run the real code path against real database files in a temp folder (create the
/// snapshot, restore it through <c>POST /api/snapshots/restore</c>, unlock again) and then use the
/// restored identity the way sync does.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class StandaloneRestoreIdentityTests : IAsyncLifetime
{
    private const string Password = "restoreIdentityPassword";

    private readonly BmbWebApplicationFactory _node = new();
    private readonly BmbWebApplicationFactory _verifier = new();
    private readonly List<IDisposable> _disposables = [];
    private HttpClient _client = null!;
    private HttpClient _verifierClient = null!;

    public async Task InitializeAsync()
    {
        _client = _node.CreateClient();
        await _node.InitializeNodeAsync(displayName: "RestoredNode", password: Password);
        await UnlockAsync(_client);

        // A second node with its own vault: it only checks signatures, as a sync peer does.
        _verifierClient = _verifier.CreateClient();
        await _verifier.InitializeNodeAsync(displayName: "Verifier", password: "verifierPassword");
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _verifierClient.Dispose();
        _node.Dispose();
        _verifier.Dispose();
        foreach (var d in _disposables) d.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RestoredNode_CanSignAChallenge()
    {
        var before = await IdentityAsync(_node);
        var fileName = await CreateSnapshotAsync(_client);

        await RestoreStandaloneAsync(_client, fileName);
        await UnlockAsync(_client);

        var after = await IdentityAsync(_node);
        after.NodeId.Should().NotBe(before.NodeId, "a standalone restore makes this a new node");
        after.Ed25519PublicKey.Should().NotEqual(before.Ed25519PublicKey);
        AssertCanSign(_node, after);
    }

    [Fact]
    public async Task RestoredNode_StoresItsKeyTheWayInitializationDoes()
    {
        var fileName = await CreateSnapshotAsync(_client);

        await RestoreStandaloneAsync(_client, fileName);
        await UnlockAsync(_client);

        var identity = await IdentityAsync(_node);
        identity.Ed25519PrivateKeyV.Should().Be(1, "the seed is wrapped under the master DEK, as InitializationService writes it");
        identity.Ed25519PrivateKeyIV.Should().NotBeNull().And.HaveCount(12);
        identity.Ed25519PrivateKey.Should().HaveCount(32 + 16, "a wrapped 32-byte seed is the seed plus the GCM tag, never the bare seed");

        var dek = _node.Services.GetRequiredService<SessionService>().GetMasterDek();
        var seed = NodeIdentityVault.GetDecryptedPrivateKey(
            identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV, identity.NodeId, dek);
        NodeIdentityCrypto.PublicKeyOf(seed).Should().Equal(identity.Ed25519PublicKey,
            "the stored private key must be the one the published public key belongs to");
    }

    [Fact]
    public async Task RestoredNode_CompletesTheSyncHandshakeWithAPeer()
    {
        var fileName = await CreateSnapshotAsync(_client);
        await RestoreStandaloneAsync(_client, fileName);
        await UnlockAsync(_client);

        var identity = await IdentityAsync(_node);
        var verifierId = await TrustAsync(_verifier, identity);

        using var scope = _node.Services.CreateScope();
        var token = await PeerAuthenticator.AuthenticateAsync(
            scope.ServiceProvider.GetRequiredService<INodeAuthSigner>(), _verifierClient, "", identity, verifierId);

        token.Should().NotBeNullOrEmpty("the peer verified the restored node's signature against its new public key");
    }

    [Fact]
    public async Task RestoreOnANodeThatWasLockedAndUnlocked_StillSigns()
    {
        _node.Services.GetRequiredService<SessionService>().Lock();
        await UnlockAsync(_client);
        var fileName = await CreateSnapshotAsync(_client);
        _node.Services.GetRequiredService<SessionService>().Lock();
        await UnlockAsync(_client);

        await RestoreStandaloneAsync(_client, fileName);
        _node.Services.GetRequiredService<SessionService>().IsUnlocked.Should().BeFalse("a restore locks the vault");
        await UnlockAsync(_client);

        AssertCanSign(_node, await IdentityAsync(_node));
    }

    [Theory]
    [InlineData(0)] // a node whose identity row is still the legacy plaintext seed
    [InlineData(1)] // a node as initialization and join write it today: the seed wrapped under the master DEK
    public async Task RestoringASnapshotOfAnotherNodeOfTheNetwork_StillSigns(int sourceKeyVersion)
    {
        // Node B joined this node's network (same master DEK, its own identity). Its backup is
        // restored here: the archive carries B's identity row, which this node must replace with a
        // key of its own, wrapped for the node id it now has.
        var peer = new BmbWebApplicationFactory();
        _disposables.Add(peer);
        await peer.JoinNodeAsync(_client, "NodeB", Password);
        var legacyPeerIdentity = await IdentityAsync(peer);
        legacyPeerIdentity.Ed25519PrivateKeyV.Should().Be(0, "JoinNodeAsync creates the legacy plaintext test fixture");
        var peerClient = peer.CreateClient();
        _disposables.Add(peerClient);
        await UnlockAsync(peerClient);
        await peer.Services.GetRequiredService<SessionService>().PostUnlockCatchUp;
        await SetIdentityKeyVersionAsync(peer, legacyPeerIdentity, sourceKeyVersion);
        var peerIdentity = await IdentityAsync(peer);
        peerIdentity.Ed25519PrivateKeyV.Should().Be(sourceKeyVersion);
        if (sourceKeyVersion == 0)
        {
            peerIdentity.Ed25519PrivateKey.Should().Equal(legacyPeerIdentity.Ed25519PrivateKey);
            peerIdentity.Ed25519PrivateKeyIV.Should().BeNull();
        }
        else
        {
            var dek = peer.Services.GetRequiredService<SessionService>().GetMasterDek();
            try
            {
                NodeIdentityVault.GetDecryptedPrivateKey(
                    peerIdentity.Ed25519PrivateKey, peerIdentity.Ed25519PrivateKeyIV,
                    peerIdentity.Ed25519PrivateKeyV, peerIdentity.NodeId, dek)
                    .Should().Equal(legacyPeerIdentity.Ed25519PrivateKey);
            }
            finally
            {
                Array.Clear(dek);
            }
        }
        var fileName = await CreateSnapshotAsync(peerClient);
        Directory.CreateDirectory(SnapshotsDir(_node));
        File.Copy(Path.Combine(SnapshotsDir(peer), fileName), Path.Combine(SnapshotsDir(_node), fileName), overwrite: true);

        await RestoreStandaloneAsync(_client, fileName);
        await UnlockAsync(_client);

        var identity = await IdentityAsync(_node);
        identity.NodeId.Should().NotBe(peerIdentity.NodeId).And.NotBe(Guid.Empty);
        identity.Ed25519PublicKey.Should().NotEqual(peerIdentity.Ed25519PublicKey);
        identity.Ed25519PrivateKeyV.Should().Be(1);
        AssertCanSign(_node, identity);
    }

    [Fact]
    public async Task ASecondRestoreOnTheSameNode_StillSigns_WithAnotherNewIdentity()
    {
        var fileName = await CreateSnapshotAsync(_client);

        await RestoreStandaloneAsync(_client, fileName);
        await UnlockAsync(_client);
        var first = await IdentityAsync(_node);
        AssertCanSign(_node, first);

        // The snapshot taken from the node as it is now, restored again.
        var second = await CreateSnapshotAsync(_client);
        await RestoreStandaloneAsync(_client, second);
        await UnlockAsync(_client);

        var identity = await IdentityAsync(_node);
        identity.NodeId.Should().NotBe(first.NodeId);
        AssertCanSign(_node, identity);

        // And the very first archive again: it still carries the original node's wrapped key, bound to
        // the original node id.
        await RestoreStandaloneAsync(_client, fileName);
        await UnlockAsync(_client);
        AssertCanSign(_node, await IdentityAsync(_node));
    }

    [Fact]
    public async Task APlainSnapshotOfAnotherVault_OpenedByTheSamePassword_HasItsKeySealedUnderThatVaultsKey()
    {
        // Not encrypted, so nothing ties it to this session's master DEK: its database is sealed under
        // a DEK of its own, which only its key slots (and the password) can produce. The node comes
        // back unlockable with that DEK, so that is the key the new identity has to be wrapped under.
        var other = new BmbWebApplicationFactory();
        _disposables.Add(other);
        await other.InitializeNodeAsync(displayName: "OtherVault", password: Password);
        var fileName = await PlainSnapshotAsync(other);
        var myDek = _node.Services.GetRequiredService<SessionService>().GetMasterDek();

        await RestoreStandaloneAsync(_client, fileName);
        await UnlockAsync(_client);

        _node.Services.GetRequiredService<SessionService>().GetMasterDek().Should().NotEqual(myDek,
            "the restored database carries the other vault's key slots");
        AssertCanSign(_node, await IdentityAsync(_node));
    }

    [Fact]
    public async Task ASnapshotNoKnownPasswordOpens_IsRefused_AndLeavesTheNodeAsItWas()
    {
        var other = new BmbWebApplicationFactory();
        _disposables.Add(other);
        await other.InitializeNodeAsync(displayName: "OtherVault", password: "aDifferentVaultPassword");
        var fileName = await PlainSnapshotAsync(other);
        var before = await IdentityAsync(_node);

        var resp = await _client.PostAsJsonAsync("/api/snapshots/restore", new
        {
            fileName,
            masterPassword = Password,
            createBackupFirst = false,
            standaloneMode = true
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("master password does not open its key slots");
        File.Exists(Path.Combine(_node.DataPath, "beememorybank.db.standalone-staging")).Should().BeFalse(
            "a half-prepared copy of the other database must not stay next to the live one");
        await UnlockAsync(_client);
        var after = await IdentityAsync(_node);
        after.NodeId.Should().Be(before.NodeId);
        after.Ed25519PrivateKey.Should().Equal(before.Ed25519PrivateKey);
        AssertCanSign(_node, after);
    }

    [Fact]
    public async Task AnEncryptedSnapshotOfADifferentVault_IsRefusedWithoutChangingThisNode()
    {
        const string otherPassword = "aDifferentVaultPassword";
        var other = new BmbWebApplicationFactory();
        _disposables.Add(other);
        await other.InitializeNodeAsync(displayName: "OtherVault", password: otherPassword);
        var otherClient = other.CreateClient();
        _disposables.Add(otherClient);
        var unlockOther = await otherClient.PostAsJsonAsync("/api/session/unlock", new { password = otherPassword });
        unlockOther.StatusCode.Should().Be(HttpStatusCode.OK, await unlockOther.Content.ReadAsStringAsync());
        var fileName = await CreateSnapshotAsync(otherClient);
        Directory.CreateDirectory(SnapshotsDir(_node));
        File.Copy(Path.Combine(SnapshotsDir(other), fileName), Path.Combine(SnapshotsDir(_node), fileName), overwrite: true);

        var mediaPath = Path.Combine(_node.DataPath, "media", "local-before-refusal.enc");
        Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
        var localMedia = RandomNumberGenerator.GetBytes(32);
        await File.WriteAllBytesAsync(mediaPath, localMedia);
        var before = await IdentityAsync(_node);

        var response = await _client.PostAsJsonAsync("/api/snapshots/restore", new
        {
            fileName,
            masterPassword = Password,
            createBackupFirst = false,
            standaloneMode = true
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(
            "This snapshot was made by a different vault (another master key) and cannot be opened here. Nothing was changed.");
        File.Exists(Path.Combine(_node.DataPath, "beememorybank.db.standalone-staging")).Should().BeFalse();
        (await File.ReadAllBytesAsync(mediaPath)).Should().Equal(localMedia, "a refusal must not alter this node's media");

        await UnlockAsync(_client);
        var after = await IdentityAsync(_node);
        after.NodeId.Should().Be(before.NodeId);
        after.Ed25519PrivateKey.Should().Equal(before.Ed25519PrivateKey);
        AssertCanSign(_node, after);

        using var scope = _node.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>().CreateConnection();
        var auditActions = await conn.QueryAsync<string>("SELECT action FROM tbl_audit_log ORDER BY id");
        auditActions.Should().Contain("snapshot_restore_refused");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores the exact legacy or v1 identity shape required by a snapshot test after the unlock catch-up has migrated
    /// the joined test node. The saved seed is always the plaintext value <see cref="BmbWebApplicationFactory.JoinNodeAsync"/>
    /// wrote before unlock.
    /// </summary>
    private static async Task SetIdentityKeyVersionAsync(BmbWebApplicationFactory node, NodeIdentity legacyIdentity, int version)
    {
        legacyIdentity.Ed25519PrivateKeyV.Should().Be(0);
        byte[] privateKey;
        byte[]? iv;
        if (version == 0)
        {
            privateKey = (byte[])legacyIdentity.Ed25519PrivateKey.Clone();
            iv = null;
        }
        else
        {
            var dek = node.Services.GetRequiredService<SessionService>().GetMasterDek();
            try
            {
                (privateKey, iv) = NodeIdentityVault.EncryptPrivateKey(
                    legacyIdentity.Ed25519PrivateKey, dek, legacyIdentity.NodeId);
            }
            finally
            {
                Array.Clear(dek);
            }
        }

        using var conn = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_node_identity SET ed25519_private_key = @wrapped, ed25519_private_key_iv = @iv, ed25519_private_key_v = @version WHERE node_id = @nodeId",
            new { wrapped = privateKey, iv, version, nodeId = legacyIdentity.NodeId });
    }

    /// <summary>A complete (secrets kept) snapshot of <paramref name="node"/> that is not encrypted, copied into this node's snapshots folder.</summary>
    private async Task<string> PlainSnapshotAsync(BmbWebApplicationFactory node)
    {
        var info = await node.Services.GetRequiredService<SnapshotService>()
            .CreateAsync(filterSecrets: false, sign: false, encryptDb: false);
        Directory.CreateDirectory(SnapshotsDir(_node));
        File.Copy(Path.Combine(SnapshotsDir(node), info.FileName), Path.Combine(SnapshotsDir(_node), info.FileName), overwrite: true);
        return info.FileName;
    }

    private static string SnapshotsDir(BmbWebApplicationFactory node) =>
        node.Services.GetRequiredService<SnapshotService>().SnapshotsDir;

    private static async Task UnlockAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
    }

    private static async Task<string> CreateSnapshotAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/api/snapshots", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<SnapshotCreatedDto>())!.FileName;
    }

    private static async Task RestoreStandaloneAsync(HttpClient client, string fileName)
    {
        var resp = await client.PostAsJsonAsync("/api/snapshots/restore", new
        {
            fileName,
            masterPassword = Password,
            createBackupFirst = false,
            standaloneMode = true
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
    }

    private static async Task<NodeIdentity> IdentityAsync(BmbWebApplicationFactory node)
    {
        using var scope = node.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
    }

    /// <summary>Signs as sync does (<see cref="INodeAuthSigner"/>) and checks the signature against the published public key.</summary>
    private static void AssertCanSign(BmbWebApplicationFactory node, NodeIdentity identity)
    {
        using var scope = node.Services.CreateScope();
        var signer = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>();
        var payload = RandomNumberGenerator.GetBytes(48);

        var signature = signer.SignChallenge(identity, payload);

        Ed25519Signer.Verify(identity.Ed25519PublicKey, payload, signature).Should().BeTrue();
    }

    /// <summary>Puts <paramref name="who"/> in the peer's whitelist, as pairing would, and returns the peer's own node id.</summary>
    private static async Task<Guid> TrustAsync(BmbWebApplicationFactory peer, NodeIdentity who)
    {
        using var scope = peer.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = who.NodeId,
            DisplayName = who.DisplayName,
            Ed25519PublicKey = who.Ed25519PublicKey,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        return (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
    }

    private sealed record SnapshotCreatedDto(string FileName);
}
