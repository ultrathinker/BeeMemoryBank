using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Sync.Blind;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-312 fix round, finding 2: the refusal of a seed is logged at Warning and answered to the sender, and it must hold a fixed sentence about the check that
/// failed - never the private directory layout of the node that refused. A link planted where the cutover works made the sentence carry the full path of it.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class BlindSeedRefusalPathTests : IAsyncLifetime
{
    private const string Password = "blindRefusalPw1!";
    private readonly StepALogCapture _logs = new();
    private LoggingBlindFactory _blind = null!;
    private readonly BmbWebApplicationFactory _pc = new();
    private HttpClient _pcClient = null!;

    private sealed class LoggingBlindFactory(StepALogCapture logs) : BlindNodeFactory(drainWaitSeconds: 10)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(logs));
        }
    }

    public async Task InitializeAsync()
    {
        _blind = new LoggingBlindFactory(_logs);
        _ = _blind.Services;
        _pc.RouteOutboundHttpThrough(_blind.Server.CreateHandler());
        await _pc.InitializeNodeAsync("PC", Password);
        _pcClient = _pc.CreateClient();
        (await _pcClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _pcClient.Dispose();
        _pc.Dispose();
        _blind.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_seed_refused_because_a_cutover_path_is_a_link_says_the_code_and_never_a_file_or_directory_name()
    {
        var outside = Path.Combine(Path.GetTempPath(), "bmb-refusal-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var link = BlindSeedCutover.DirOf(_blind.DataPath);
        try
        {
            if (!BlindSeedCutoverFileTests.TryCreateDirectoryLink(link, outside)) return;   // no link, no test

            using var console = _blind.CreateClient();
            var code = (await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code")).GetProperty("code").GetString()!;
            var add = await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code });
            var answer = await add.Content.ReadAsStringAsync();

            add.IsSuccessStatusCode.Should().BeFalse("the blind node refused the seed: " + answer);
            var refusals = _logs.Lines.Where(l => l.Contains(" Warning ") && l.Contains("refused")).ToList();
            refusals.Should().NotBeEmpty("the operator of the blind node is told");
            refusals.Should().Contain(l => l.Contains(nameof(BlindSeedRefusal.CutoverLink)) && l.Contains("a link"), "the line says which check failed, by its code");
            var privateParts = new[] { _blind.DataPath, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), outside };
            foreach (var secret in privateParts)
            {
                refusals.Should().NotContain(l => l.Contains(secret, StringComparison.OrdinalIgnoreCase), "the Warning holds no private path");
                answer.Should().NotContain(secret, "nor does the answer the sender gets");
            }
        }
        finally
        {
            BlindSeedCutoverFileTests.TryRemoveDirectoryLink(link);
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    public static IEnumerable<object[]> Codes() => Enum.GetValues<BlindSeedRefusal>().Select(c => new object[] { c });

    private static readonly System.Text.RegularExpressions.Regex PathOrAddress = new(
        @"[A-Za-z]:[\\/]|\\\\[^\s]|://|(^|[\s'""(=])/(?:[\w.\-]+/)+[\w.\-]+");

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_refusal_has_one_fixed_sentence_without_a_path_or_an_address(BlindSeedRefusal code)
    {
        var sentence = BlindSeedRefusals.Sentence(code);

        sentence.Should().NotBeNullOrWhiteSpace();
        PathOrAddress.IsMatch(sentence).Should().BeFalse("a refusal never carries a path or an address");
        new BlindSeedRejectedException(code).Message.Should().Be(sentence);
        new BlindSeedRejectedException(code).Reason.Should().Be(code);
    }

    [Theory]
    [InlineData(typeof(InvalidDataException), @"cannot read C:\private\layout\package.bin")]
    [InlineData(typeof(InvalidDataException), @"..\..\relative\secret.db is damaged")]
    [InlineData(typeof(System.Text.Json.JsonException), "'x' is invalid after a property name. Path: $.secret.token | LineNumber: 3")]
    [InlineData(typeof(IOException), "The process cannot access the file '/var/lib/bmb/data/control.db' because it is being used")]
    public void The_text_of_an_inner_exception_is_never_part_of_a_refusal(Type type, string text)
    {
        var inner = (Exception)Activator.CreateInstance(type, text)!;

        var refusal = new BlindSeedRejectedException(BlindSeedRefusal.PackageV2Invalid, inner);

        refusal.Message.Should().Be(BlindSeedRefusals.Sentence(BlindSeedRefusal.PackageV2Invalid));
        refusal.Message.Should().NotContain("private").And.NotContain("secret").And.NotContain("control.db").And.NotContain("$.");
        refusal.InnerException.Should().BeSameAs(inner, "the cause travels with the exception for whoever has a right to it, not in its sentence");
    }
}
