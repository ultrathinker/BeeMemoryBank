using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-312: a blind node that refuses a seed answered 400 to the sender and wrote nothing, so the operator of the blind node (whose log is the
/// first place anybody looks) learned nothing, and a seed that failed with "file is not a database" could only be diagnosed by the sender's console.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class BlindSeedRefusalLogTests : IAsyncLifetime
{
    private readonly StepALogCapture _logs = new();
    private LoggingBlindFactory _blind = null!;

    private sealed class LoggingBlindFactory(StepALogCapture logs) : BlindNodeFactory(drainWaitSeconds: 10)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(logs));
        }
    }

    public Task InitializeAsync()
    {
        _blind = new LoggingBlindFactory(_logs);
        _ = _blind.Services;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _blind.Dispose();
        return Task.CompletedTask;
    }

    private async Task<BlindPairCode> PairCodeAsync()
    {
        using var console = _blind.CreateClient();
        var body = await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code");
        return BlindPairCode.Parse(body.GetProperty("code").GetString()!);
    }

    /// <summary>A seeder that names itself under the pair code's secret, as a PC does (BlindNodeManager), over a package that is too big to be taken.</summary>
    private async Task<(HttpResponseMessage Response, Guid SeedId, string KeyB64, string Secret)> SendTooBigSeedAsync()
    {
        var code = await PairCodeAsync();
        var (key, _) = Ed25519Signer.GenerateKeyPair();
        var (seeder, seedId) = (Guid.NewGuid(), Guid.NewGuid());
        using var http = _blind.Server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={seedId}&offset=0&total={BlindSeedService.DefaultMaxPackageBytes + 1}&sha256={new string('0', 64)}")
        { Content = new ByteArrayContent(new byte[1]) };
        req.Headers.Add(BlindSeederProof.NodeIdHeader, seeder.ToString());
        req.Headers.Add(BlindSeederProof.KeyHeader, Convert.ToBase64String(key));
        req.Headers.Add(BlindSeederProof.MacHeader, BlindSeederProof.Compute(code.Secret, seedId, seeder, key));
        return (await http.SendAsync(req), seedId, Convert.ToBase64String(key), code.Secret);
    }

    [Fact]
    public async Task A_refused_seed_is_logged_with_its_reason_at_Warning()
    {
        var (response, seedId, _, _) = await SendTooBigSeedAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var reason = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
        reason.Should().Contain("outside", "the sender is told why");
        _logs.Lines.Should().Contain(line => line.Contains(" Warning ") && line.Contains(seedId.ToString()) && line.Contains(reason),
            "the operator of the blind node is told the same thing in its log");
    }

    [Fact]
    public async Task A_package_that_cannot_be_read_is_refused_with_the_fixed_sentence_and_the_log_names_the_code_and_only_the_type_of_the_cause()
    {
        // BMB-306/307 fix round 2 (M3/P4): bytes that are no package at all. The reader's own words never leave the node.
        var code = await PairCodeAsync();
        var (key, _) = Ed25519Signer.GenerateKeyPair();
        var (seeder, seedId) = (Guid.NewGuid(), Guid.NewGuid());
        var bytes = RandomNumberGenerator.GetBytes(2048);
        using var http = _blind.Server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={seedId}&offset=0&total={bytes.Length}&sha256={Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}")
        { Content = new ByteArrayContent(bytes) };
        req.Headers.Add(BlindSeederProof.NodeIdHeader, seeder.ToString());
        req.Headers.Add(BlindSeederProof.KeyHeader, Convert.ToBase64String(key));
        req.Headers.Add(BlindSeederProof.MacHeader, BlindSeederProof.Compute(code.Secret, seedId, seeder, key));

        var response = await http.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        var reason = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
        reason.Should().Be(BlindSeedRefusals.Sentence(BlindSeedRefusal.PackageInvalid));
        var line = _logs.Lines.Single(l => l.Contains(" Warning ") && l.Contains(seedId.ToString()) && l.Contains("refused"));
        line.Should().Contain(nameof(BlindSeedRefusal.PackageInvalid)).And.Contain("cause:").And.Contain("Exception");
        line.Should().NotContain("tar").And.NotContain("archive").And.NotContain("gzip", "the reader's own words are not in the log either");
    }

    [Fact]
    public async Task The_refusal_line_holds_no_pair_code_secret_and_no_key()
    {
        var (_, _, keyB64, secret) = await SendTooBigSeedAsync();

        var refused = _logs.Lines.Where(l => l.Contains("refused")).ToList();
        refused.Should().NotBeEmpty();
        refused.Should().NotContain(l => l.Contains(secret) || l.Contains(keyB64));
    }

    [Fact]
    public async Task A_seed_that_is_not_authorized_is_not_logged_as_a_refusal_with_a_reason()
    {
        using var http = _blind.Server.CreateClient();
        var seedId = Guid.NewGuid();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={seedId}&offset=0&total=10&sha256={new string('0', 64)}")
        { Content = new ByteArrayContent(new byte[10]) };

        (await http.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        _logs.Lines.Should().NotContain(l => l.Contains("Blind seed " + seedId), "an unauthenticated caller is not told anything and does not fill the log");
    }
}
