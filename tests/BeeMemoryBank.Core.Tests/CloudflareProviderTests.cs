using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Verifies <see cref="CloudflareProvider"/> against the real Cloudflare API v4 DNS-record
/// update shape (https://developers.cloudflare.com/api/resources/dns/subresources/records/methods/update/):
/// <c>PATCH/PUT https://api.cloudflare.com/client/v4/zones/{zone_id}/dns_records/{record_id}</c>
/// with <c>Authorization: Bearer {api_token}</c> and a JSON body containing the record fields.
/// The response is JSON with a top-level <c>success</c> boolean.
/// </summary>
public class CloudflareProviderTests
{
    private static string CfSuccessJson() => JsonSerializer.Serialize(new
    {
        success = true,
        errors = Array.Empty<object>(),
        messages = Array.Empty<object>(),
        result = new
        {
            id = "rec1",
            name = "home.example.com",
            type = "A",
            content = "203.0.113.42",
            ttl = 300,
            proxied = false
        }
    });

    [Fact]
    public async Task SendsBearerAuthAndJsonBodyToCorrectRecordUrl()
    {
        CapturedRequest? captured = null;
        int port = DdnsTestPort.GetFreePort();
        using var server = new DdnsMockHttpServer(port, req =>
        {
            captured = req;
            return ScriptedResponse.Json(CfSuccessJson());
        });
        server.Start();

        var http = new HttpClient();
        var config = new CloudflareConfig
        {
            ZoneId = "zoneABC",
            RecordId = "rec1",
            ApiToken = "cf-api-token",
            Domain = "home.example.com",
            RecordType = "A",
            Ttl = 300,
            Proxied = false
        };
        var provider = new CloudflareProvider(http, config)
        {
            BaseUrl = $"http://127.0.0.1:{port}/client/v4"
        };

        await provider.UpdateAsync(IPAddress.Parse("203.0.113.42"));

        // Assert the real Cloudflare request shape
        captured.Should().NotBeNull();
        captured!.Path.Should().Be("/client/v4/zones/zoneABC/dns_records/rec1");
        captured.Method.Should().Be("PATCH");

        // Documented auth: Bearer API token.
        captured.Headers["Authorization"].Should().Be("Bearer cf-api-token");
        (captured.Headers["Content-Type"] ?? "").Should().Contain("application/json");

        // Documented body: { name, type, content, ttl, proxied }
        using var doc = JsonDocument.Parse(captured.Body);
        var root = doc.RootElement;
        root.GetProperty("content").GetString().Should().Be("203.0.113.42");
        root.GetProperty("name").GetString().Should().Be("home.example.com");
        root.GetProperty("type").GetString().Should().Be("A");
        root.GetProperty("ttl").GetInt32().Should().Be(300);
        root.GetProperty("proxied").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AcceptsPutMethodAndDeducesRecordTypeForIpv4()
    {
        CapturedRequest? captured = null;
        int port = DdnsTestPort.GetFreePort();
        using var server = new DdnsMockHttpServer(port, req =>
        {
            captured = req;
            return ScriptedResponse.Json(CfSuccessJson());
        });
        server.Start();

        var http = new HttpClient();
        var config = new CloudflareConfig
        {
            ZoneId = "zoneABC",
            RecordId = "rec1",
            ApiToken = "cf-api-token",
            Domain = "home.example.com"
            // RecordType intentionally unset: PUT must still carry an explicit type.
        };
        var provider = new CloudflareProvider(http, config)
        {
            BaseUrl = $"http://127.0.0.1:{port}/client/v4",
            UpdateMethod = HttpMethod.Put
        };

        await provider.UpdateAsync(IPAddress.Parse("203.0.113.42"));

        captured.Should().NotBeNull();
        captured!.Method.Should().Be("PUT");
        captured.Path.Should().Be("/client/v4/zones/zoneABC/dns_records/rec1");

        using var doc = JsonDocument.Parse(captured.Body);
        var root = doc.RootElement;
        root.GetProperty("content").GetString().Should().Be("203.0.113.42");
        root.GetProperty("type").GetString().Should().Be("A");
    }

    [Fact]
    public async Task ThrowsWhenSuccessIsFalse()
    {
        int port = DdnsTestPort.GetFreePort();
        var failureBody = JsonSerializer.Serialize(new
        {
            success = false,
            errors = new[] { new { code = 1003, message = "Invalid or missing zone id." } },
            messages = Array.Empty<object>(),
            result = (object?)null
        });
        using var server = new DdnsMockHttpServer(port, _ => ScriptedResponse.Json(failureBody));
        server.Start();

        var provider = new CloudflareProvider(
            new HttpClient(),
            new CloudflareConfig
            {
                ZoneId = "zoneABC",
                RecordId = "rec1",
                ApiToken = "cf-api-token",
                Domain = "home.example.com",
                RecordType = "A"
            })
        {
            BaseUrl = $"http://127.0.0.1:{port}/client/v4"
        };

        var act = () => provider.UpdateAsync(IPAddress.Parse("203.0.113.42"));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ThrowsOnHttpErrorStatus()
    {
        int port = DdnsTestPort.GetFreePort();
        using var server = new DdnsMockHttpServer(port, _ => ScriptedResponse.Status(400, "{\"success\":false}"));
        server.Start();

        var provider = new CloudflareProvider(
            new HttpClient(),
            new CloudflareConfig
            {
                ZoneId = "zoneABC",
                RecordId = "rec1",
                ApiToken = "cf-api-token",
                Domain = "home.example.com",
                RecordType = "A"
            })
        {
            BaseUrl = $"http://127.0.0.1:{port}/client/v4"
        };

        var act = () => provider.UpdateAsync(IPAddress.Parse("203.0.113.42"));

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
