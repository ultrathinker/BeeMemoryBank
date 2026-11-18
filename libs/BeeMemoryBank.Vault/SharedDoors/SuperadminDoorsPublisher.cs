using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.SharedDoors;

/// <summary>
/// Crypto and canonical-digest helpers shared by <see cref="SuperadminDoorsPublisher"/> and <see cref="SharedDoorsApplier"/>.
/// </summary>
public static class SuperadminDoorsCrypto
{
    public static byte[] BuildListAad(long version, Guid authorNodeId) =>
        Encoding.UTF8.GetBytes($"bmb-superadmin-doors-v1:{version.ToString(CultureInfo.InvariantCulture)}:{authorNodeId:D}");

    public static SuperadminDoorsDocument? TryDecryptForNode(
        SuperadminDoorsSetPayload payload,
        Guid eventId,
        Guid authorNodeId,
        Guid myNodeId,
        byte[] myEd25519Seed)
    {
        var nodeKey = myNodeId.ToString("D").ToUpperInvariant();
        if (payload.ListEnvelopes?.Peers is null
            || !payload.ListEnvelopes.Peers.TryGetValue(nodeKey, out var box))
            return null;

        var listKey = DekEnvelope.Open(
            payload.ListEnvelopes.EphemeralPub,
            box.Wrapped,
            box.Nonce,
            eventId,
            myNodeId,
            myEd25519Seed);
        try
        {
            var aad = BuildListAad(payload.Version, authorNodeId);
            var plaintext = AesGcmHelper.Decrypt(
                listKey,
                Convert.FromBase64String(payload.CiphertextB64),
                Convert.FromBase64String(payload.IvB64),
                aad);
            try
            {
                return JsonSerializer.Deserialize<SuperadminDoorsDocument>(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(listKey);
        }
    }
}

/// <summary>
/// Publishes <c>superadmin_doors_set</c> events on the accounts node whenever the set of active superadmins
/// with v2 doors, their doors or password hints, or the set of active non-blind shared full peers changes.
/// </summary>
public sealed class SuperadminDoorsPublisher(
    IServiceScopeFactory scopeFactory,
    ILogger<SuperadminDoorsPublisher> logger) : ISuperadminDoorsNotifier, IDisposable
{
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly object _timerLock = new();
    private CancellationTokenSource? _pendingCts;
    private bool _disposed;

    /// <summary>Delay used for background notifications before coalescing into a single publish.</summary>
    public TimeSpan DebounceDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public Task NotifyChangedAsync(bool immediate = false, CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        if (immediate)
        {
            CancelPendingTimer();
            return PublishIfDueAsync(ct);
        }

        if (DebounceDelay <= TimeSpan.Zero)
            return PublishIfDueAsync(ct);

        CancellationTokenSource nextCts;
        lock (_timerLock)
        {
            if (_disposed) return Task.CompletedTask;
            _pendingCts?.Cancel();
            _pendingCts?.Dispose();
            _pendingCts = new CancellationTokenSource();
            nextCts = _pendingCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelay, nextCts.Token).ConfigureAwait(false);
                if (!nextCts.IsCancellationRequested && !_disposed)
                    await PublishIfDueAsync(nextCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Debounced superadmin_doors_set publish failed.");
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task<bool> PublishIfDueAsync(CancellationToken ct = default) => await PublishCoreAsync(null, force: false, ct).ConfigureAwait(false) > 0;

    /// <summary>
    /// Step S2 of a removal (release 2.12.0): publishes the list NOW, without <paramref name="removedPersonIds"/> (and without anybody a removal holds),
    /// even when the last published list looks the same, and returns its version; 0 when nothing could be published (not the accounts node, locked,
    /// no shared node, a shared node that cannot take the list).
    /// </summary>
    public Task<long> PublishForRemovalAsync(IReadOnlyCollection<Guid> removedPersonIds, CancellationToken ct = default) =>
        PublishCoreAsync(removedPersonIds, force: true, ct);

    private async Task<long> PublishCoreAsync(IReadOnlyCollection<Guid>? removedPersonIds, bool force, CancellationToken ct)
    {
        await _publishGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var session = sp.GetRequiredService<SessionService>();
            if (!session.IsUnlocked) return 0;

            var nodeRepo = sp.GetRequiredService<INodeIdentityRepository>();
            var identity = await nodeRepo.GetAsync().ConfigureAwait(false);
            if (identity is null || BlindNodeId.IsBlind(identity.NodeId)) return 0;

            var accountsHomeRepo = sp.GetRequiredService<IAccountsHomeRepository>();
            var home = await accountsHomeRepo.GetAsync().ConfigureAwait(false);
            if (home is null || home.NodeId != identity.NodeId) return 0;

            var whitelistRepo = sp.GetRequiredService<IWhitelistRepository>();
            var sharedPeers = (await whitelistRepo.GetAllActiveAsync().ConfigureAwait(false))
                .Where(p => !BlindNodeId.IsBlind(p.NodeId)
                            && p.NodeId != identity.NodeId
                            && OwnershipKinds.NormalizeOrNull(p.Ownership) == OwnershipKinds.Shared)
                .OrderBy(p => p.NodeId)
                .ToList();

            if (sharedPeers.Count == 0)
                return 0;

            var missingCap = sharedPeers
                .Where(p => !MetadataCapabilities.HasSharedDoorsV1(MetadataCapabilities.Parse(p.LastCapabilities)))
                .Select(p => p.DisplayName)
                .ToList();
            if (missingCap.Count > 0)
            {
                logger.LogInformation(
                    "Deferring superadmin_doors_set publish: shared nodes lacking {Cap}: {Nodes}",
                    MetadataCapabilities.SharedDoorsV1, string.Join(", ", missingCap));
                return 0;
            }

            var validPeers = new List<WhitelistEntry>(sharedPeers.Count);
            foreach (var peer in sharedPeers)
            {
                try
                {
                    _ = DekEnvelope.Ed25519PublicKeyToX25519PublicKey(peer.Ed25519PublicKey);
                    validPeers.Add(peer);
                }
                catch (Exception ex) when (ex is ArgumentException or CryptographicException)
                {
                    logger.LogWarning(ex,
                        "Skipping shared peer {NodeId} ({DisplayName}) from superadmin_doors_set: unusable Ed25519 public key.",
                        peer.NodeId, peer.DisplayName);
                }
            }

            if (validPeers.Count == 0)
            {
                logger.LogWarning("Deferring superadmin_doors_set publish: no active shared node has a usable Ed25519 public key.");
                return 0;
            }

            var userRepo = sp.GetRequiredService<IUserRepository>();
            var slotRepo = sp.GetRequiredService<IKeySlotRepository>();
            var slotsById = (await slotRepo.GetAllAsync().ConfigureAwait(false))
                .ToDictionary(s => s.SlotId);

            var descriptors = new List<SuperadminDoorDescriptor>();
            // Every superadmin this node holds a sealed door for: its own accounts and, on a node that became the accounts node after holding the
            // list of another (a hand-over, or a removal taken over after the accounts node was lost, ADR 0014), the mirrors it holds - so the people
            // who stay keep their doors on the shared nodes. Never anybody a removal holds (release 2.12.0): their door went at the lock-out, and the
            // list of S2 names them as removed besides.
            var users = (await userRepo.ListActiveAsync().ConfigureAwait(false))
                .Where(u => string.Equals(u.Role, UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase)
                            && u.KeySlotId is > 0
                            && !u.IsBeingRemoved
                            && (removedPersonIds is null || !removedPersonIds.Contains(u.PersonId)))
                .OrderBy(u => u.PersonId);

            foreach (var user in users)
            {
                if (!slotsById.TryGetValue(user.KeySlotId!.Value, out var slot)) continue;
                if (slot.DoorVersion != 2
                    || !string.Equals(slot.SlotType, KeySlotTypes.User, StringComparison.Ordinal)
                    || slot.DoorPublicKey is null || slot.DoorPublicKey.Length == 0
                    || slot.Salt is null || slot.Salt.Length == 0)
                    continue;

                descriptors.Add(new SuperadminDoorDescriptor(
                    PersonId: user.PersonId,
                    Username: user.Username,
                    DisplayName: user.DisplayName,
                    PasswordHint: user.PasswordHint,
                    SaltB64: Convert.ToBase64String(slot.Salt),
                    ArgonMemory: slot.ArgonMemory ?? CryptoConstants.DefaultArgonMemory,
                    ArgonIterations: slot.ArgonIterations ?? CryptoConstants.DefaultArgonIterations,
                    ArgonParallelism: slot.ArgonParallelism ?? CryptoConstants.DefaultArgonParallelism,
                    DoorPublicKeyB64: Convert.ToBase64String(slot.DoorPublicKey)));
            }

            var digest = ComputeDigest(validPeers, descriptors);
            var stateRepo = sp.GetRequiredService<ISharedDoorsStateRepository>();
            var state = await stateRepo.GetAsync().ConfigureAwait(false);
            if (!force && string.Equals(state.LastPublishedDigest, digest, StringComparison.Ordinal))
                return 0;

            // Allocate the monotonic version atomically BEFORE building/signing the event so concurrent
            // publishers (e.g. CLI and API) can never issue the same version; a crash after allocation skips one.
            // Above every version this node has seen for its own author: a list somebody forged with this node's key and a
            // huge version is overtaken by the next real one, and so is the counter of a node whose state was reset.
            var floor = await OwnAuthorVersionFloorAsync(sp, identity.NodeId, state).ConfigureAwait(false);
            var version = await stateRepo.AllocateNextVersionAsync(atLeast: floor).ConfigureAwait(false);
            var issuedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var eventId = Guid.NewGuid();
            var document = new SuperadminDoorsDocument(version, issuedAt, descriptors);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(document);

            var listKey = RandomNumberGenerator.GetBytes(CryptoConstants.KeySize);
            try
            {
                var aad = SuperadminDoorsCrypto.BuildListAad(version, identity.NodeId);
                var (ciphertext, iv) = AesGcmHelper.Encrypt(listKey, plaintext, aad);
                var recipients = validPeers.Select(p => new DekEnvelope.Recipient(p.NodeId, p.Ed25519PublicKey));
                var envelopes = DekEnvelope.Build(listKey, eventId, recipients);
                var wirePeers = envelopes.Peers.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new DekEnvelopeBox(kvp.Value.WrappedB64, kvp.Value.NonceB64),
                    StringComparer.Ordinal);

                var payload = new SuperadminDoorsSetPayload(
                    Version: version,
                    IssuedAt: issuedAt,
                    CiphertextB64: Convert.ToBase64String(ciphertext),
                    IvB64: Convert.ToBase64String(iv),
                    ListEnvelopes: new DekEnvelopesPayload(envelopes.EphemeralPublicKeyB64, wirePeers));

                var clock = sp.GetRequiredService<ILamportClock>();
                var policy = sp.GetRequiredService<ISyncProtocolPolicy>();
                var eventLogRepo = sp.GetRequiredService<IEventLogRepository>();
                var syncTrigger = sp.GetRequiredService<ISyncTrigger>();

                // An own event: signed and logged inside the event-write gate, with the identity read inside it. An identity re-key
                // holds the gate while it lists the node's own events and swaps the key; this list is signed by the key current when it is
                // written (its node id, which the list's AAD binds, does not change with a re-key).
                using var writeGate = await EventWriteGate.Instance.EnterAsync(ct).ConfigureAwait(false);
                using var ownerFlow = EventWriteGate.EnterOwnerFlow();
                identity = await nodeRepo.GetAsync().ConfigureAwait(false) ?? identity;
                var evt = new SyncEvent
                {
                    EventId = eventId,
                    NodeId = identity.NodeId,
                    LamportTs = clock.Tick(),
                    EventType = EventTypes.SuperadminDoorsSet,
                    Payload = JsonSerializer.Serialize(payload),
                    Signature = [],
                    ProtocolVersion = policy.Declared,
                    CreatedAt = DateTime.UtcNow,
                    ActorType = "system",
                    ActorName = identity.DisplayName,
                };
                evt.Signature = NodeIdentityVault.SignWithIdentityOrGetDek(
                    identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
                    identity.NodeId, session.GetMasterDek, EventSignature.BuildPayload(evt));

                await eventLogRepo.AppendAsync(evt).ConfigureAwait(false);
                await stateRepo.RecordPublishedDigestAsync(digest).ConfigureAwait(false);
                syncTrigger.Signal();

                logger.LogInformation(
                    "Published superadmin_doors_set version {Version} with {DoorCount} door(s) for {PeerCount} shared node(s)",
                    version, descriptors.Count, sharedPeers.Count);
                return version;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(listKey);
            }
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <summary>A version floor far below <see cref="long.MaxValue"/>: whatever a forger claimed, the next version never overflows.</summary>
    public const long MaxVersionFloor = long.MaxValue / 4;

    /// <summary>
    /// The highest list version this node has seen for its own author: the lists of this author it holds in its log, the one it
    /// last received from itself, and what the shared nodes report having applied while this node is the accounts node (they apply only the
    /// accounts node's lists, so a version there is one signed as this node - by it, or by somebody holding its key).
    /// </summary>
    private static async Task<long> OwnAuthorVersionFloorAsync(IServiceProvider sp, Guid self, SharedDoorsStateRecord state)
    {
        long floor = state.LatestAuthorNodeId == self ? state.LatestReceivedVersion : 0;
        using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var logged = await conn.ExecuteScalarAsync<long?>(
            @"SELECT MAX(CAST(json_extract(payload, '$.version') AS INTEGER)) FROM tbl_event
              WHERE event_type = @Type AND node_id = @Self COLLATE NOCASE",
            new { Type = EventTypes.SuperadminDoorsSet, Self = self.ToString("D") }).ConfigureAwait(false);
        floor = Math.Max(floor, logged ?? 0);
        var reports = await conn.QueryAsync<string?>(
            @"SELECT s.extra_json FROM tbl_node_state s JOIN tbl_whitelist w ON w.node_id = s.node_id COLLATE NOCASE
              WHERE w.status = 'A' AND w.node_id <> @Self COLLATE NOCASE",
            new { Self = self.ToString("D") }).ConfigureAwait(false);
        foreach (var extra in reports)
            floor = Math.Max(floor, EventApplier.NodeStateNumber(extra, SharedDoorsNodeStateContributor.AppliedDoorsVersionKey));
        return Math.Min(floor, MaxVersionFloor);
    }

    private static string ComputeDigest(
        List<WhitelistEntry> sharedPeers,
        List<SuperadminDoorDescriptor> descriptors)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var peer in sharedPeers)
        {
            sha.AppendData(peer.NodeId.ToByteArray());
            sha.AppendData(peer.Ed25519PublicKey);
        }
        sha.AppendData([0xFF]);
        foreach (var d in descriptors)
        {
            sha.AppendData(d.PersonId.ToByteArray());
            AppendLengthPrefixed(sha, d.Username);
            AppendLengthPrefixed(sha, d.DisplayName);
            AppendLengthPrefixed(sha, d.PasswordHint ?? "");
            AppendLengthPrefixed(sha, d.SaltB64);
            AppendInt(sha, d.ArgonMemory);
            AppendInt(sha, d.ArgonIterations);
            AppendInt(sha, d.ArgonParallelism);
            AppendLengthPrefixed(sha, d.DoorPublicKeyB64);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());

        static void AppendLengthPrefixed(IncrementalHash hash, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            AppendInt(hash, bytes.Length);
            hash.AppendData(bytes);
        }

        static void AppendInt(IncrementalHash hash, int value)
        {
            Span<byte> buf = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf, value);
            hash.AppendData(buf);
        }
    }

    private void CancelPendingTimer()
    {
        lock (_timerLock)
        {
            _pendingCts?.Cancel();
            _pendingCts?.Dispose();
            _pendingCts = null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        CancelPendingTimer();
        _publishGate.Dispose();
    }
}
