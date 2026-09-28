using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Rekey;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// <c>GET /api/rekey/report</c> (rekey-offline.md §8.4): the report the verb left in the data directory, as it is, to
/// the superadmin only.
/// </summary>
public class RekeyReportEndpointTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    private const string Password = "rekeyReportPw1!";

    public async Task InitializeAsync() => await _factory.InitializeNodeAsync(password: Password);

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task WithoutAReport_ItIsNotFound()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/rekey/report");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheReport_IsReturnedAsTheVerbWroteIt()
    {
        var oldVault = Directory.CreateDirectory(_factory.DataPath + ".pre-rekey-20260928T120000Z").FullName;
        try
        {
            RekeyReport.Write(_factory.DataPath, Sample(oldVault));

            var resp = await _factory.CreateClient().GetAsync("/api/rekey/report");

            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("oldVaultExists").GetBoolean().Should().BeTrue();
            var report = body.GetProperty("report");
            report.GetProperty("result").GetString().Should().Be(RekeyReport.Done);
            report.GetProperty("oldVault").GetString().Should().Be(oldVault);
            report.GetProperty("steps")[0].GetProperty("counts").GetProperty("chat_message.content_ciphertext").GetInt64().Should().Be(7);
            report.GetProperty("preflight").GetProperty("warnings")[0].GetString().Should().Contain("temp");
            report.GetProperty("revokedPeers")[0].GetString().Should().Be("peer-1");
        }
        finally
        {
            Directory.Delete(oldVault);
        }
    }

    [Fact]
    public async Task OnceTheOldVaultIsDeleted_ThePageIsToldSo()
    {
        RekeyReport.Write(_factory.DataPath, Sample(_factory.DataPath + ".pre-rekey-gone"));

        var body = await (await _factory.CreateClient().GetAsync("/api/rekey/report")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("oldVaultExists").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AReportThatDoesNotParse_IsAnError_NotAnEmptyPage()
    {
        await File.WriteAllTextAsync(Path.Combine(_factory.DataPath, RekeyReport.FileName), "{ not json");

        var resp = await _factory.CreateClient().GetAsync("/api/rekey/report");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("admin")]
    public async Task OnlyTheSuperadmin_ReadsIt(string role)
    {
        RekeyReport.Write(_factory.DataPath, Sample(null));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Role", role);

        var resp = await client.GetAsync("/api/rekey/report");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>An agent inherits its owner's superadmin flag; the report is still not an agent's to read.</summary>
    [Fact]
    public async Task ASuperadminsAgent_DoesNotReadIt()
    {
        RekeyReport.Write(_factory.DataPath, Sample(null));
        using var client = _factory.Server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await SuperadminAgentKeyAsync());
        client.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);

        var resp = await client.GetAsync("/api/rekey/report");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("peer-1");
    }

    private async Task<string> SuperadminAgentKeyAsync()
    {
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password });
        login.EnsureSuccessStatusCode();
        var userId = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetInt32();

        var masterDek = _factory.Services.GetRequiredService<SessionService>().GetMasterDek();
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var (ciphertext, iv) = AgentKeyHelper.EncryptDek(apiKey, masterDek);
        Array.Clear(masterDek);
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAgentRepository>().CreateAsync(new Agent
        {
            Name = "Rekey Report Agent", KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey), KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
            EncryptedDek = ciphertext, DekIV = iv, Status = "A", CreatedAt = DateTime.UtcNow, OwnerUserId = userId,
        });
        return apiKey;
    }

    private static RekeyReport Sample(string? oldVault) => new(
        RekeyReport.Done, DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow,
        new RekeyPreflightReport([], ["C:\\temp\\tmp1.tmp: a copy of a vault in the OS temp folder"], 1234),
        [new RekeyStepResult("chat", new Dictionary<string, long> { ["chat_message.content_ciphertext"] = 7 }, [])],
        ["peer-1"], ["recovery-code"], ["agent-1"], oldVault);
}
