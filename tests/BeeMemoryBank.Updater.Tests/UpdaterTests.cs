using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Updater;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Updater.Tests;

public class UpdaterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<MockHttpServer> _servers = [];

    public UpdaterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bmb_updater_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var server in _servers)
        {
            server.Dispose();
        }

        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best effort
            }
        }
    }

    private int GetRandomPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private MockHttpServer CreateMockServer(int port)
    {
        var server = new MockHttpServer(port);
        _servers.Add(server);
        return server;
    }

    private void CreateZipFile(string zipPath, Dictionary<string, string> files)
    {
        using var fileStream = new FileStream(zipPath, FileMode.Create);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var entry = archive.CreateEntry(file.Key);
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write(file.Value);
        }
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private (string json, string signature) BuildSignedManifest(
        string version, string artifactName, string sha256, long size, byte[] signingKey)
    {
        var manifest = new ReleasesManifest
        {
            SchemaVersion = 1,
            Channels = new ReleasesChannels
            {
                Stable = new ReleaseChannelInfo
                {
                    Version = version,
                    ProtocolVersion = 1,
                    Artifacts =
                    [
                        new ArtifactDescriptor { Name = artifactName, Sha256 = sha256, Size = size }
                    ]
                }
            }
        };
        var json = JsonSerializer.Serialize(manifest);
        var sig = Convert.ToBase64String(
            Ed25519Signer.Sign(signingKey, Encoding.UTF8.GetBytes(json)));
        return (json, sig);
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_SwitchesJunctionAndCleansUp()
    {
        // 1. Arrange
        string app1Dir = Path.Combine(_tempDir, "app-1.0.0");
        Directory.CreateDirectory(app1Dir);
        File.WriteAllText(Path.Combine(app1Dir, "version.txt"), "1.0.0");

        string currentPath = Path.Combine(_tempDir, "current");
        JunctionHelper.CreateJunction(currentPath, app1Dir);

        string updatesDir = Path.Combine(_tempDir, "updates");
        Directory.CreateDirectory(updatesDir);

        // Create Zip package
        string zipPath = Path.Combine(updatesDir, "release-1.1.0.zip");
        CreateZipFile(zipPath, new Dictionary<string, string>
        {
            { "version.txt", "1.1.0" },
            { "greet.txt", "Hello from v1.1.0!" }
        });

        byte[] zipBytes = File.ReadAllBytes(zipPath);
        string zipHash = Sha256Hex(zipBytes);

        // Generate keys & signed manifest
        var (pubKey, privKey) = Ed25519Signer.GenerateKeyPair();
        var (manifestJson, sigB64) = BuildSignedManifest("1.1.0", "release-1.1.0.zip", zipHash, zipBytes.Length, privKey);

        // Write request file
        var request = new UpdateRequest
        {
            TargetVersion = "1.1.0",
            ManifestJson = manifestJson,
            ManifestSignatureBase64 = sigB64
        };
        string requestPath = Path.Combine(updatesDir, "update.request");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request));

        // Start mock health check server (200 OK)
        int port = GetRandomPort();
        var server = CreateMockServer(port);
        server.Start(200);

        string healthUrl = $"http://127.0.0.1:{port}/health/";

        // 2. Act
        string[] args =
        [
            "--root", _tempDir,
            "--health-check-url", healthUrl,
            "--health-check-retries", "2",
            "--health-check-interval", "1",
            "--pubkey-0", Convert.ToBase64String(pubKey)
        ];

        int exitCode = await Program.Main(args);

        // 3. Assert
        exitCode.Should().Be(0);

        // Verify junction points to new app directory
        Directory.Exists(currentPath).Should().BeTrue();
        var targetInfo = new DirectoryInfo(currentPath);
        targetInfo.Attributes.HasFlag(FileAttributes.ReparsePoint).Should().BeTrue();
        
        string resolvedTarget = targetInfo.LinkTarget ?? "";
        resolvedTarget.Should().EndWith("app-1.1.0");

        // Verify contents of new app directory
        File.ReadAllText(Path.Combine(currentPath, "version.txt")).Should().Be("1.1.0");
        File.ReadAllText(Path.Combine(currentPath, "greet.txt")).Should().Be("Hello from v1.1.0!");

        // Verify update.request is deleted
        File.Exists(requestPath).Should().BeFalse();
    }

    [Fact]
    public async Task BadSignature_IsRejectedBeforeTouchingJunction()
    {
        // 1. Arrange
        string app1Dir = Path.Combine(_tempDir, "app-1.0.0");
        Directory.CreateDirectory(app1Dir);
        File.WriteAllText(Path.Combine(app1Dir, "version.txt"), "1.0.0");

        string currentPath = Path.Combine(_tempDir, "current");
        JunctionHelper.CreateJunction(currentPath, app1Dir);

        string updatesDir = Path.Combine(_tempDir, "updates");
        Directory.CreateDirectory(updatesDir);

        // Create Zip package
        string zipPath = Path.Combine(updatesDir, "release-1.1.0.zip");
        CreateZipFile(zipPath, new Dictionary<string, string> { { "version.txt", "1.1.0" } });

        byte[] zipBytes = File.ReadAllBytes(zipPath);
        string zipHash = Sha256Hex(zipBytes);

        // Generate keys & signed manifest, but sign with WRONG key
        var (correctPubKey, _) = Ed25519Signer.GenerateKeyPair();
        var (_, wrongPrivKey) = Ed25519Signer.GenerateKeyPair();
        var (manifestJson, sigB64) = BuildSignedManifest("1.1.0", "release-1.1.0.zip", zipHash, zipBytes.Length, wrongPrivKey);

        // Write request file
        var request = new UpdateRequest
        {
            TargetVersion = "1.1.0",
            ManifestJson = manifestJson,
            ManifestSignatureBase64 = sigB64
        };
        string requestPath = Path.Combine(updatesDir, "update.request");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request));

        // 2. Act
        string[] args =
        [
            "--root", _tempDir,
            "--pubkey-0", Convert.ToBase64String(correctPubKey)
        ];

        int exitCode = await Program.Main(args);

        // 3. Assert
        exitCode.Should().Be(2); // Signature verification failed code

        // Verify junction is NOT changed and still points to 1.0.0
        var targetInfo = new DirectoryInfo(currentPath);
        string resolvedTarget = targetInfo.LinkTarget ?? "";
        resolvedTarget.Should().EndWith("app-1.0.0");

        // Verify new app folder was not extracted
        Directory.Exists(Path.Combine(_tempDir, "app-1.1.0")).Should().BeFalse();

        // Verify update.request is NOT deleted
        File.Exists(requestPath).Should().BeTrue();
    }

    [Fact]
    public async Task HealthCheckFailure_RollsJunctionBack()
    {
        // 1. Arrange
        string app1Dir = Path.Combine(_tempDir, "app-1.0.0");
        Directory.CreateDirectory(app1Dir);
        File.WriteAllText(Path.Combine(app1Dir, "version.txt"), "1.0.0");

        string currentPath = Path.Combine(_tempDir, "current");
        JunctionHelper.CreateJunction(currentPath, app1Dir);

        string updatesDir = Path.Combine(_tempDir, "updates");
        Directory.CreateDirectory(updatesDir);

        // Create Zip package
        string zipPath = Path.Combine(updatesDir, "release-1.1.0.zip");
        CreateZipFile(zipPath, new Dictionary<string, string> { { "version.txt", "1.1.0" } });

        byte[] zipBytes = File.ReadAllBytes(zipPath);
        string zipHash = Sha256Hex(zipBytes);

        // Generate keys & signed manifest
        var (pubKey, privKey) = Ed25519Signer.GenerateKeyPair();
        var (manifestJson, sigB64) = BuildSignedManifest("1.1.0", "release-1.1.0.zip", zipHash, zipBytes.Length, privKey);

        // Write request file
        var request = new UpdateRequest
        {
            TargetVersion = "1.1.0",
            ManifestJson = manifestJson,
            ManifestSignatureBase64 = sigB64
        };
        string requestPath = Path.Combine(updatesDir, "update.request");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request));

        // Start mock health check server that returns 500 OK (Failure)
        int port = GetRandomPort();
        var server = CreateMockServer(port);
        server.Start(500);

        string healthUrl = $"http://127.0.0.1:{port}/health/";

        // 2. Act
        string[] args =
        [
            "--root", _tempDir,
            "--health-check-url", healthUrl,
            "--health-check-retries", "2",
            "--health-check-interval", "1",
            "--pubkey-0", Convert.ToBase64String(pubKey)
        ];

        int exitCode = await Program.Main(args);

        // 3. Assert
        exitCode.Should().Be(5); // Health check failure exit code

        // Verify junction is rolled back to app-1.0.0
        var targetInfo = new DirectoryInfo(currentPath);
        string resolvedTarget = targetInfo.LinkTarget ?? "";
        resolvedTarget.Should().EndWith("app-1.0.0");

        // Verify update.request is NOT deleted
        File.Exists(requestPath).Should().BeTrue();
    }
}

// ── Mock Http Server ─────────────────────────────────────────────────────────

public class MockHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private int _statusCode = 200;

    public MockHttpServer(int port)
    {
        _port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/health/");
    }

    public void Start(int statusCode)
    {
        _statusCode = statusCode;
        _listener.Start();
        Task.Run(async () =>
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    context.Response.StatusCode = _statusCode;
                    var buf = Encoding.UTF8.GetBytes("OK");
                    context.Response.ContentLength64 = buf.Length;
                    await context.Response.OutputStream.WriteAsync(buf);
                    context.Response.OutputStream.Close();
                }
            }
            catch
            {
                // Suppress exception on stop
            }
        });
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch { }
    }
}
