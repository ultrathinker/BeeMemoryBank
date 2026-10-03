using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>The blind node's restore routes (plan 6.7): who may call them and what a code allows.</summary>
public class BlindRestoreEndpointsTests : IAsyncLifetime
{
    private readonly RecoveryTestFactory _blind = new(blind: true);
    private HttpClient _console = null!; // internal key: the blind node's own console
    private HttpClient _public = null!;  // a stranger on the network

    public async Task InitializeAsync()
    {
        await _blind.InitializeNodeAsync();
        _console = _blind.CreateClient();
        _public = _blind.Server.CreateClient();
    }

    public Task DisposeAsync()
    {
        _console.Dispose();
        _public.Dispose();
        _blind.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Issues a restore code and returns its one-time secret (what the restore routes take).</summary>
    private async Task<string> IssueAsync() => (await IssueCodeAsync()).Secret;

    private async Task<BlindRestoreCode> IssueCodeAsync()
    {
        var resp = await _console.PostAsync("/api/blind/restore-code", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return BlindRestoreCode.Parse((await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheCode_NamesThisBlindNode_ItsKey_Address_AndTlsPin()
    {
        var code = await IssueCodeAsync();

        var identity = (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        code.NodeId.Should().Be(identity.NodeId);
        code.IsKeyOf(identity.Ed25519PublicKey).Should().BeTrue();
        code.Address.Should().Be(BlindNodeFactory.PublicAddress);
        code.TlsSpki.Should().Be(_blind.Services.GetRequiredService<BeeMemoryBank.Api.Services.BlindTlsIdentity>().Spki);
    }

    private Task<HttpResponseMessage> PackageAsync(string? code)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/blind/restore/package");
        if (code != null) req.Headers.Add("X-Restore-Code", code);
        return _public.SendAsync(req);
    }

    private Task<HttpResponseMessage> ClaimAsync(string code, Guid nodeId, string publicKeyB64)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/blind/claim")
        {
            Content = JsonContent.Create(new { nodeId, displayName = "New PC", publicKeyB64 })
        };
        req.Headers.Add("X-Restore-Code", code);
        return _public.SendAsync(req);
    }

    private static string NewKey() => Convert.ToBase64String(Ed25519Signer.GenerateKeyPair().publicKey);

    [Fact]
    public async Task IssuingACode_NeedsTheConsole()
    {
        (await _public.PostAsync("/api/blind/restore-code", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Code_IsFormattedAndOnlyItsHashIsStored()
    {
        var code = await IssueAsync();

        code.Should().MatchRegex("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$");
        using var conn = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        var stored = await conn.QuerySingleAsync<string>("SELECT code_hash FROM tbl_blind_restore_code");
        stored.Should().NotContain(code.Replace("-", "")).And.MatchRegex("^[0-9a-f]{64}$");
        var log = await _console.GetFromJsonAsync<JsonElement>("/api/blind/restore-codes");
        log.GetArrayLength().Should().Be(1);
        log.ToString().Should().NotContain(code);
    }

    [Fact]
    public async Task Package_WithoutOrWithAWrongCode_IsRefused()
    {
        await IssueAsync();

        (await PackageAsync(null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PackageAsync("AAAA-BBBB-CCCC-DDDD")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Package_WithTheCode_IsServedSigned_AndTheCodeStaysUsableUntilTheClaim()
    {
        var code = await IssueAsync();

        var first = await PackageAsync(code.ToLowerInvariant().Replace("-", " ")); // case and separators do not matter
        var second = await PackageAsync(code);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.GetValues("X-BMB-Snapshot-Signature").Single().Should().NotBeEmpty();
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExpiredCode_IsRefused()
    {
        var code = await IssueAsync();
        using (var conn = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_blind_restore_code SET expires_at = @T", new { T = DateTime.UtcNow.AddMinutes(-1).ToString("O") });

        (await PackageAsync(code)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RepeatedWrongCodes_RevokeTheOpenCodes()
    {
        var code = await IssueAsync();

        for (var i = 0; i < 5; i++)
            (await PackageAsync($"WRNG-{i}AAA-BBBB-CCCC")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await PackageAsync(code)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "guessing must not outrun the code's lifetime");
    }

    [Fact]
    public async Task Claim_TrustsTheDeviceAsSuperadmin_AndUsesTheCodeUp()
    {
        var code = await IssueAsync();
        var device = Guid.NewGuid();

        (await ClaimAsync(code, device, NewKey())).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _blind.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(device);
            row!.IsSuperadmin.Should().BeTrue();
        }
        (await ClaimAsync(code, Guid.NewGuid(), NewKey())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PackageAsync(code)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Claim_WithABadKeyOrABlindId_IsRejected_AndKeepsTheCode()
    {
        var code = await IssueAsync();

        (await ClaimAsync(code, Guid.NewGuid(), Convert.ToBase64String(new byte[7]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ClaimAsync(code, BlindNodeId.NewId(), NewKey())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ClaimAsync(code, Guid.NewGuid(), NewKey())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

#if !BLIND_NODE_HOST // needs a full node next to the blind one; the BlindNode.Tests project links this file without it
    [Fact]
    public async Task OnAFullNode_TheRoutesDoNotExist()
    {
        using var full = new RecoveryTestFactory();
        await full.InitializeNodeAsync(password: "FullPass1");
        using var console = full.CreateClient();

        (await console.PostAsync("/api/blind/restore-code", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/blind/restore/package");
        req.Headers.Add("X-Restore-Code", "AAAA-BBBB-CCCC-DDDD");
        (await full.Server.CreateClient().SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
#endif
}
