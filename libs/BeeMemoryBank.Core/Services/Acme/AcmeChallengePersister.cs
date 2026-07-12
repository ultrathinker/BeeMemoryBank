using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.Core.Services.Acme;

/// <summary>
/// Writes and reads the TLS-ALPN-01 ephemeral challenge certificate to/from a shared file under
/// the common data directory so that the <b>Node</b> process's live HTTPS listener can serve it
/// during a validation probe that is being orchestrated by the <b>Api</b> process.
/// </summary>
/// <remarks>
/// <para>
/// <b>File path.</b>  <c>&lt;dataDir&gt;/certs/acme/&lt;domain&gt;.live-challenge.json</c> — one
/// file PER DOMAIN (mirroring the existing <c>&lt;domain&gt;.pfx</c>/<c>&lt;domain&gt;.chain.pem</c>
/// naming), so two concurrent ACME requests for different domains never contend for the same file
/// or delete each other's in-flight challenge.
/// </para>
/// <para>
/// <b>Lifecycle.</b>
/// <see cref="AcmeCertificateService"/> calls <see cref="Write"/> immediately after calling
/// <c>TlsAlpnChallengeResponder.SetChallenge</c> (before asking the CA to validate) and
/// <see cref="Delete"/> in its <c>finally</c> block once the challenge resolves (success or
/// failure). The file therefore exists only for the few seconds a validation is in flight.
/// </para>
/// <para>
/// <b>Atomic write.</b>  The file is written via a <c>.tmp</c> → <see cref="File.Move"/> swap so a
/// crash mid-write can never leave a half-written record (mirrors the pattern in
/// <c>InternetAccessEndpoints.SaveAsync</c>).
/// </para>
/// <para>
/// <b>Read semantics for Node.</b>  The listener's certificate selector calls
/// <see cref="TryReadChallengeCert"/> on every TLS handshake. The file is only re-read/re-parsed
/// when its last-write time changes since the previous call for that SNI (cached otherwise), so
/// repeated handshakes during one active challenge don't re-import the PFX/re-allocate a CNG key
/// container each time; the previously cached certificate is disposed when superseded. If the
/// file is absent, malformed, or its <c>ExpiresAt</c> hint is in the past the method returns
/// <c>null</c>, causing the selector to fall through to the normal leaf certificate.
/// </para>
/// <para>
/// <b>Windows SChannel note.</b>  The PFX is exported with
/// <see cref="X509KeyStorageFlags.EphemeralKeySet"/> by the writer (Api process). The reader
/// (Node process) reloads it with <see cref="X509KeyStorageFlags.PersistKeySet"/> so that Windows
/// SChannel can acquire credentials from it for the server-side TLS handshake. This mirrors the
/// existing <c>CachedLeafCert.ToSchannelUsable</c> pattern in <c>NodeFront</c>.
/// </para>
/// </remarks>
public sealed class AcmeChallengePersister
{
    private readonly string _dir;
    private readonly object _cacheGate = new();
    private string? _cachedDomain;
    private DateTime _cachedWriteTimeUtc;
    private X509Certificate2? _cachedCert;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>
    /// Creates the persister.
    /// </summary>
    /// <param name="dataDir">The application data root. Challenge files are written under
    /// <c>&lt;dataDir&gt;/certs/acme/</c>.</param>
    public AcmeChallengePersister(string dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir))
            throw new ArgumentException("dataDir must not be empty.", nameof(dataDir));
        _dir = Path.Combine(dataDir, "certs", "acme");
    }

    /// <summary>
    /// The absolute path of the per-domain challenge file for <paramref name="domain"/>.
    /// Exposed for testing.
    /// </summary>
    public string FilePathFor(string domain) => Path.Combine(_dir, Normalize(domain) + ".live-challenge.json");

    /// <summary>
    /// Atomically writes the challenge certificate for <paramref name="domain"/> to its own shared
    /// file so that the Node process's TLS listener can pick it up on the next handshake.
    /// </summary>
    /// <param name="domain">The DNS identifier being validated.</param>
    /// <param name="cert">The ephemeral challenge certificate (with private key attached).
    /// Must be exportable as a PFX.</param>
    public void Write(string domain, X509Certificate2 cert)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException("Domain must not be empty.", nameof(domain));
        ArgumentNullException.ThrowIfNull(cert);

        Directory.CreateDirectory(_dir);

        // Export the cert + private key as a PFX. We use no password (the file is protected by
        // OS filesystem permissions like the other files in <data>/certs/acme/). EphemeralKeySet
        // is fine for the export; the reader reimports with PersistKeySet for SChannel.
        var pfxBytes = cert.Export(X509ContentType.Pfx);

        var record = new ChallengeRecord
        {
            Domain = Normalize(domain),
            PfxBase64 = Convert.ToBase64String(pfxBytes),
            // Validity hint: the challenge cert itself is short-lived (10 minutes per
            // TlsAlpn01CertificateBuilder), but we use its NotAfter so Node discards stale files
            // even if the Api process crashes and never calls Delete().
            ExpiresAt = cert.NotAfter.ToUniversalTime(),
        };

        var json = JsonSerializer.Serialize(record, JsonOpts);
        var path = FilePathFor(domain);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Deletes the shared challenge file for <paramref name="domain"/> only. Safe to call when the
    /// file does not exist. Never affects another domain's in-flight challenge file.
    /// </summary>
    public void Delete(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return;
        try { File.Delete(FilePathFor(domain)); }
        catch (IOException) { /* best-effort: the file may already be gone */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    /// <summary>
    /// Reads the challenge file for <paramref name="sni"/> and returns its certificate if a
    /// challenge is active for that exact domain. Returns <c>null</c> when:
    /// <list type="bullet">
    ///   <item>the per-domain file does not exist;</item>
    ///   <item>the file is malformed or cannot be read;</item>
    ///   <item>the challenge has already expired (<c>ExpiresAt</c> is in the past).</item>
    /// </list>
    /// The returned certificate is loaded with <see cref="X509KeyStorageFlags.PersistKeySet"/> so
    /// Windows SChannel can use it for a server-side TLS handshake. It is cached internally (keyed
    /// on the file's last-write time) and MUST NOT be disposed by the caller — ownership stays with
    /// this instance, which disposes the previous cert once superseded or cleared.
    /// </summary>
    public X509Certificate2? TryReadChallengeCert(string? sni)
    {
        if (string.IsNullOrWhiteSpace(sni)) return null;
        var domain = Normalize(sni);
        var path = FilePathFor(domain);

        DateTime writeTimeUtc;
        try
        {
            if (!File.Exists(path)) { ClearCache(domain); return null; }
            writeTimeUtc = File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            ClearCache(domain);
            return null;
        }

        lock (_cacheGate)
        {
            if (_cachedCert != null && _cachedDomain == domain && _cachedWriteTimeUtc == writeTimeUtc)
                return _cachedCert;
        }

        ChallengeRecord? record;
        try
        {
            var json = File.ReadAllText(path);
            record = JsonSerializer.Deserialize<ChallengeRecord>(json, JsonOpts);
        }
        catch
        {
            // Malformed or unreadable file — treat as no active challenge.
            return null;
        }

        if (record is null) return null;
        if (record.ExpiresAt <= DateTime.UtcNow) return null;

        X509Certificate2 loaded;
        try
        {
            var pfxBytes = Convert.FromBase64String(record.PfxBase64 ?? "");
            // PersistKeySet so Windows SChannel can acquire credentials.
            loaded = X509CertificateLoader.LoadPkcs12(
                pfxBytes, (string?)null,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        }
        catch
        {
            return null;
        }

        lock (_cacheGate)
        {
            // Another thread may have already cached the same (domain, writeTime) — dispose our
            // redundant load and reuse theirs rather than leaking a second key container.
            if (_cachedCert != null && _cachedDomain == domain && _cachedWriteTimeUtc == writeTimeUtc)
            {
                loaded.Dispose();
                return _cachedCert;
            }
            _cachedCert?.Dispose();
            _cachedCert = loaded;
            _cachedDomain = domain;
            _cachedWriteTimeUtc = writeTimeUtc;
            return loaded;
        }
    }

    private void ClearCache(string domain)
    {
        lock (_cacheGate)
        {
            if (_cachedDomain != domain) return;
            _cachedCert?.Dispose();
            _cachedCert = null;
            _cachedDomain = null;
        }
    }

    private static string Normalize(string domain) => domain.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>Serialized representation of an active TLS-ALPN-01 challenge.</summary>
    private sealed class ChallengeRecord
    {
        [JsonPropertyName("domain")]
        public string Domain { get; set; } = "";

        /// <summary>Base64-encoded PFX bytes (no password).</summary>
        [JsonPropertyName("pfxBase64")]
        public string? PfxBase64 { get; set; }

        /// <summary>UTC time after which this record should be considered stale.</summary>
        [JsonPropertyName("expiresAt")]
        public DateTime ExpiresAt { get; set; }
    }
}
