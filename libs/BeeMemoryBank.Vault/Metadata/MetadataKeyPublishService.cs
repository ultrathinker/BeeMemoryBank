using System.Text.Json;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Vault.Metadata;

/// <summary>Whether this node may write sealed metadata now (docs/design/step-a-spec.md 4.4): not while a key it created is unpublished.</summary>
public interface IMetadataKeyPublishGate
{
    /// <summary>True while a DEK rotation's new metadata key has no logged <c>metadata_key_set</c> event yet.</summary>
    bool IsPending { get; }
}

/// <summary>
/// Step 2 of the metadata key of a DEK rotation (docs/design/step-a-spec.md 4.4): the rotation's own transaction created the key
/// row and the marker (<see cref="MetadataKeyPublishMarker"/>); this service appends the <c>metadata_key_set</c> event for that
/// row and deletes the marker in ONE transaction, then signals sync and asks for the early baseline (<c>key_rotation</c>). A
/// failure is never swallowed into "done": the marker stays, authoring of sealed metadata stays refused
/// (<see cref="MetadataKeyPublishPendingException"/>), and the step is repeated every <see cref="RetryPeriod"/> while the vault is
/// unlocked and at every unlock, until the marker is gone. A crash after the rotation committed resumes the same way: the marker
/// and the key row survive, the new DEK is back in the session at the next unlock, and the event is appended before this node
/// writes anything else sealed. One run at a time.
/// </summary>
public sealed class MetadataKeyPublishService : IMetadataKeyPublishGate, IDisposable
{
    public static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(MetadataKeyPublishPendingException.RetryAfterSeconds);

    private readonly IServiceScopeFactory _scopes;
    private readonly SessionService _session;
    private readonly IDbConnectionFactory _db;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly ITimer _timer;

    public MetadataKeyPublishService(
        IServiceScopeFactory scopes,
        SessionService session,
        IDbConnectionFactory db,
        ILogger<MetadataKeyPublishService>? logger = null,
        TimeProvider? time = null)
    {
        _scopes = scopes;
        _session = session;
        _db = db;
        _logger = logger ?? NullLogger<MetadataKeyPublishService>.Instance;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => OnTimer(), null, RetryPeriod, RetryPeriod);
    }

    /// <summary>Raised after the event was logged and the marker deleted, with the key id (the rotation that waited for it can finish).</summary>
    public event Func<string, Task>? Published;

    public bool IsPending => MetadataKeyPublishMarker.Read(_db) is not null;

    /// <summary>
    /// Runs the step once. True when nothing is outstanding (none was, or it was published now); false when it is still outstanding
    /// (the vault is locked, or the attempt failed: logged, retried by the timer and at the next unlock).
    /// </summary>
    public async Task<bool> TryPublishAsync(CancellationToken ct = default)
    {
        await _running.WaitAsync(ct);
        try
        {
            return await PublishCoreAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publishing the metadata key of the last key rotation failed; it is retried every {Seconds} s and at the next unlock",
                (int)RetryPeriod.TotalSeconds);
            return false;
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<bool> PublishCoreAsync(CancellationToken ct)
    {
        if (MetadataKeyPublishMarker.Read(_db) is not { } keyId) return true;
        if (!_session.IsUnlocked) return false;

        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var row = ReadKeyRow(keyId);
        if (row is null || row.EventId != "")
        {
            // A marker with no row to publish (a re-key replaced the keys) or whose row was already logged: nothing to do but forget it.
            _logger.LogWarning("The marker of an unpublished metadata key named a key that is {State}; the marker was cleared",
                row is null ? "gone" : "already published");
            ClearMarker();
            return true;
        }

        var nodeRepo = sp.GetRequiredService<INodeIdentityRepository>();
        var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException("Node is not initialized.");
        var clock = sp.GetRequiredService<ILamportClock>();
        var policy = sp.GetRequiredService<ISyncProtocolPolicy>();
        var eventLog = sp.GetRequiredService<IEventLogRepository>();

        var payload = new MetadataKeySetPayload(row.KeyId, row.Generation, row.DekFingerprint, row.EpochHint,
            Convert.ToBase64String(row.Wrapped), Convert.ToBase64String(row.Iv), row.CreatedAt);
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = identity.NodeId,
            LamportTs = clock.Tick(),
            EventType = EventTypes.MetadataKeySet,
            Payload = JsonSerializer.Serialize(payload),
            Signature = [],
            // A control event, clear at every protocol; stamped with what this node declares now.
            ProtocolVersion = policy.Declared,
            CreatedAt = DateTime.UtcNow,
            ActorType = "system",
            ActorName = identity.DisplayName,
        };
        evt.Signature = NodeIdentityVault.SignWithIdentityOrGetDek(
            identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV,
            identity.NodeId, _session.GetMasterDek, EventSignature.BuildPayload(evt));

        // The event and the end of the marker are one fact: both or neither.
        using (var conn = _db.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                if (!MetadataKeyPublishMarker.Clear(conn, tx))
                {
                    tx.Rollback(); // another run published it first
                    return true;
                }
                await eventLog.AppendAsync(evt, tx);
                conn.Execute("UPDATE tbl_metadata_key SET event_id = @EventId, lamport_ts = @Lamport WHERE key_id = @KeyId",
                    new { EventId = evt.EventId.ToString("D"), Lamport = evt.LamportTs, row.KeyId }, tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        _logger.LogInformation("Metadata key generation {Generation} published after the DEK rotation", row.Generation);

        // Past the commit nothing may undo "published": the marker is gone, so a failure from here on would be found by no retry. The
        // two notifications are best effort (the push loop and the baseline scheduler run on their own periods anyway), and what the
        // rotation still has to finish is kept in its own durable record (DekRotationCompletionRecord), not in this call.
        try
        {
            sp.GetRequiredService<ISyncTrigger>().Signal();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Waking the sync loop after publishing the metadata key failed; the loop runs on its own period");
        }
        try
        {
            sp.GetService<IBaselineScheduler>()?.RequestEarly(BaselineReasons.KeyRotation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Asking for the early baseline after publishing the metadata key failed; the scheduled baseline covers it");
        }
        await RaisePublishedAsync(row.KeyId);
        return true;
    }

    private async Task RaisePublishedAsync(string keyId)
    {
        if (Published is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Func<string, Task>>())
        {
            try
            {
                await handler(keyId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A handler of the published metadata key failed");
            }
        }
    }

    private UnpublishedKeyRow? ReadKeyRow(string keyId)
    {
        using var conn = _db.CreateConnection();
        return conn.Query<UnpublishedKeyRow>(
            @"SELECT key_id AS KeyId, generation AS Generation, dek_fingerprint AS DekFingerprint, epoch_hint AS EpochHint,
                     wrapped AS Wrapped, iv AS Iv, created_at AS CreatedAt, event_id AS EventId
                FROM tbl_metadata_key WHERE key_id = @keyId", new { keyId }).FirstOrDefault();
    }

    private void ClearMarker()
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        MetadataKeyPublishMarker.Clear(conn, tx);
        tx.Commit();
    }

    private void OnTimer()
    {
        try
        {
            if (!_session.IsUnlocked || !IsPending) return;
        }
        catch
        {
            return; // the database is gone (shutdown); nothing to retry
        }
        _ = Task.Run(() => TryPublishAsync());
    }

    public void Dispose() => _timer.Dispose();

    private sealed class UnpublishedKeyRow
    {
        public string KeyId { get; set; } = "";
        public int Generation { get; set; }
        public string DekFingerprint { get; set; } = "";
        public int EpochHint { get; set; }
        public byte[] Wrapped { get; set; } = [];
        public byte[] Iv { get; set; } = [];
        public string CreatedAt { get; set; } = "";
        public string EventId { get; set; } = "";
    }
}
