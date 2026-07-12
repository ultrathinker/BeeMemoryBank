using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Certes;
using Certes.Acme;

namespace BeeMemoryBank.Core.Services.Acme;

/// <summary>
/// ACME v2 certificate client (RFC 8555) built over the <c>Certes</c> library. It obtains real,
/// publicly-trusted certificates from Let's Encrypt for a node that has a real public domain, using
/// the <b>TLS-ALPN-01</b> challenge (RFC 8737) — not HTTP-01 or DNS-01.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why TLS-ALPN-01.</b> This node does not necessarily have port 80 open (rules out HTTP-01) and
/// does not control the DNS zone (rules out DNS-01). TLS-ALPN-01 answers the challenge over the same
/// TLS listener (port 443) that already serves real traffic: when the CA connects with the
/// <c>acme-tls/1</c> ALPN protocol, the listener's certificate selector returns the ephemeral
/// challenge certificate (registered in <see cref="TlsAlpnChallengeResponder"/>). A separate
/// front-wiring task plugs <see cref="TlsAlpnChallengeResponder"/> into that selector.
/// </para>
/// <para>
/// <b>Storage.</b> Certs + keys live under <c>&lt;dataDir&gt;/certs/acme/</c>:
/// </para>
/// <list type="bullet">
///   <item><c>account.pem</c> — the ACME account key (EC P-256); reused across runs;</item>
///   <item><c>&lt;domain&gt;.pfx</c> — the issued leaf certificate + its private key (PKCS#12);</item>
///   <item><c>&lt;domain&gt;.chain.pem</c> — the full issuance chain for reference;</item>
///   <item><c>&lt;domain&gt;.meta.json</c> — <see cref="StoredCertificate"/> metadata (paths,
///   validity window, password) so the renewal check and cert loader don't need to parse the PFX.</item>
/// </list>
/// <para>
/// <b>Renewal.</b> <see cref="CheckRenewalsAsync"/> is the callable renewal operation (it re-issues
/// any cert whose remaining validity is at or below
/// <see cref="AcmeOptions.RenewalDaysThreshold"/>). It is deliberately <i>not</i> a timer — the
/// host-wiring task decides how often to call it.
/// </para>
/// <para>
/// <b>Verification level (spike).</b> The TLS-ALPN-01 challenge certificate construction and the
/// storage/renewal logic are covered by offline unit tests. The live ACME protocol round-trip was
/// <i>not</i> exercised in this environment (no owned domain; production rate limits; no Pebble
/// available) — see the task report. The <see cref="RequestCertificateAsync"/> flow uses the
/// verified Certes 3.0.4 API surface but its end-to-end behavior against a live CA is unverified.
/// </para>
/// </remarks>
public sealed class AcmeCertificateService
{
    private readonly string _dataDir;
    private readonly AcmeOptions _options;
    private readonly TlsAlpnChallengeResponder _responder;
    private readonly AcmeChallengePersister? _challengePersister;
    private readonly Action<string>? _trace;

    private string AcmeDir => Path.Combine(_dataDir, "certs", "acme");
    private string AccountKeyPath => Path.Combine(AcmeDir, "account.pem");

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="dataDir">The application data root. Certs are stored under
    /// <c>&lt;dataDir&gt;/certs/acme/</c>.</param>
    /// <param name="options">ACME configuration (directory, contacts, renewal threshold, polling).</param>
    /// <param name="responder">The shared TLS-ALPN-01 challenge registry the TLS listener queries.</param>
    /// <param name="challengePersister">Optional cross-process challenge file writer. When provided,
    /// <see cref="RequestCertificateAsync"/> also writes the challenge certificate to the shared
    /// data directory so that the Node process's live HTTPS listener can serve it during the
    /// validation probe. Pass <c>null</c> (or omit) to disable cross-process file hand-off (e.g.
    /// when constructing a transient service for read-only metadata access).</param>
    /// <param name="trace">Optional diagnostic sink (e.g. a logger wrapper). May be <c>null</c>.</param>
    public AcmeCertificateService(
        string dataDir,
        AcmeOptions options,
        TlsAlpnChallengeResponder responder,
        AcmeChallengePersister? challengePersister = null,
        Action<string>? trace = null)
    {
        if (string.IsNullOrWhiteSpace(dataDir))
            throw new ArgumentException("dataDir must not be empty.", nameof(dataDir));
        _dataDir = dataDir;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        _challengePersister = challengePersister;
        _trace = trace;
    }

    /// <summary>
    /// Requests (or re-issues) a certificate for <paramref name="domain"/> using TLS-ALPN-01.
    /// Blocks until the CA has validated the challenge and issued the certificate.
    /// </summary>
    public async Task<StoredCertificate> RequestCertificateAsync(string domain, CancellationToken ct = default)
    {
        domain = NormalizeDomain(domain);
        _trace?.Invoke($"ACME: requesting certificate for '{domain}' via {_options.DirectoryUri}");

        var acme = await GetOrCreateAccountContextAsync(ct);

        // 1. Place the order.
        var order = await acme.NewOrder(new List<string> { domain });
        _trace?.Invoke("ACME: order placed");

        // 2. Pick the TLS-ALPN-01 challenge from the order's first authorization.
        var authorizations = await order.Authorizations();
        var authz = authorizations.FirstOrDefault()
            ?? throw new InvalidOperationException($"ACME order for '{domain}' returned no authorization.");
        var challenge = await authz.TlsAlpn();
        _trace?.Invoke($"ACME: selected challenge type '{challenge.Type}' token='{challenge.Token}'");

        // 3. Build the ephemeral challenge cert and register it so the TLS listener serves it.
        //    For the cross-process hand-off: also write the cert to the shared file so that the
        //    Node process's live HTTPS listener (which runs in a separate process) can pick it up.
        var challengeCert = TlsAlpn01CertificateBuilder.Build(domain, challenge.KeyAuthz);
        _responder.SetChallenge(domain, challengeCert);
        _challengePersister?.Write(domain, challengeCert);
        _trace?.Invoke($"ACME: challenge cert registered (domain={domain})" +
            (_challengePersister != null ? $"; written to {_challengePersister.FilePath}" : " (no file persister)"));

        try
        {
            // 4. Ask the CA to validate. The CA will connect with SNI=domain, ALPN=acme-tls/1; the
            //    listener's selector returns challengeCert via TlsAlpnChallengeResponder (in-process
            //    path) or AcmeChallengePersister (cross-process path via the shared file).
            await challenge.Validate();
            _trace?.Invoke("ACME: validation triggered; polling authorization status");

            // 5. Poll the authorization until valid / invalid / timeout.
            await WaitForAuthorizationAsync(authz, domain, ct);

            // 6. Generate a fresh private key + CSR and finalize the order to obtain the chain.
            var certKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
            var csr = new CsrInfo { CommonName = domain };
            var chain = await order.Generate(csr, certKey, preferredChain: null, retryCount: 60);
            _trace?.Invoke("ACME: certificate issued");

            // 7. Persist PFX + chain PEM + metadata.
            return PersistIssuedCertificate(domain, chain, certKey);
        }
        finally
        {
            // Always tear down the challenge registration, even on failure, so a leftover ephemeral
            // cert can't shadow the real cert for the same SNI.
            _responder.RemoveChallenge(domain);
            _challengePersister?.Delete();
            _trace?.Invoke("ACME: challenge cert deregistered");
        }
    }

    /// <summary>
    /// Scans every stored certificate and re-issues any whose remaining validity is at or below
    /// <see cref="AcmeOptions.RenewalDaysThreshold"/>. Returns the list of (re)issued certificates;
    /// an empty list means nothing needed renewal. This is the callable renewal operation — it is
    /// not a timer; the host decides cadence.
    /// </summary>
    public async Task<IReadOnlyList<StoredCertificate>> CheckRenewalsAsync(CancellationToken ct = default)
    {
        var renewed = new List<StoredCertificate>();
        foreach (var stored in ListStoredCertificates())
        {
            if (!stored.NeedsRenewal(_options.RenewalDaysThreshold))
            {
                _trace?.Invoke($"ACME: '{stored.Domain}' valid until {stored.NotAfter:u}; skipping renewal");
                continue;
            }
            _trace?.Invoke($"ACME: '{stored.Domain}' needs renewal (NotAfter={stored.NotAfter:u})");
            renewed.Add(await RequestCertificateAsync(stored.Domain, ct));
        }
        return renewed;
    }

    /// <summary>
    /// Returns the stored metadata for <paramref name="domain"/>, or <c>null</c> if none is stored.
    /// Use <see cref="StoredCertificate.LoadCertificate"/> to obtain the live
    /// <see cref="X509Certificate2"/> for the TLS listener.
    /// </summary>
    public StoredCertificate? GetStoredCertificate(string domain)
    {
        domain = NormalizeDomain(domain);
        var metaPath = MetaPath(domain);
        return File.Exists(metaPath) ? ReadMeta(metaPath) : null;
    }

    /// <summary>Enumerates metadata for every persisted certificate.</summary>
    public IReadOnlyList<StoredCertificate> ListStoredCertificates()
    {
        EnsureDir();
        var result = new List<StoredCertificate>();
        foreach (var metaPath in Directory.GetFiles(AcmeDir, "*.meta.json"))
        {
            var stored = ReadMeta(metaPath);
            if (stored != null) result.Add(stored);
        }
        return result;
    }

    // ─────────────────────────────── account ───────────────────────────────

    private async Task<IAcmeContext> GetOrCreateAccountContextAsync(CancellationToken ct)
    {
        EnsureDir();

        IKey accountKey;
        if (File.Exists(AccountKeyPath))
        {
            string pem;
            if (OperatingSystem.IsWindows())
            {
                var bytes = await File.ReadAllBytesAsync(AccountKeyPath, ct);
                try
                {
                    var decryptedBytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                    pem = Encoding.UTF8.GetString(decryptedBytes);
                }
                catch (CryptographicException)
                {
                    // Fallback if the file is plain text PEM
                    pem = Encoding.UTF8.GetString(bytes);
                }
            }
            else
            {
                pem = await File.ReadAllTextAsync(AccountKeyPath, ct);
            }
            accountKey = KeyFactory.FromPem(pem);
            _trace?.Invoke("ACME: loaded existing account key");
        }
        else
        {
            accountKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
            _trace?.Invoke("ACME: generated new account key");
        }

        var acme = new AcmeContext(new Uri(_options.DirectoryUri), accountKey);

        // ACME newAccount is idempotent: for an existing key the CA returns the existing account
        // rather than creating a duplicate. So calling it on every run is safe and verifies the key.
        var contacts = BuildContacts();
        await acme.NewAccount(contacts, termsOfServiceAgreed: true);

        // Persist the key only after the account is known-good.
        if (accountKey is IEncodable encodable)
        {
            var pem = encodable.ToPem();
            if (OperatingSystem.IsWindows())
            {
                var pemBytes = Encoding.UTF8.GetBytes(pem);
                var encryptedBytes = ProtectedData.Protect(pemBytes, null, DataProtectionScope.CurrentUser);
                await File.WriteAllBytesAsync(AccountKeyPath, encryptedBytes, ct);
            }
            else
            {
                await File.WriteAllTextAsync(AccountKeyPath, pem, ct);
            }
        }
        else
        {
            throw new InvalidOperationException(
                "ACME account key does not implement IEncodable; cannot persist. " +
                "This indicates a Certes API change.");
        }

        return acme;
    }

    private IList<string> BuildContacts()
    {
        var email = _options.ContactsEmail?.Trim();
        return string.IsNullOrEmpty(email)
            ? new List<string>()
            : new List<string> { "mailto:" + email };
    }

    // ─────────────────────────── validation polling ────────────────────────

    private async Task WaitForAuthorizationAsync(IAuthorizationContext authz, string domain, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _options.ChallengeTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var resource = await authz.Resource();
            var status = resource.Status ?? Certes.Acme.Resource.AuthorizationStatus.Pending;

            if (status == Certes.Acme.Resource.AuthorizationStatus.Valid)
            {
                _trace?.Invoke($"ACME: authorization for '{domain}' is valid");
                return;
            }
            if (status == Certes.Acme.Resource.AuthorizationStatus.Invalid)
            {
                throw new InvalidOperationException(
                    $"ACME validation of '{domain}' failed (status=Invalid). " +
                    "Check that the TLS listener is serving the acme-tls/1 challenge cert on port 443 " +
                    "and that the domain's DNS points at this host.");
            }

            try
            {
                await Task.Delay(_options.ChallengePollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
        }
        throw new TimeoutException(
            $"ACME validation of '{domain}' timed out after {_options.ChallengeTimeout.TotalSeconds:0}s.");
    }

    // ────────────────────────────── persistence ────────────────────────────

    private StoredCertificate PersistIssuedCertificate(string domain, CertificateChain chain, IKey certKey)
    {
        EnsureDir();

        var pfxBuilder = chain.ToPfx(certKey);
        pfxBuilder.FullChain = true;
        var password = RandomPassword(_options.PfxPasswordLength);
        var pfxBytes = pfxBuilder.Build(domain, password);

        var pfxPath = Path.Combine(AcmeDir, domain + ".pfx");
        var chainPemPath = Path.Combine(AcmeDir, domain + ".chain.pem");
        var metaPath = MetaPath(domain);

        File.WriteAllBytes(pfxPath, pfxBytes);
        File.WriteAllText(chainPemPath, chain.ToPem(certKey));

        // Read validity window from the freshly-issued PFX so the metadata is authoritative.
        DateTimeOffset notBefore, notAfter;
        using (var parsed = X509CertificateLoader.LoadPkcs12(
                   pfxBytes, password, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable))
        {
            notBefore = new DateTimeOffset(parsed.NotBefore, TimeSpan.Zero);
            notAfter = new DateTimeOffset(parsed.NotAfter, TimeSpan.Zero);
        }

        var stored = new StoredCertificate
        {
            Domain = domain,
            PfxPath = pfxPath,
            ChainPemPath = chainPemPath,
            PfxPassword = OperatingSystem.IsWindows() ? StoredCertificate.EncryptPassword(password) : password,
            NotBefore = notBefore.UtcDateTime,
            NotAfter = notAfter.UtcDateTime,
            IssuedAt = DateTime.UtcNow,
            DirectoryUri = _options.DirectoryUri,
        };

        WriteMeta(metaPath, stored);
        _trace?.Invoke($"ACME: persisted cert for '{domain}' → {pfxPath} (valid until {stored.NotAfter:u})");
        return stored;
    }

    private string MetaPath(string domain) => Path.Combine(AcmeDir, domain + ".meta.json");

    private static StoredCertificate? ReadMeta(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<StoredCertificate>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteMeta(string path, StoredCertificate stored)
    {
        var json = JsonSerializer.Serialize(stored, MetaJsonOptions);
        File.WriteAllText(path, json);
    }

    private static readonly JsonSerializerOptions MetaJsonOptions = new()
    {
        WriteIndented = true,
    };

    // ─────────────────────────────── helpers ───────────────────────────────

    private void EnsureDir()
    {
        if (!Directory.Exists(AcmeDir))
            Directory.CreateDirectory(AcmeDir);
    }

    private static string NormalizeDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException("Domain must not be empty.", nameof(domain));
        return domain.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static string RandomPassword(int length)
    {
        // URL-safe base64 of random bytes gives a strong password without ambiguous characters.
        var bytes = RandomNumberGenerator.GetBytes(length);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
