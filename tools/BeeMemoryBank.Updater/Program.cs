using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Updater;

public sealed class UpdateRequest
{
    [JsonPropertyName("targetVersion")]
    public string TargetVersion { get; set; } = "";

    [JsonPropertyName("manifestJson")]
    public string ManifestJson { get; set; } = "";

    [JsonPropertyName("manifestSignatureBase64")]
    public string ManifestSignatureBase64 { get; set; } = "";
}

public sealed class ReleasesManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("channels")]
    public ReleasesChannels Channels { get; set; } = new();
}

public sealed class ReleasesChannels
{
    [JsonPropertyName("stable")]
    public ReleaseChannelInfo Stable { get; set; } = new();
}

public sealed class ReleaseChannelInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("artifacts")]
    public System.Collections.Generic.List<ArtifactDescriptor> Artifacts { get; set; } = [];
}

public sealed class ArtifactDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public static class JunctionHelper
{
    public static void CreateJunction(string junctionPath, string targetPath)
    {
        if (Directory.Exists(junctionPath))
        {
            Directory.Delete(junctionPath);
        }

        if (OperatingSystem.IsWindows())
        {
            // Run mklink /J via cmd.exe
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit();
            if (process?.ExitCode != 0)
            {
                var err = process?.StandardError.ReadToEnd();
                throw new IOException($"Failed to create junction via mklink: {err}");
            }
        }
        else
        {
            Directory.CreateSymbolicLink(junctionPath, targetPath);
        }
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? root = null;
        string healthCheckUrl = "http://localhost:5000/health";
        int healthCheckRetries = 5;
        int healthCheckIntervalSeconds = 2;
        string? pubKey0Base64 = null;
        string? pubKey1Base64 = null;
        string? artifactSourceDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("--root requires a value"); return 1; }
                    root = args[++i];
                    break;
                case "--health-check-url":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("--health-check-url requires a value"); return 1; }
                    healthCheckUrl = args[++i];
                    break;
                case "--health-check-retries":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("--health-check-retries requires a value"); return 1; }
                    if (!int.TryParse(args[++i], out healthCheckRetries)) { Console.Error.WriteLine("Invalid retries count"); return 1; }
                    break;
                case "--health-check-interval":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("--health-check-interval requires a value"); return 1; }
                    if (!int.TryParse(args[++i], out healthCheckIntervalSeconds)) { Console.Error.WriteLine("Invalid interval"); return 1; }
                    break;
                case "--pubkey-0":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("--pubkey-0 requires a value"); return 1; }
                    pubKey0Base64 = args[++i];
                    break;
                case "--pubkey-1":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("--pubkey-1 requires a value"); return 1; }
                    pubKey1Base64 = args[++i];
                    break;
                case "--artifact-source-dir":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("--artifact-source-dir requires a value"); return 1; }
                    artifactSourceDir = args[++i];
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintHelp();
                    return 1;
            }
        }

        if (string.IsNullOrEmpty(root))
        {
            Console.Error.WriteLine("Error: --root directory is required.");
            PrintHelp();
            return 1;
        }

        byte[][] publicKeys =
        [
            new byte[32],
            new byte[32]
        ];

        if (!string.IsNullOrEmpty(pubKey0Base64))
        {
            try
            {
                publicKeys[0] = Convert.FromBase64String(pubKey0Base64);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error decoding pubkey-0: {ex.Message}");
                return 1;
            }
        }
        if (!string.IsNullOrEmpty(pubKey1Base64))
        {
            try
            {
                publicKeys[1] = Convert.FromBase64String(pubKey1Base64);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error decoding pubkey-1: {ex.Message}");
                return 1;
            }
        }

        string updateRequestPath = Path.Combine(root, "updates", "update.request");
        if (!File.Exists(updateRequestPath))
        {
            Console.WriteLine("No update request file found at: " + updateRequestPath);
            return 0; // Nothing to do, exit successfully
        }

        Console.WriteLine($"Reading update request from {updateRequestPath}...");
        UpdateRequest? request;
        try
        {
            string requestJson = File.ReadAllText(updateRequestPath);
            request = JsonSerializer.Deserialize<UpdateRequest>(requestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read or parse update request: {ex.Message}");
            return 10;
        }

        if (request == null || string.IsNullOrEmpty(request.TargetVersion) || string.IsNullOrEmpty(request.ManifestJson) || string.IsNullOrEmpty(request.ManifestSignatureBase64))
        {
            Console.Error.WriteLine("Error: update.request is missing required fields.");
            return 11;
        }

        byte[] manifestBytes = Encoding.UTF8.GetBytes(request.ManifestJson);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(request.ManifestSignatureBase64);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error decoding manifest signature from Base64: {ex.Message}");
            return 12;
        }

        bool sigValid = false;
        try
        {
            sigValid = publicKeys.Any(pk => Ed25519Signer.Verify(pk, manifestBytes, signature));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Signature verification encountered an error: {ex.Message}");
        }

        if (!sigValid)
        {
            Console.Error.WriteLine("Manifest signature verification failed — not signed by any trusted release key.");
            return 2; // Exact code or standard error
        }
        Console.WriteLine("Manifest signature verified successfully.");

        ReleasesManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ReleasesManifest>(request.ManifestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Manifest JSON is malformed: {ex.Message}");
            return 13;
        }

        if (manifest?.Channels?.Stable == null)
        {
            Console.Error.WriteLine("Manifest stable channel is missing.");
            return 14;
        }

        var stableVersion = manifest.Channels.Stable.Version;
        if (stableVersion != request.TargetVersion)
        {
            Console.Error.WriteLine($"Target version '{request.TargetVersion}' does not match manifest version '{stableVersion}'.");
            return 15;
        }

        var artifacts = manifest.Channels.Stable.Artifacts;
        if (artifacts == null || artifacts.Count == 0)
        {
            Console.Error.WriteLine("Manifest has no artifacts to download.");
            return 16;
        }

        var descriptor = artifacts[0];
        string sourceDir = artifactSourceDir ?? Path.Combine(root, "updates");
        string artifactPath = Path.Combine(sourceDir, descriptor.Name);
        if (!File.Exists(artifactPath))
        {
            Console.Error.WriteLine($"Artifact file not found at: {artifactPath}");
            return 17;
        }

        Console.WriteLine($"Verifying SHA-256 hash for {descriptor.Name}...");
        byte[] artifactBytes;
        try
        {
            artifactBytes = File.ReadAllBytes(artifactPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read artifact: {ex.Message}");
            return 18;
        }

        string actualHash = Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant();
        string expectedHash = descriptor.Sha256.ToLowerInvariant();
        if (actualHash != expectedHash)
        {
            Console.Error.WriteLine($"Artifact SHA-256 mismatch for '{descriptor.Name}': expected {expectedHash}, got {actualHash}. Refusing to apply.");
            return 4;
        }
        Console.WriteLine("Artifact SHA-256 hash verified successfully.");

        string targetVersionDir = Path.Combine(root, $"app-{stableVersion}");
        Console.WriteLine($"Extracting artifact to target directory: {targetVersionDir}");
        try
        {
            if (Directory.Exists(targetVersionDir))
            {
                Directory.Delete(targetVersionDir, recursive: true);
            }
            Directory.CreateDirectory(targetVersionDir);
            ZipFile.ExtractToDirectory(artifactPath, targetVersionDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to deploy/extract artifact: {ex.Message}");
            return 19;
        }

        string currentPath = Path.Combine(root, "current");
        string? previousAppFolder = null;
        try
        {
            var currentInfo = new DirectoryInfo(currentPath);
            if (currentInfo.Exists && (currentInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                previousAppFolder = currentInfo.LinkTarget;
                if (!string.IsNullOrEmpty(previousAppFolder) && !Path.IsPathRooted(previousAppFolder))
                {
                    previousAppFolder = Path.GetFullPath(Path.Combine(root, previousAppFolder));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to resolve previous junction target: {ex.Message}");
        }

        Console.WriteLine($"Updating junction 'current' -> '{targetVersionDir}'");
        try
        {
            JunctionHelper.CreateJunction(currentPath, targetVersionDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to switch junction to new version: {ex.Message}");
            return 20;
        }

        Console.WriteLine($"Running health check against: {healthCheckUrl}");
        bool healthCheckPassed = false;
        using (var httpClient = new HttpClient())
        {
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            for (int attempt = 1; attempt <= healthCheckRetries; attempt++)
            {
                try
                {
                    var response = await httpClient.GetAsync(healthCheckUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        healthCheckPassed = true;
                        Console.WriteLine($"Health check attempt {attempt}/{healthCheckRetries} succeeded!");
                        break;
                    }
                    Console.WriteLine($"Health check attempt {attempt}/{healthCheckRetries} returned non-success code: {response.StatusCode}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Health check attempt {attempt}/{healthCheckRetries} failed: {ex.Message}");
                }

                if (attempt < healthCheckRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(healthCheckIntervalSeconds));
                }
            }
        }

        if (!healthCheckPassed)
        {
            Console.Error.WriteLine("Health check failed. Initiating rollback...");
            if (!string.IsNullOrEmpty(previousAppFolder) && Directory.Exists(previousAppFolder))
            {
                try
                {
                    JunctionHelper.CreateJunction(currentPath, previousAppFolder);
                    Console.WriteLine($"Rollback successful: junction reverted to {previousAppFolder}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Critical error: Failed to rollback junction to {previousAppFolder}: {ex.Message}");
                }
            }
            else
            {
                Console.Error.WriteLine("No previous version found or folder does not exist. Cannot rollback.");
            }
            return 5; // Exit code 5 for health check failure
        }

        Console.WriteLine("Deleting update request file...");
        try
        {
            File.Delete(updateRequestPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to delete update request file: {ex.Message}");
        }

        Console.WriteLine("Update complete successfully!");
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"bmb-updater — BeeMemoryBank service updater tool

Usage:
  bmb-updater --root <dir> [options]

Options:
  --root <dir>                   Root install directory (required)
  --health-check-url <url>       Health check URL (default: http://localhost:5000/health)
  --health-check-retries <n>     Number of health check retries (default: 5)
  --health-check-interval <sec>  Delay between retries in seconds (default: 2)
  --pubkey-0 <base64>            Base64 encoded release public key slot 0 override
  --pubkey-1 <base64>            Base64 encoded release public key slot 1 override
  --artifact-source-dir <dir>    Directory containing the artifact to extract (default: <root>/updates)
");
    }
}
