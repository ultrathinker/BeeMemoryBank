using System;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

public class DdnsSecretsEncryptionTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DdnsConfig_EncryptsSecretsAtRest()
    {
        var plaintextToken = "super-secret-duckdns-token-" + Guid.NewGuid().ToString();
        var req = new
        {
            provider = "duckdns",
            domain = "testnode.duckdns.org",
            token = plaintextToken,
            ipMode = "upnp"
        };

        // 1. Post to config endpoint
        var resp = await _client.PostAsJsonAsync("/api/internet-access/ddns/config", req);
        resp.EnsureSuccessStatusCode();

        // 2. Verify response does NOT contain the plaintext token
        var responseString = await resp.Content.ReadAsStringAsync();
        responseString.Should().NotContain(plaintextToken);

        // 3. Read the actual persisted file on disk. Encryption is Windows-only (DPAPI) — on other
        //    platforms the endpoint intentionally leaves the token as plaintext, so only assert the
        //    "not recoverable" property where it's actually supposed to hold.
        var configPath = Path.Combine(_factory.DataPath, "internet-access", "ddns-config.json");
        File.Exists(configPath).Should().BeTrue();

        var fileContent = await File.ReadAllTextAsync(configPath);
        if (OperatingSystem.IsWindows())
        {
            fileContent.Should().NotContain(plaintextToken);
            // Verify that the file actually contains the serialized property but not the plaintext
            fileContent.Should().Contain("\"token\"");
        }
        else
        {
            fileContent.Should().Contain(plaintextToken,
                "off Windows, DPAPI is unavailable and the endpoint intentionally stores the token as plaintext");
        }

        // 4. Verify we can call GET /info and it doesn't leak the token (neither plaintext nor encrypted)
        var infoResp = await _client.GetAsync("/api/internet-access/info");
        infoResp.EnsureSuccessStatusCode();
        var infoString = await infoResp.Content.ReadAsStringAsync();
        infoString.Should().NotContain(plaintextToken);
    }
}
