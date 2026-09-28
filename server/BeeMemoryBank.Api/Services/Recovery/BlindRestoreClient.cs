using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Recovery;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// The new device's side of a restore (plan 6.7, 6.8): from a blind node over the network (address +
/// one-time code + master password), or from a backup with the blind node switched off — a folder with
/// a database copy, or a restic repository — using the recovery set that lies next to it.
/// </summary>
public class BlindRestoreClient(
    IHttpClientFactory httpFactory,
    RecoveryRestoreService restore,
    IEnumerable<IBackupFileRestoreSource> fileSources,
    ILogger<BlindRestoreClient> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How long the claim may take, all tries together. It runs after the restore committed, on its own
    /// token: the user's cancel stops only work before the commit — after it, this node is initialized and
    /// "cancelled" would be false.
    /// </summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(30);
    private const int ClaimTries = 3;

    /// <summary>
    /// Restore from a blind node, then claim it: this device becomes its superadmin. HTTPS only, pinned to
    /// <paramref name="pin"/> (from the code the user typed or scanned), no redirects. What the blind node
    /// sends — package, signature, its key, whitelist, events — is only as good as that pin; whether the
    /// restored data is CONFIRMED is decided by the anchor under the master DEK alone.
    /// </summary>
    /// <param name="code">The restore code (<see cref="BlindRestoreCode"/>): the blind node's identity, pin and secret.</param>
    /// <param name="address">Overrides the code's address when given.</param>
    /// <param name="pin">Optional; when given it must be the code's pin.</param>
    public async Task<RestoreResult> RestoreFromBlindNodeAsync(string? address, string code, string? pin, RestoreIdentity who,
        CancellationToken ct = default, RestoreBoxPolicy boxes = RestoreBoxPolicy.Default)
    {
        BlindRestoreCode restoreCode;
        try { restoreCode = BlindRestoreCode.Parse(code); }
        catch (FormatException ex) { throw new ArgumentException(ex.Message, ex); }
        address = (string.IsNullOrWhiteSpace(address) ? restoreCode.Address : address)?.Trim().TrimEnd('/')
            ?? throw new ArgumentException("The restore code carries no address: enter the blind node's address.");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A blind node is reached over HTTPS only.");
        if (!string.IsNullOrWhiteSpace(pin) && !(RestoreTlsPin.IsWellFormed(pin) && Spki.Equal(pin.Trim(), restoreCode.TlsSpki)))
            throw new ArgumentException("The certificate pin does not match the restore code.");
        pin = restoreCode.TlsSpki;
        code = restoreCode.Secret;
        // The sync client: SpkiPinRegistry's handler — the key in the request's own pin, HTTPS only, no
        // redirects — the same one the PC adds a blind node through. Long enough for a whole vault.
        var http = httpFactory.CreateClient(SyncScheduler.HttpClientName);
        http.Timeout = TimeSpan.FromMinutes(30);
        var package = Path.Combine(Path.GetTempPath(), $"bmb-blind-restore-{Guid.NewGuid():N}.tar.gz");
        try
        {
            using var packageReq = Pinned(HttpMethod.Get, $"{address}/api/blind/restore/package", pin);
            packageReq.Headers.Add(BlindRestoreEndpoints.RestoreCodeHeader, code);
            using var packageResp = await http.SendAsync(packageReq, HttpCompletionOption.ResponseHeadersRead, ct);
            RefuseRedirect(packageResp);
            if (packageResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException("The blind node refused the restore code (wrong, used or expired).");
            packageResp.EnsureSuccessStatusCode();
            await using (var fs = File.Create(package))
                await packageResp.Content.CopyToAsync(fs, ct);

            var signature = Convert.FromBase64String(Header(packageResp, "X-BMB-Snapshot-Signature"));
            var producer = Guid.Parse(Header(packageResp, "X-BMB-Snapshot-Producer"));
            var producerKey = Convert.FromBase64String(Header(packageResp, "X-BMB-Snapshot-Producer-Key"));
            // The key the package will be checked against comes from the code, not from this response: a pinned
            // endpoint answering with any other identity is refused before its package is looked at.
            if (producer != restoreCode.NodeId || !restoreCode.IsKeyOf(producerKey))
                throw new InvalidDataException("The blind node answered with another identity than the restore code names; refusing it.");

            using var eventsReq = Pinned(HttpMethod.Get, $"{address}/api/blind/restore/events", pin);
            eventsReq.Headers.Add(BlindRestoreEndpoints.RestoreCodeHeader, code);
            using var eventsResp = await http.SendAsync(eventsReq, ct);
            RefuseRedirect(eventsResp);
            eventsResp.EnsureSuccessStatusCode();
            var events = await eventsResp.Content.ReadFromJsonAsync<BlindRestoreEvents>(JsonOpts, ct) ?? new BlindRestoreEvents([]);

            // Whitelist and positions come from the package's signed blind-manifest.json; the blind node's
            // row gets the pin the user typed, so this device keeps dialing it pinned.
            var result = await restore.RestoreFromPackageAsync(package, signature, producerKey, who,
                new RestoreBlindPeer(producer, "Blind node", producerKey, address, CpSeq: 0, TlsSpki: pin),
                events.Events, ct, boxes);

            // Committed. From here on `ct` is not used.
            return result with { Claim = await ClaimAsync(http, address, code, pin, result, who) };
        }
        finally
        {
            try { if (File.Exists(package)) File.Delete(package); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Restore from a backup with no blind node needed (plan 6.8). <paramref name="path"/> is either a
    /// folder holding a database copy (<c>beememorybank.db</c>) with a <c>*.recovery-set.json</c>, or a
    /// restic repository with <c>&lt;repo&gt;.recovery-set.json</c> beside it — the restic password is the
    /// sealed secret <c>restic:*</c> in that set, opened with the recovered key.
    /// </summary>
    public async Task<RestoreResult> RestoreFromBackupAsync(string path, RestoreIdentity who, CancellationToken ct = default,
        RestoreBoxPolicy boxes = RestoreBoxPolicy.Default)
    {
        path = Path.GetFullPath(path.Trim());
        // A one-file backup that carries its own recovery material (an Android blind node's).
        if (File.Exists(path) && fileSources.FirstOrDefault(s => s.Handles(path)) is { } source)
            return await source.RestoreAsync(path, who, ct, boxes);
        if (Directory.Exists(Path.Combine(path, "keys")) && Directory.Exists(Path.Combine(path, "snapshots")))
            return await RestoreFromResticAsync(path, who, ct, boxes);

        var setFile = Directory.EnumerateFiles(path, "*.recovery-set.json").FirstOrDefault()
            ?? throw new FileNotFoundException("No *.recovery-set.json in the backup folder.");
        var db = Path.Combine(path, BlindPackageFile.DbFileName);
        if (!File.Exists(db)) throw new FileNotFoundException("No database copy in the backup folder.", db);
        var set = RecoverySet.Parse(await File.ReadAllTextAsync(setFile, ct));
        return await restore.RestoreFromDatabaseAsync(db, set, who, ct, boxes);
    }

    private async Task<RestoreResult> RestoreFromResticAsync(string repo, RestoreIdentity who, CancellationToken ct, RestoreBoxPolicy boxes)
    {
        // Next to the repository, never inside it (CONTRACTS §2, plan 6.8).
        var setFile = repo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".recovery-set.json";
        if (!File.Exists(setFile)) throw new FileNotFoundException("The recovery set next to the restic repository is missing.", setFile);
        var set = RecoverySet.Parse(await File.ReadAllTextAsync(setFile, ct));

        using var keys = await RecoveryRestoreService.ResolveKeysAsync(set, who.Password, boxes, ct);
        var candidates = new[] { keys.Current }.Concat(keys.Retired.Values).ToList();
        string? resticPassword = null;
        foreach (var secret in set.SealedSecrets.Where(s => s.Name.StartsWith("restic:", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var dek in candidates)
            {
                var opened = SealedSecretCrypto.TryOpen(secret.Name, TryBase64(secret.Wrapped), TryBase64(secret.Iv), dek);
                if (opened == null) continue;
                resticPassword = System.Text.Encoding.UTF8.GetString(opened);
                Array.Clear(opened);
                break;
            }
            if (resticPassword != null) break;
        }
        if (resticPassword == null) throw new InvalidOperationException("No sealed restic password opens under the recovered key.");

        var workDir = Path.Combine(Path.GetTempPath(), $"bmb-restic-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var listing = await RunResticAsync(repo, resticPassword, ct, "ls", "latest", "--json");
            var dbInRepo = listing.Split('\n')
                .Select(l => { try { return JsonDocument.Parse(l).RootElement; } catch (JsonException) { return default; } })
                .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("path", out _))
                .Select(e => e.GetProperty("path").GetString())
                .FirstOrDefault(p => p != null && p.EndsWith("/" + BlindPackageFile.DbFileName, StringComparison.Ordinal))
                ?? throw new InvalidDataException("The latest restic snapshot holds no database copy.");
            await RunResticAsync(repo, resticPassword, ct, "restore", "latest", "--target", workDir, "--include", dbInRepo);
            var db = Directory.EnumerateFiles(workDir, BlindPackageFile.DbFileName, SearchOption.AllDirectories).First();
            return await restore.RestoreFromDatabaseAsync(db, set, who, ct, boxes);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<string> RunResticAsync(string repo, string password, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("restic") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add(repo);
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Through the environment, never the command line where other processes could read it.
        psi.Environment["RESTIC_PASSWORD"] = password;
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("restic could not be started.");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0) throw new InvalidOperationException($"restic {args[0]} failed: {await stderr}");
        return await stdout;
    }

    // The handler follows none, but a 3xx is still an answer — refuse it by name rather than let it look
    // like any other failure.
    /// <summary>
    /// The claim after the commit: a few tries within <see cref="ClaimTimeout"/>, on a token of its own.
    /// Never throws — the restore is done whatever the blind node answers.
    /// </summary>
    private async Task<string> ClaimAsync(HttpClient http, string address, string code, string pin, RestoreResult result, RestoreIdentity who)
    {
        using var cts = new CancellationTokenSource(ClaimTimeout);
        for (var attempt = 1; attempt <= ClaimTries; attempt++)
        {
            try
            {
                using var claimReq = Pinned(HttpMethod.Post, $"{address}/api/blind/claim", pin);
                claimReq.Content = JsonContent.Create(new BlindClaimRequest(result.NodeId, who.DisplayName,
                    Convert.ToBase64String(result.PublicKey), null), options: JsonOpts);
                claimReq.Headers.Add(BlindRestoreEndpoints.RestoreCodeHeader, code);
                using var claimResp = await http.SendAsync(claimReq, cts.Token);
                if (claimResp.IsSuccessStatusCode) return "complete";
                if ((int)claimResp.StatusCode < 500)
                {
                    // A redirect, or the code refused (used, expired): trying again changes nothing.
                    logger.LogWarning("Restore finished but the blind node refused the claim ({Status}); issue a new code and pair again",
                        (int)claimResp.StatusCode);
                    return "refused";
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                if (cts.IsCancellationRequested) break;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(attempt), cts.Token); }
            catch (OperationCanceledException) { break; }
        }
        logger.LogWarning("Restore finished but the claim on the blind node got no answer; issue a new code and pair again");
        return "pending";
    }

    private static HttpRequestMessage Pinned(HttpMethod method, string url, string pin)
    {
        var req = new HttpRequestMessage(method, url);
        req.Options.Set(SpkiPinRegistry.ExplicitPin, pin);
        return req;
    }

    private static void RefuseRedirect(HttpResponseMessage resp)
    {
        if ((int)resp.StatusCode is >= 300 and < 400)
            throw new InvalidDataException("The blind node answered with a redirect; a restore follows none.");
    }

    private static string Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var values) ? values.First()
            : throw new InvalidDataException($"The blind node's reply lacks {name}.");

    private static byte[]? TryBase64(string? value)
    {
        try { return value == null ? null : Convert.FromBase64String(value); }
        catch (FormatException) { return null; }
    }
}
