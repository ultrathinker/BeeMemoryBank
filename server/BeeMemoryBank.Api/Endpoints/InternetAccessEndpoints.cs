using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.Acme;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// /api/internet-access/* — thin HTTP surface that drives the "Access from the internet"
/// Web wizard (superplan §5 Ярус 2, Этап 5). It exposes the three already-merged pieces —
/// <see cref="DdnsUpdater"/>, <see cref="AcmeCertificateService"/> and the existing
/// <c>POST /api/sync/probe</c> reachability self-test — as small, superadmin-only endpoints
/// the wizard's PageModel can call through <c>ApiClient</c>.
///
/// <para><b>Design notes.</b></para>
/// <list type="bullet">
///   <item>The DDNS provider + IP-detection mode are runtime-chosen by the admin, so the
///   provider objects are constructed per-call from a persisted config file rather than
///   registered as fixed DI services. This matches how the underlying services are shaped
///   (creds live in the provider's ctor) and avoids touching <c>DdnsUpdater</c>/
///   <c>*Provider</c>/<c>*Config</c>.</item>
///   <item>Config is persisted as plain JSON under <c>&lt;data&gt;/internet-access/</c>.
///   This is consistent with <c>ddns-state.json</c> (already plain) and the single-admin,
///   OS-protected data dir; encrypting DDNS tokens under the master DEK is a future
///   hardening item, out of scope for the spike.</item>
///   <item>The ACME request constructs a fresh <see cref="TlsAlpnChallengeResponder"/>
///   per call. For a real issuance to succeed, the SAME responder instance must be wired
///   into the live TLS listener's certificate selector (the parallel front-wiring task).
///   Until then <see cref="AcmeCertificateService.RequestCertificateAsync"/> will fail at
///   challenge validation — which is the honest expected outcome per the task brief.</item>
/// </list>
/// </summary>
public static class InternetAccessEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void MapInternetAccessEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/internet-access").WithTags("InternetAccess").RequireInternalKey();

        // GET /api/internet-access/info — LAN IP(s), local ports, persisted DDNS config +
        // last-known DDNS state, persisted ACME config + stored certificate (if any). Safe to
        // call; read-only. The wizard renders its sections from this single payload.
        group.MapGet("/info", (HttpContext ctx, IConfiguration config) =>
        {
            if (!CallerIdentity.Extract(ctx).IsSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            var dataPath = ResolveDataPath(config);

            return Results.Ok(new
            {
                lanIpAddresses = LanIpEnumerator.GetLanIPv4Addresses().Select(ip => ip.ToString()).ToList(),
                localPorts = new
                {
                    // The desktop "Node" front binds 127.0.0.1:5310 (http) / 5311 (https) by default;
                    // standalone/Docker uses 5300 (api http) / 5301 (api https). These are the
                    // well-known defaults surfaced as port-forwarding guidance — the actual bound
                    // port can differ per deployment, so the wizard also tells the admin to confirm.
                    frontHttp = 5310,
                    frontHttps = 5311,
                    apiHttp = 5300,
                    apiHttps = 5301,
                },
                ddns = ReadDdnsView(dataPath),
                acme = ReadAcmeView(dataPath),
            });
        });

        // POST /api/internet-access/ddns/config — persist the chosen provider + credentials +
        // IP-detection mode so a later "check now" can rebuild the provider without re-prompting.
        group.MapPost("/ddns/config", async (
            HttpContext ctx, IConfiguration config, DdnsConfigRequest req) =>
        {
            if (!CallerIdentity.Extract(ctx).IsSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(req.Provider)
                || !DdnsProviders.All.Contains(req.Provider, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Json(new ErrorResponse(
                    $"provider must be one of: {string.Join(", ", DdnsProviders.All)}"), statusCode: 400);
            }
            if (string.IsNullOrWhiteSpace(req.IpMode)
                || !IpModes.All.Contains(req.IpMode, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Json(new ErrorResponse(
                    $"ipMode must be one of: {string.Join(", ", IpModes.All)}"), statusCode: 400);
            }

            // Provider-specific required-field validation, mirroring each *Config's ctor invariants.
            var prov = req.Provider.ToLowerInvariant();
            if (prov == "duckdns" || prov == "desec")
            {
                if (string.IsNullOrWhiteSpace(req.Domain) || string.IsNullOrWhiteSpace(req.Token))
                    return Results.Json(new ErrorResponse("domain and token are required for this provider"), statusCode: 400);
            }
            else // cloudflare
            {
                if (string.IsNullOrWhiteSpace(req.ZoneId) || string.IsNullOrWhiteSpace(req.RecordId)
                    || string.IsNullOrWhiteSpace(req.ApiToken))
                    return Results.Json(new ErrorResponse("zoneId, recordId and apiToken are required for cloudflare"), statusCode: 400);
            }
            if (req.IpMode.Equals("static", StringComparison.OrdinalIgnoreCase)
                && (!IPAddress.TryParse(req.StaticIp, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork))
            {
                return Results.Json(new ErrorResponse("staticIp must be a valid IPv4 address when ipMode=static"), statusCode: 400);
            }

            var dataPath = ResolveDataPath(config);
            await SaveAsync(DdnsConfigPath(dataPath), req);
            return Results.Ok(ReadDdnsView(dataPath));
        });

        // POST /api/internet-access/ddns/check — rebuild the provider from the persisted config
        // and run one DdnsUpdater.CheckAndUpdateAsync cycle. Returns the raw result fields so the
        // wizard can show success / no-change / failure with the underlying message.
        group.MapPost("/ddns/check", async (
            HttpContext ctx, IConfiguration config, HttpClient http, ILoggerFactory loggerFactory) =>
        {
            if (!CallerIdentity.Extract(ctx).IsSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            var dataPath = ResolveDataPath(config);
            var cfg = await ReadAsync<DdnsConfigRequest>(DdnsConfigPath(dataPath));
            if (cfg == null)
                return Results.Json(new ErrorResponse(
                    "No DDNS provider configured yet. Save your provider settings first."), statusCode: 409);

            var logger = loggerFactory.CreateLogger<DdnsUpdater>();
            try
            {
                var ipProvider = BuildIpProvider(http, cfg);
                var ddnsProvider = BuildDdnsProvider(http, cfg, loggerFactory);
                var updater = new DdnsUpdater(ipProvider, ddnsProvider, dataPath, logger);
                var result = await updater.CheckAndUpdateAsync();
                return Results.Ok(new
                {
                    success = result.IsSuccess,
                    changed = result.Changed,
                    message = result.Message,
                    error = result.Exception?.Message,
                    // Re-read the on-disk state so the UI shows the freshest lastIp/lastUpdated.
                    state = ReadDdnsView(dataPath),
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DDNS check failed.");
                return Results.Json(new ErrorResponse("DDNS check failed: " + ex.Message), statusCode: 500);
            }
        });

        // POST /api/internet-access/acme/config — persist the domain + contact email + staging
        // toggle used by the next certificate request.
        group.MapPost("/acme/config", async (
            HttpContext ctx, IConfiguration config, AcmeConfigRequest req) =>
        {
            if (!CallerIdentity.Extract(ctx).IsSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            if (string.IsNullOrWhiteSpace(req.Domain))
                return Results.Json(new ErrorResponse("domain is required"), statusCode: 400);

            var dataPath = ResolveDataPath(config);
            await SaveAsync(AcmeConfigPath(dataPath), req);
            return Results.Ok(ReadAcmeView(dataPath));
        });

        // POST /api/internet-access/acme/request — issue (or re-issue) a certificate for the
        // configured domain. Builds AcmeOptions from the staging toggle and constructs the
        // service ad-hoc (it is not a fixed DI service). Real issuance requires the TLS-ALPN-01
        // challenge responder to be shared with the live TLS listener — see the class summary.
        group.MapPost("/acme/request", async (
            HttpContext ctx, IConfiguration config, ILoggerFactory loggerFactory,
            System.Threading.CancellationToken ct, AcmeRequestRequest? req) =>
        {
            if (!CallerIdentity.Extract(ctx).IsSuperadmin)
                return Results.Json(new ErrorResponse("Superadmin required"), statusCode: 403);

            var dataPath = ResolveDataPath(config);

            // Domain/staging may be supplied inline (ad-hoc request) or fall back to persisted config.
            AcmeConfigRequest? saved = await ReadAsync<AcmeConfigRequest>(AcmeConfigPath(dataPath));
            var domain = (req?.Domain ?? saved?.Domain ?? "").Trim();
            var useStaging = req?.UseStaging ?? saved?.UseStaging ?? true;
            var email = (req?.ContactsEmail ?? saved?.ContactsEmail ?? "").Trim();
            if (string.IsNullOrWhiteSpace(domain))
                return Results.Json(new ErrorResponse(
                    "No domain configured. Save ACME settings (or pass a domain) first."), statusCode: 409);

            if (string.IsNullOrWhiteSpace(email) && saved != null)
                email = (saved.ContactsEmail ?? "").Trim();

            var logger = loggerFactory.CreateLogger<AcmeCertificateService>();
            var options = new AcmeOptions
            {
                DirectoryUri = useStaging
                    ? AcmeDirectories.LetsEncryptStagingV2
                    : AcmeDirectories.LetsEncryptV2,
                ContactsEmail = email,
            };

            // Fresh responder per call. Correct for wiring isolation; see class summary. The trace
            // sink surfaces the service's step-by-step diagnostics in the response so the wizard can
            // show exactly where a (likely) validation failure happened.
            var traceLines = new List<string>();
            var responder = new TlsAlpnChallengeResponder();
            var service = new AcmeCertificateService(
                dataPath, options, responder,
                trace: msg => { traceLines.Add(msg); logger.LogInformation("{Trace}", msg); });

            try
            {
                var stored = await service.RequestCertificateAsync(domain, ct);
                return Results.Ok(new
                {
                    success = true,
                    message = $"Certificate issued for '{stored.Domain}', valid until {stored.NotAfter:u}.",
                    certificate = CertView(stored),
                    trace = traceLines,
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ACME certificate request for '{Domain}' failed.", domain);
                return Results.Json(new
                {
                    error = ex.Message,
                    trace = traceLines,
                    hint = "TLS-ALPN-01 issuance needs a real public domain with DNS pointing here " +
                           "and port 443 reachable by the CA. Until the front TLS listener shares the " +
                           "challenge responder, validation will fail — this is expected.",
                }, statusCode: 500);
            }
        });
    }

    // ──────────────────────────── provider construction ────────────────────────────

    private static IExternalIpProvider BuildIpProvider(HttpClient http, DdnsConfigRequest cfg) =>
        cfg.IpMode.Equals("static", StringComparison.OrdinalIgnoreCase) && IPAddress.TryParse(cfg.StaticIp, out var ip)
            ? new StaticExternalIpProvider(ip)
            : new UpnpExternalIpProvider(http);

    private static IDdnsProvider BuildDdnsProvider(HttpClient http, DdnsConfigRequest cfg, ILoggerFactory loggerFactory)
    {
        var prov = cfg.Provider.ToLowerInvariant();
        return prov switch
        {
            "duckdns" => new DuckDnsProvider(http,
                new DuckDnsConfig { Domain = cfg.Domain!, Token = cfg.Token! },
                loggerFactory.CreateLogger<DuckDnsProvider>()),
            "desec" => new DesecProvider(http,
                new DesecConfig { Domain = cfg.Domain!, Token = cfg.Token! },
                loggerFactory.CreateLogger<DesecProvider>()),
            "cloudflare" => new CloudflareProvider(http,
                new CloudflareConfig
                {
                    ZoneId = cfg.ZoneId!,
                    RecordId = cfg.RecordId!,
                    ApiToken = cfg.ApiToken!,
                    Domain = cfg.Domain,
                },
                loggerFactory.CreateLogger<CloudflareProvider>()),
            _ => throw new InvalidOperationException($"Unknown DDNS provider '{cfg.Provider}'.")
        };
    }

    // ──────────────────────────── read views for the UI ────────────────────────────

    private static object ReadDdnsView(string dataPath)
    {
        var cfg = ReadAsync<DdnsConfigRequest>(DdnsConfigPath(dataPath)).GetAwaiter().GetResult();
        DdnsState? state = null;
        var statePath = Path.Combine(dataPath, "ddns-state.json");
        if (File.Exists(statePath))
        {
            try { state = JsonSerializer.Deserialize<DdnsState>(File.ReadAllText(statePath), JsonOpts); }
            catch { /* corrupt state file — ignore */ }
        }
        return new
        {
            configured = cfg != null,
            provider = cfg?.Provider,
            domain = cfg?.Domain,
            ipMode = cfg?.IpMode,
            staticIp = cfg?.StaticIp,
            lastIp = state?.LastIp,
            lastUpdated = state?.LastUpdated,
        };
    }

    private static object ReadAcmeView(string dataPath)
    {
        var cfg = ReadAsync<AcmeConfigRequest>(AcmeConfigPath(dataPath)).GetAwaiter().GetResult();
        StoredCertificate? cert = null;
        var directoryUri = cfg?.UseStaging switch
        {
            false => AcmeDirectories.LetsEncryptV2,
            _ => AcmeDirectories.LetsEncryptStagingV2,
        };
        if (cfg is { Domain: not null } c)
        {
            // Construct a transient service ONLY to read stored cert metadata — no network calls.
            var reader = new AcmeCertificateService(dataPath,
                new AcmeOptions { DirectoryUri = directoryUri, ContactsEmail = c.ContactsEmail ?? "" },
                new TlsAlpnChallengeResponder());
            try { cert = reader.GetStoredCertificate(c.Domain.Trim()); }
            catch { /* ignore — metadata read is best-effort */ }
        }
        return new
        {
            configured = cfg != null,
            domain = cfg?.Domain,
            contactsEmail = cfg?.ContactsEmail,
            useStaging = cfg?.UseStaging ?? true,
            directoryUri,
            certificate = CertView(cert),
        };
    }

    private static object? CertView(StoredCertificate? c)
    {
        if (c == null) return null;
        var daysRemaining = (c.NotAfter - DateTime.UtcNow).TotalDays;
        return new
        {
            present = true,
            domain = c.Domain,
            notBefore = c.NotBefore,
            notAfter = c.NotAfter,
            issuedAt = c.IssuedAt,
            daysRemaining = Math.Round(daysRemaining, 1),
            needsRenewal = c.NeedsRenewal(30),
            issuedByStaging = c.DirectoryUri == AcmeDirectories.LetsEncryptStagingV2,
        };
    }

    // ──────────────────────────── persistence helpers ──────────────────────────────

    private static string InternetAccessDir(string dataPath) => Path.Combine(dataPath, "internet-access");
    private static string DdnsConfigPath(string dataPath) => Path.Combine(InternetAccessDir(dataPath), "ddns-config.json");
    private static string AcmeConfigPath(string dataPath) => Path.Combine(InternetAccessDir(dataPath), "acme-config.json");

    private static async Task<T?> ReadAsync<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path), JsonOpts); }
        catch { return null; }
    }

    private static async Task SaveAsync(string path, object value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(value, JsonOpts);
        // Atomic write so a crash mid-save can't leave a half-written config.
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private static string ResolveDataPath(IConfiguration config) =>
        config["BeeMemoryBank:DataPath"]
        ?? Environment.GetEnvironmentVariable("BMB_DATA_PATH")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
}

// ──────────────────────────── request DTOs ──────────────────────────────────────

public sealed class DdnsConfigRequest
{
    /// <summary>"duckdns" | "desec" | "cloudflare"</summary>
    public string Provider { get; set; } = "";
    /// <summary>DuckDNS/deSEC domain, or Cloudflare record name (optional for Cloudflare).</summary>
    public string? Domain { get; set; }
    /// <summary>DuckDNS/deSEC token.</summary>
    public string? Token { get; set; }
    /// <summary>Cloudflare zone id.</summary>
    public string? ZoneId { get; set; }
    /// <summary>Cloudflare DNS record id.</summary>
    public string? RecordId { get; set; }
    /// <summary>Cloudflare API token.</summary>
    public string? ApiToken { get; set; }
    /// <summary>"upnp" | "static"</summary>
    public string IpMode { get; set; } = "upnp";
    /// <summary>Required when IpMode == "static".</summary>
    public string? StaticIp { get; set; }
}

public sealed class AcmeConfigRequest
{
    /// <summary>Domain to issue the certificate for (e.g. mynode.duckdns.org).</summary>
    public string Domain { get; set; } = "";
    /// <summary>Optional contact email for the ACME account.</summary>
    public string? ContactsEmail { get; set; }
    /// <summary>True (default) = Let's Encrypt staging; false = production (rate-limited).</summary>
    public bool UseStaging { get; set; } = true;
}

/// <summary>Ad-hoc certificate request — overrides the persisted config for a single request.</summary>
public sealed class AcmeRequestRequest
{
    public string? Domain { get; set; }
    public string? ContactsEmail { get; set; }
    public bool? UseStaging { get; set; }
}

internal static class DdnsProviders
{
    public static readonly string[] All = ["duckdns", "desec", "cloudflare"];
}

internal static class IpModes
{
    public static readonly string[] All = ["upnp", "static"];
}

/// <summary>
/// Enumerates the machine's LAN IPv4 addresses for the wizard's port-forwarding guidance.
/// Mirrors the (private) filter in <c>LocalCaService.GetLanIPv4Addresses</c>: real UP NICs
/// only, excluding loopback/tunnel/virtual adapters so the admin isn't told to forward to a
/// Docker/Hyper-V/Hamachi address. Extracted here as a small public helper because
/// <c>LocalCaService</c>'s copy is private and this task may land before it.
/// </summary>
internal static class LanIpEnumerator
{
    public static IReadOnlyList<IPAddress> GetLanIPv4Addresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                var desc = ni.Description.ToLowerInvariant();
                var name = ni.Name.ToLowerInvariant();
                if (IsVirtual(desc) || IsVirtual(name)) continue;

                foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(unicast.Address))
                    {
                        addresses.Add(unicast.Address);
                    }
                }
            }
        }
        catch
        {
            // Fail-safe: return whatever was collected (possibly empty).
        }
        return addresses;
    }

    private static bool IsVirtual(string id) =>
        id.Contains("virtual") || id.Contains("vpn") || id.Contains("pseudo") ||
        id.Contains("docker") || id.Contains("hyper-v") || id.Contains("virtualbox") ||
        id.Contains("vmware") || id.Contains("loopback") || id.Contains("vethernet");
}
