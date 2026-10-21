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

    // ── RV7-01: wrong codes are counted against the ADDRESS that sent them, never against the codes ──

    private Task<HttpResponseMessage> FromAsync(string address, HttpRequestMessage request)
    {
        request.Headers.Add(BmbWebApplicationFactory.RemoteIpHeader, address);
        return _public.SendAsync(request);
    }

    private Task<HttpResponseMessage> PackageFromAsync(string address, string code)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/blind/restore/package");
        request.Headers.Add("X-Restore-Code", code);
        return FromAsync(address, request);
    }

    private Task<HttpResponseMessage> ClaimFromAsync(string address, string code, Guid nodeId, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/blind/claim")
        {
            Content = content ?? JsonContent.Create(new { nodeId, displayName = "New PC", publicKeyB64 = NewKey() })
        };
        request.Headers.Add("X-Restore-Code", code);
        return FromAsync(address, request);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("package")]
    public async Task RV7_01_five_wrong_codes_from_one_address_cannot_revoke_a_legitimate_devices_code(string route)
    {
        var code = await IssueAsync();
        var device = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            using var wrong = route == "claim"
                ? await ClaimFromAsync("203.0.113.7", $"WRNG-{i}AAA-BBBB-CCCC", Guid.NewGuid())
                : await PackageFromAsync("203.0.113.7", $"WRNG-{i}AAA-BBBB-CCCC");
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var legitimate = route == "claim"
            ? await ClaimFromAsync("198.51.100.20", code, device)
            : await PackageFromAsync("198.51.100.20", code);
        legitimate.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, "a stranger's wrong guesses did not burn the code of the device that holds it");
        if (route == "claim") legitimate.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RV7_01_a_guesser_is_stopped_after_five_wrong_tries_from_one_address_whatever_it_sends_next()
    {
        var code = await IssueAsync();
        for (var i = 0; i < 5; i++)
        {
            using var wrong = await PackageFromAsync("203.0.113.7", $"WRNG-{i}AAA-BBBB-CCCC");
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var sixth = await PackageFromAsync("203.0.113.7", "WRNG-6AAA-BBBB-CCCC");
        using var rightCodeFromTheSameAddress = await PackageFromAsync("203.0.113.7", code);
        using var claimFromTheSameAddress = await ClaimFromAsync("203.0.113.7", "WRNG-7AAA-BBBB-CCCC", Guid.NewGuid());

        sixth.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        sixth.Headers.RetryAfter.Should().NotBeNull();
        rightCodeFromTheSameAddress.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "an address that has used its budget cannot test one more guess, even by accident");
        claimFromTheSameAddress.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the budget is the address's, across the routes that take a code");
    }

    [Fact]
    public async Task RV7_01_a_right_code_does_not_use_up_the_budget_and_a_claim_with_a_bad_body_does()
    {
        var code = await IssueAsync();
        for (var i = 0; i < 4; i++)
        {
            using var wrong = await PackageFromAsync("203.0.113.9", $"WRNG-{i}AAA-BBBB-CCCC");
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        using (var right = await PackageFromAsync("203.0.113.9", code)) right.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        using (var right = await PackageFromAsync("203.0.113.9", code)) right.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "the right code counts nothing");

        for (var i = 0; i < 5; i++)
        {
            using var garbage = await ClaimFromAsync("203.0.113.11", code, Guid.NewGuid(), new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));
            garbage.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        using var next = await ClaimFromAsync("203.0.113.11", code, Guid.NewGuid());
        next.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "every body the node reads for an address that has no right to be read is an attempt of that address");
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

    /// <summary>BMB-306/307 fix round 2 (T1): the address a claiming device states about itself goes through the one policy for a peer's address.</summary>
    [Theory]
    [InlineData("HTTPS://New-Pc.LOCAL:443/", "https://new-pc.local")]
    [InlineData("https://user:pw@new-pc.local", null)]
    [InlineData("http://169.254.169.254", null)]
    public async Task Claim_StoresTheDevicesAddressByTheOnePolicy_OrNone(string sent, string? stored)
    {
        var code = await IssueAsync();
        var device = Guid.NewGuid();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/blind/claim")
        {
            Content = JsonContent.Create(new { nodeId = device, displayName = "New PC", publicKeyB64 = NewKey(), apiAddress = sent })
        };
        req.Headers.Add("X-Restore-Code", code);

        (await _public.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _blind.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(device))!.ApiAddress.Should().Be(stored);
    }

    // ─── Review release-a #9: the claim is idempotent for the device that spent the code ───

    [Fact]
    public async Task Claim_WhoseTrustWriteFailed_IsRepeatedByTheSameDevice_AndCreatesOneRow()
    {
        var code = await IssueAsync();
        var device = Guid.NewGuid();
        var key = NewKey();
        // The code is spent before the whitelist write, which is not in its transaction: make that write fail once.
        Execute("CREATE TRIGGER test_fail_claim BEFORE INSERT ON tbl_whitelist BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
        HttpStatusCode first;
        try { first = (await ClaimAsync(code, device, key)).StatusCode; }
        catch (Exception) { first = HttpStatusCode.InternalServerError; } // the test host may rethrow instead of answering 500
        finally { Execute("DROP TRIGGER test_fail_claim;"); }
        first.Should().NotBe(HttpStatusCode.OK, "precondition: the first claim failed after the code was spent");

        var retry = await ClaimAsync(code, device, key);

        retry.StatusCode.Should().Be(HttpStatusCode.OK, "the device's retry repeats its claim instead of finding the code burnt");
        RowsFor(device).Should().Be(1);
        using var scope = _blind.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(device))!.IsSuperadmin.Should().BeTrue();
        (await ClaimAsync(code, Guid.NewGuid(), NewKey())).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a second device can never use the same code");
    }

    [Fact]
    public async Task Claim_RepeatedWithTheSameKey_IsIdempotent()
    {
        var code = await IssueAsync();
        var device = Guid.NewGuid();
        var key = NewKey();
        (await ClaimAsync(code, device, key)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ClaimAsync(code, device, key)).StatusCode.Should().Be(HttpStatusCode.OK);

        RowsFor(device).Should().Be(1);
    }

    [Fact]
    public async Task Claim_RepeatedWithAnotherKey_IsRefused_AndTheTrustedKeyStays()
    {
        var code = await IssueAsync();
        var device = Guid.NewGuid();
        var key = NewKey();
        (await ClaimAsync(code, device, key)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ClaimAsync(code, device, NewKey())).StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a replayed code must never swap the key of a trusted device");

        using var scope = _blind.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(device);
        Convert.ToBase64String(row!.Ed25519PublicKey).Should().Be(key);
    }

    [Fact]
    public async Task Claim_RepeatedAfterTheDeviceWasRevoked_DoesNotBringItBack()
    {
        var code = await IssueAsync();
        var device = Guid.NewGuid();
        var key = NewKey();
        (await ClaimAsync(code, device, key)).StatusCode.Should().Be(HttpStatusCode.OK);
        Execute($"UPDATE tbl_whitelist SET status = 'R', deleted_at = '{DateTime.UtcNow:O}' WHERE node_id = '{device}' COLLATE NOCASE;");

        (await ClaimAsync(code, device, key)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var scope = _blind.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(device)).Should().BeNull(
            "the revoked device stays revoked");
    }

    private void Execute(string sql)
    {
        using var conn = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        conn.Execute(sql);
    }

    private long RowsFor(Guid device)
    {
        using var conn = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_whitelist WHERE node_id = @Id COLLATE NOCASE", new { Id = device.ToString() });
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
