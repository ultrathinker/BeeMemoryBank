using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Rekey;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// <c>GET /api/rekey/report</c> (rekey-offline.md §8.4): the report the verb left in the data directory, as it is, to
/// the superadmin only.
/// </summary>
public class RekeyReportEndpointTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    public async Task InitializeAsync() => await _factory.InitializeNodeAsync();

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

    [Fact]
    public async Task AnAgentKey_DoesNotReadIt()
    {
        RekeyReport.Write(_factory.DataPath, Sample(null));
        var client = _factory.Server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "bee_not-a-real-agent-key");

        var resp = await client.GetAsync("/api/rekey/report");

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    private static RekeyReport Sample(string? oldVault) => new(
        RekeyReport.Done, DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow,
        new RekeyPreflightReport([], ["C:\\temp\\tmp1.tmp: a copy of a vault in the OS temp folder"], 1234),
        [new RekeyStepResult("chat", new Dictionary<string, long> { ["chat_message.content_ciphertext"] = 7 }, [])],
        ["peer-1"], ["recovery-code"], ["agent-1"], oldVault);
}
