using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Channels;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// What a TLS certificate must show for ONE peer (docs/design/lan-peer-sync.md section 9, decision D4): one of its pinned leaf keys, or a leaf its local
/// CA signed (<c>ca-pin</c>), or - only for a peer that is not pinned at all, as today - an ordinary CA-valid certificate. Nothing ever falls back to
/// "accept any certificate": a peer that announces no CA key keeps its leaf pin.
/// </summary>
/// <param name="LeafPins">The pins (base64url SHA-256 of the SubjectPublicKeyInfo) a leaf may have.</param>
/// <param name="CaKey">The peer's local CA public key (DER SubjectPublicKeyInfo), or null.</param>
/// <param name="AllowPublicCa">True when the peer's row is not pinned: an ordinary certificate passes too (a server behind a public CA) - at
/// <see cref="PublicCaHost"/> only.</param>
public sealed record PeerTlsAnchor(IReadOnlyList<string> LeafPins, byte[]? CaKey, bool AllowPublicCa)
{
    /// <summary>
    /// The one host where <see cref="AllowPublicCa"/> applies: the host of the row's own address. Every other address of the peer (announced, seen by
    /// mDNS, typed here) is held to its pinned keys alone, so a forged mDNS record or announcement naming a host with an ordinary certificate never
    /// passes as the peer (docs/design/lan-peer-sync.md 14). Null: nowhere.
    /// </summary>
    public string? PublicCaHost { get; init; }

    /// <summary>The anchor of one pin and nothing else (a pair code, a join code, a row in <c>pin</c> mode).</summary>
    public static PeerTlsAnchor PinOnly(string pin) => new([pin], null, false);

    /// <summary>
    /// The anchor of a peer: the pin its row holds (a row in <c>pin</c> mode, replicated), the leaf pin and the CA key it announced about itself in its
    /// signed <c>node_endpoints</c> record or gave at the join (a row upgraded LOCALLY to accept either; the replicated pin stays as it is for older
    /// nodes). Null when there is nothing to pin: the ordinary check, exactly as before.
    /// </summary>
    public static PeerTlsAnchor? For(WhitelistEntry row, PeerEndpointRecord? announced)
    {
        ArgumentNullException.ThrowIfNull(row);
        var rowPin = row.EffectivePin;
        var pins = new List<string>();
        if (rowPin is not null) pins.Add(rowPin);
        if (announced?.TlsLeafSpki is { } leaf && Spki.IsWellFormed(leaf) && !pins.Contains(leaf, StringComparer.Ordinal)) pins.Add(leaf);
        var ca = announced?.CaKeyBytes;
        if (pins.Count == 0 && ca is null) return null;
        var primaryHost = Uri.TryCreate(row.ApiAddress ?? "", UriKind.Absolute, out var primary) ? primary.Host : null;
        var allowPublic = rowPin is null && primaryHost is not null;
        return new PeerTlsAnchor(pins, ca, allowPublic) { PublicCaHost = allowPublic ? primaryHost : null };
    }

    /// <summary>The anchor a request to <paramref name="uri"/> is checked against: this one, without the public CAs anywhere but <see cref="PublicCaHost"/>.</summary>
    public PeerTlsAnchor AtHost(Uri? uri) =>
        AllowPublicCa && (PublicCaHost is null || uri is null || !string.Equals(uri.Host, PublicCaHost, StringComparison.OrdinalIgnoreCase))
            ? this with { AllowPublicCa = false, PublicCaHost = null }
            : this;

    /// <summary>True when <paramref name="certificate"/> carries one of the pinned leaf keys, or is a current leaf the pinned CA signed.</summary>
    public bool Accepts(X509Certificate2? certificate, DateTime nowUtc)
    {
        if (certificate is null) return false;
        var spki = Spki.Of(certificate);
        foreach (var pin in LeafPins)
            if (Spki.Equal(spki, pin)) return true;
        if (CaKey is null) return false;
        // A leaf of the pinned CA: only while it is valid (a stolen old leaf of an earlier key is not accepted forever), with a day of clock skew.
        var notAfter = certificate.NotAfter.ToUniversalTime();
        var notBefore = certificate.NotBefore.ToUniversalTime();
        if (notAfter < nowUtc || notBefore > nowUtc.AddDays(1)) return false;
        return LeafSignature.IsSignedBy(certificate, CaKey);
    }

    /// <summary>The lane of the pinned client this anchor is checked in: one per exact anchor.</summary>
    public string LaneKey
    {
        get
        {
            var ca = CaKey is null ? "-" : Convert.ToHexString(SHA256.HashData(CaKey));
            return $"pins:{string.Join(',', LeafPins)}|ca:{ca}|pub:{(AllowPublicCa ? PublicCaHost ?? "-" : "0")}";
        }
    }
}

/// <summary>
/// Puts one peer's <see cref="PeerTlsAnchor"/> on every request a client sends (<see cref="SpkiPinRegistry.ExplicitAnchor"/>): the client of one session
/// with one peer, whichever address it dials. Wrapped around the pinned handler of the sync client (<see cref="SyncScheduler.HttpClientName"/>).
/// </summary>
public sealed class PeerAnchorStamp(PeerTlsAnchor? anchor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (anchor is not null) request.Options.Set(SpkiPinRegistry.ExplicitAnchor, anchor);
        return base.SendAsync(request, cancellationToken);
    }
}
