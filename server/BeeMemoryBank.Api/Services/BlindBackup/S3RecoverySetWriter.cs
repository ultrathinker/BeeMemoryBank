using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Api.Services.BlindBackup;

/// <summary>
/// Writes the recovery-set next to the repository (plan §6.8): for a folder repo that is the file
/// <c>{repo}.recovery-set.json</c> in the repo's parent directory; for an S3 repo it is the object
/// <c>{prefix}.recovery-set.json</c> — a SIBLING of the repository's key prefix, never under it,
/// so restic's prune/lock/index never sees it. The write happens after every backup and never
/// inside a snapshot.
/// </summary>
public sealed class RecoverySetWriter
{
    /// <summary>Writes next to the repository of <paramref name="s"/> — the job's own snapshot, not the file.</summary>
    public async Task WriteAsync(BlindBackupSettings s, string json, CancellationToken ct)
    {
        if (s.RepoType == BlindRepoType.Folder)
        {
            var repo = Path.GetFullPath(s.RepoFolder!);
            var file = repo + ".recovery-set.json";
            var dir = Path.GetDirectoryName(repo);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Atomic-ish: a restore reads this file exactly when everything else has already gone
            // wrong; a truncated copy of it is the one failure worth taking an extra rename to
            // avoid.
            var tmp = file + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, file, overwrite: true);
            return;
        }

        await S3Put.PutAsync(
            new Uri(s.S3Endpoint!.TrimEnd('/')),
            s.S3Bucket!, s.S3Prefix! + ".recovery-set.json",
            Encoding.UTF8.GetBytes(json),
            s.S3AccessKey!, s.S3SecretKey!, s.S3Region ?? "us-east-1", ct);
    }
}

/// <summary>
/// The one S3 call the blind node needs outside restic itself: a SigV4-signed PUT of the
/// recovery-set object. Path-style addressing, payload signed with its SHA-256 (the safe default;
/// UNSIGNED-PAYLOAD is only needed for TLS-terminating proxies). Kept to a single verb on
/// purpose — the moment this grows a second one, it should become a real SDK client instead.
/// </summary>
public static class S3Put
{
    public static async Task PutAsync(Uri endpoint, string bucket, string key, byte[] content,
        string accessKey, string secretKey, string region, CancellationToken ct)
    {
        // String concatenation, not Uri-combining: an S3 endpoint may itself carry a path prefix
        // (https://host/minio), and a relative Uri against a prefix-less base would drop it.
        var url = new Uri(endpoint.ToString().TrimEnd('/')
            + "/" + Uri.EscapeDataString(bucket) + "/" + EscapeKey(key));
        var host = url.Authority;
        var path = url.AbsolutePath;

        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'");
        var dateStamp = now.ToString("yyyyMMdd");

        var canonicalRequest = BuildCanonicalRequest("PUT", path, host, sha, amzDate);
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var scope = $"{dateStamp}/{region}/s3/aws4_request";
        var stringToSign = BuildStringToSign(amzDate, scope, canonicalRequest);

        var signing = Hmac(Hmac(Hmac(Hmac("AWS4" + secretKey, dateStamp), region), "s3"), "aws4_request");
        var signature = Convert.ToHexString(Hmac(signing, stringToSign)).ToLowerInvariant();

        using var http = new HttpClient(new SocketsHttpHandler());
        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(content),
        };
        req.Headers.Host = host;
        req.Headers.Add("x-amz-date", amzDate);
        req.Headers.Add("x-amz-content-sha256", sha);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "AWS4-HMAC-SHA256",
            $"Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"recovery-set S3 PUT to {url} failed: {(int)resp.StatusCode} {Truncate(body, 300)}");
        }
    }

    /// <summary>
    /// An object key as S3 expects it in the path: every segment URI-encoded, the '/' between
    /// segments kept. A prefix like "bmb/blind" encoded whole would travel as "bmb%2Fblind" — a
    /// different key from the one restic's own client writes under, and a signature mismatch on
    /// hosts that normalize the path before verifying.
    /// </summary>
    internal static string EscapeKey(string key) =>
        string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// Canonical request (SigV4): method, URI (URL-encoded by the caller), no query, the three
    /// signed headers sorted, and the payload hash both signed and sent. Internal so a test can
    /// pin the exact string — a signature that signs the wrong bytes verifies perfectly against
    /// itself and fails only against a real S3 host.
    /// </summary>
    internal static string BuildCanonicalRequest(string method, string path, string host, string payloadSha256, string amzDate) =>
        method + "\n" + path + "\n\n"
        + "host:" + host + "\n"
        + "x-amz-content-sha256:" + payloadSha256 + "\n"
        + "x-amz-date:" + amzDate + "\n"
        + "\n"
        + "host;x-amz-content-sha256;x-amz-date"
        + "\n" + payloadSha256;

    internal static string BuildStringToSign(string amzDate, string scope, string canonicalRequest) =>
        $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Sha256Hex(canonicalRequest)}";

    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static byte[] Hmac(string key, string data) => Hmac(Encoding.UTF8.GetBytes(key), data);

    private static byte[] Hmac(byte[] key, string data) =>
        new HMACSHA256(key).ComputeHash(Encoding.UTF8.GetBytes(data));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
