using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Recovery;

namespace BeeMemoryBank.Api.Services.BlindPhone;

/// <summary>
/// A node an Android blind node can call: it listens on https and the network knows how to trust its TLS endpoint
/// (<paramref name="Trust"/>: <c>pin</c>, or <c>public-ca</c> for a hub behind a real certificate).
/// </summary>
public sealed record BlindPhoneListener(Guid NodeId, string DisplayName, string Address, bool IsBlind, string Trust = BlindTrust.Pin);

/// <summary>What Windows shows the phone after pairing: the "where to call" code.</summary>
public sealed record BlindPhonePaired(Guid PhoneId, string DisplayName, string CallCode, Guid ListenerId);

/// <summary>The pairing cannot go ahead; the message says what to do.</summary>
public sealed class BlindPhonePairingException(string message) : InvalidOperationException(message);

/// <summary>
/// Windows side of pairing an Android blind node (plan section 10), on the Blind nodes page. The phone
/// never listens, so it is told a node that does — a server blind node or a hub — by that node's own
/// whitelist row: address, NodeId, TLS pin and Ed25519 key come from what the network already pinned,
/// never from the phone's code. Then, as for a server blind node: pre-flight, <c>whitelist_add</c> (a
/// plain peer, never a superadmin), and the backup key sealed under the DEK as
/// <c>android-backup:&lt;id&gt;</c> — the phone waits with its backups until its recovery set holds it.
/// </summary>
public sealed class BlindPhonePairingService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    SessionService session,
    TimeProvider time,
    ILogger<BlindPhonePairingService> logger)
{
    /// <summary>The nodes a phone could be told to call, blind nodes first.</summary>
    public async Task<IReadOnlyList<BlindPhoneListener>> ListenersAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
        return rows.Where(CanBeCalled)
            .Select(r => new BlindPhoneListener(r.NodeId, r.DisplayName, r.ApiAddress!, BlindNodeId.IsBlind(r.NodeId), r.EffectiveTlsTrust!))
            .OrderByDescending(l => l.IsBlind).ThenBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<BlindPhonePaired> PairAsync(string phoneCode, Guid listenerId, CancellationToken ct)
    {
        // Sealing the backup key needs the DEK; checked first so nothing is published half-way.
        if (!session.IsUnlocked)
            throw new BlindPhonePairingException("Unlock this PC first: the phone's backup key is sealed under the master key.");

        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var listener = await whitelist.GetByNodeIdAsync(listenerId);
        if (listener == null || !CanBeCalled(listener))
            throw new BlindPhonePairingException(
                "The phone can only call a node that listens on https with a pinned or a publicly valid certificate — a blind node or a hub. " +
                "Add one first (Admin, Trusted Nodes: \"Let blind copies call this node\"); this PC alone cannot serve a phone.");

        var enrollment = BlindPhoneEnrollment.Prepare(phoneCode, listener.ApiAddress!, listener.NodeId,
            listener.TlsSpki ?? "", listener.Ed25519PublicKey, time.GetUtcNow().UtcDateTime, out var error,
            listener.EffectiveTlsTrust!)
            ?? throw new FormatException(error);
        try
        {
            var phone = enrollment.Entry;
            var existing = await whitelist.GetByNodeIdAsync(phone.NodeId, includeDeleted: true);
            if (existing is { Status: "A" } && !existing.Ed25519PublicKey.AsSpan().SequenceEqual(phone.Ed25519PublicKey))
                throw new BlindPhonePairingException(
                    "A device with this id is already in the network under another key. Disconnect it first.");

            using (var http = httpClientFactory.CreateClient(SyncScheduler.HttpClientName))
                await sp.GetRequiredService<BlindPreflight>().RunAsync(http, ct);

            if (existing is not { Status: "A" })
            {
                // Log first, like every whitelist write: the row carries the version the mesh is told.
                var version = await sp.GetRequiredService<IEventLogger>().LogWhitelistAddAsync(phone);
                phone.LamportTs = version.LamportTs;
                phone.SourceNodeId = version.SourceNodeId;
                if (existing is null) await whitelist.CreateAsync(phone);
                else await whitelist.UpdateAsync(phone);
            }
            // Pasted again (the phone was not told its call code the first time): the row stands, the
            // seal is published again — the same name, so the recovery set keeps one.
            // The binding of phone → producer is signed by this node (a superadmin: the pre-flight above), so a
            // restore can tell it from a replacement another full peer publishes under the same name.
            var me = await sp.GetRequiredService<INodeIdentityRepository>().GetAsync()
                ?? throw new BlindPhonePairingException("This node is not initialized.");
            var signed = enrollment.Seal with
            {
                PairedBy = me.NodeId,
                PairingSignature = sp.GetRequiredService<INodeAuthSigner>().SignChallenge(me, enrollment.Seal.PairingStatement(phone.NodeId)),
            };
            var sealedValue = signed.Encode();
            try
            {
                await sp.GetRequiredService<SealedSecretService>().PublishAsync(enrollment.SealedSecretName, sealedValue);
            }
            finally
            {
                Array.Clear(sealedValue);
            }

            logger.LogInformation("Android blind node {PhoneId} paired to call {ListenerId}", phone.NodeId, listener.NodeId);
            return new BlindPhonePaired(phone.NodeId, phone.DisplayName, enrollment.CallCode.ToString(), listener.NodeId);
        }
        finally
        {
            Array.Clear(enrollment.Seal.BackupKey);
        }
    }

    // The phone trusts the listener the way the network recorded it: by its pinned key, or by the system's
    // certificate chain for a hub on a public CA. Without an https origin and that, there is nothing to trust, and a
    // row the call code would not carry must not be offered.
    private static bool CanBeCalled(WhitelistEntry row) =>
        row.Status == "A" && BlindListener.IsCallable(row.ApiAddress, row.TlsSpki, row.Ed25519PublicKey, row.TlsTrust);
}
