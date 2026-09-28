using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// BMB-42 for <c>bmb join</c>: the host it joined through is recorded as a superadmin, inherited peers
/// keep the authority the host reports (the CLI used to drop it, leaving every other superadmin of the
/// mesh a plain peer on this node only), and a blind node is never a superadmin. The host is a
/// one-shot local HTTP listener answering <c>/api/join</c> the way the real endpoint does.
/// </summary>
public class JoinCommandAuthorityTests : IDisposable
{
    private const string Password = "cliJoinAuthorityPassword";

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "bmb_cli_join_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Join_RecordsHostAsSuperadmin_KeepsInheritedAuthority_NeverABlindSuperadmin()
    {
        var hostId = Guid.NewGuid();
        var superPeer = Guid.NewGuid();
        var plainPeer = Guid.NewGuid();
        var blindPeer = BlindNodeId.NewId();

        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var serve = ServeJoinOnceAsync(listener, JoinResponseJson(hostId,
            (superPeer, true), (plainPeer, false), (blindPeer, true)));

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, $"http://localhost:{port}", Password, "CliJoiner", output: output);
        await serve;
        rc.Should().Be(0, output.ToString());

        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        (await rows.GetByNodeIdAsync(hostId))!.IsSuperadmin.Should().BeTrue("the host proved the master password");
        (await rows.GetByNodeIdAsync(superPeer))!.IsSuperadmin.Should().BeTrue("the host reports it as a superadmin");
        (await rows.GetByNodeIdAsync(plainPeer))!.IsSuperadmin.Should().BeFalse("the host reports it as a plain peer");
        (await rows.GetByNodeIdAsync(blindPeer))!.IsSuperadmin.Should().BeFalse(
            "a blind node is never a superadmin, whatever the host's row says");
    }

    private static string JoinResponseJson(Guid hostId, params (Guid NodeId, bool IsSuperadmin)[] peers)
    {
        var dek = MasterKeyManager.GenerateMasterDek();
        var salt = KeyDerivation.GenerateSalt();
        var (encDek, iv) = MasterKeyManager.WrapMasterDek(dek, KeyDerivation.DeriveKek(Password, salt));
        string Key() => Convert.ToBase64String(Ed25519Signer.GenerateKeyPair().publicKey);

        return JsonSerializer.Serialize(new
        {
            remoteNode = new { nodeId = hostId, displayName = "Host", ed25519PublicKeyB64 = Key(), protocolVersion = 2 },
            keySlot = new
            {
                encryptedMasterDekB64 = Convert.ToBase64String(encDek),
                ivB64 = Convert.ToBase64String(iv),
                saltB64 = Convert.ToBase64String(salt),
                argonMemory = CryptoConstants.DefaultArgonMemory,
                argonIterations = CryptoConstants.DefaultArgonIterations,
                argonParallelism = CryptoConstants.DefaultArgonParallelism
            },
            whitelist = peers.Select(p => new
            {
                nodeId = p.NodeId,
                displayName = p.NodeId.ToString("N")[..8],
                ed25519PublicKeyB64 = Key(),
                apiAddress = (string?)null,
                isSuperadmin = p.IsSuperadmin
            })
        });
    }

    private static async Task ServeJoinOnceAsync(HttpListener listener, string body)
    {
        var ctx = await listener.GetContextAsync();
        ctx.Request.Url!.AbsolutePath.Should().Be("/api/join");
        using (var reader = new StreamReader(ctx.Request.InputStream)) await reader.ReadToEndAsync();
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
