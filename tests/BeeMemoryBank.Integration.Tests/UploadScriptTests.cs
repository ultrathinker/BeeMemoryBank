using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using BeeMemoryBank.Api.McpTools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Python script bee_get_upload_script hands out: it compiles, it reports the caller's own client
/// name (so a gateway sees no new client), it needs no bearer key through a gateway, and it can
/// prefix the tool names a gateway serves. Run against a tiny fake MCP endpoint, with the real
/// interpreter; skipped (with the reason) where there is none.
/// </summary>
public class UploadScriptTests
{
    private const string ClientName = "Claude Code - Personal";
    private const string GatewayPrefix = "bee-memory-bank__";

    private static string ScriptText() => new BeeUploadTools(null!, null!, null!, null!).GetUploadScript();

    // One copy of the script and one upload fixture per test run, overwritten in place next to the
    // test binaries (nothing is created per test and nothing needs removing).
    private static readonly Lazy<string> ScriptPath = new(() => Write("bmb-upload.py", ScriptText()));
    private static readonly Lazy<string> FixturePath = new(() => Write("bmb-upload-fixture.txt", "# fixture\nhello\n"));

    private static string Write(string name, string content)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    // ───── what the descriptions and the usage comment tell an agent ─────

    [Fact]
    public void Descriptions_TellTheAgentToPassItsOwnClientName()
    {
        foreach (var tool in new[] { nameof(BeeUploadTools.GetUploadScript), nameof(BeeUploadTools.SaveMedia) })
        {
            var text = typeof(BeeUploadTools).GetMethod(tool)!.GetCustomAttribute<DescriptionAttribute>()!.Description;
            text.Should().Contain("--client-name").And.Contain("do not invent a new name")
                .And.Contain("every new name creates a new client in the gateway", $"{tool} describes the script");
        }
    }

    [Fact]
    public void UsageComment_ShowsTheDirectWayAndTheGatewayWay()
    {
        var comment = string.Join("\n", ScriptText().Split('\n').TakeWhile(l => l.StartsWith('#')));

        comment.Should().Contain("--bearer bee_xxx").And.Contain($"--client-name \"{ClientName}\"")
            .And.Contain($"--tool-prefix {GatewayPrefix}").And.Contain("http://127.0.0.1:39100/mcp");
    }

    // ───── the script itself ─────

    [PythonFact]
    public async Task TheScript_Compiles()
    {
        var run = await RunPythonAsync("-m", "py_compile", ScriptPath.Value);

        run.ExitCode.Should().Be(0, run.Stderr);
    }

    [PythonFact]
    public async Task ClientName_GoesIntoInitializeExactlyAsGiven()
    {
        await using var mcp = await FakeMcp.StartAsync();

        var run = await RunScriptAsync("--url", mcp.Url, "--client-name", ClientName, "upload-media", FixturePath.Value);

        run.ExitCode.Should().Be(0, run.Stderr);
        mcp.Requests.Single(r => r.Method == "initialize")
            .Body["params"]!["clientInfo"]!["name"]!.GetValue<string>().Should().Be(ClientName);
    }

    [PythonFact]
    public async Task WithoutBearer_NoAuthorizationHeaderGoesOut()
    {
        await using var mcp = await FakeMcp.StartAsync();

        var run = await RunScriptAsync("--url", mcp.Url, "--client-name", ClientName, "upload-media", FixturePath.Value);

        run.ExitCode.Should().Be(0, run.Stderr);
        mcp.Requests.Select(r => r.Method).Should().Equal("initialize", "notifications/initialized", "tools/call");
        mcp.Requests.Should().OnlyContain(r => r.Authorization == null);
    }

    [PythonFact]
    public async Task WithBearer_EveryRequestCarriesIt()
    {
        await using var mcp = await FakeMcp.StartAsync();

        var run = await RunScriptAsync("--url", mcp.Url, "--bearer", "bee_test", "--client-name", ClientName, "upload-media", FixturePath.Value);

        run.ExitCode.Should().Be(0, run.Stderr);
        mcp.Requests.Should().HaveCount(3).And.OnlyContain(r => r.Authorization == "Bearer bee_test");
    }

    [PythonTheory]
    [InlineData("", "upload-media", "bee_save_media")]
    [InlineData(GatewayPrefix, "upload-media", GatewayPrefix + "bee_save_media")]
    [InlineData(GatewayPrefix, "create", GatewayPrefix + "bee_save_article")]
    [InlineData(GatewayPrefix, "update", GatewayPrefix + "bee_update_article")]
    public async Task ToolPrefix_IsPutInFrontOfTheToolName(string prefix, string action, string expectedTool)
    {
        await using var mcp = await FakeMcp.StartAsync();
        var actionArgs = action switch
        {
            "create" => new[] { "create", FixturePath.Value, "A title", "/Some/Path" },
            "update" => new[] { "update", FixturePath.Value, Guid.NewGuid().ToString() },
            _ => new[] { "upload-media", FixturePath.Value },
        };
        var args = new[] { "--url", mcp.Url, "--client-name", ClientName }
            .Concat(prefix.Length > 0 ? new[] { "--tool-prefix", prefix } : [])
            .Concat(actionArgs).ToArray();

        var run = await RunScriptAsync(args);

        run.ExitCode.Should().Be(0, run.Stderr);
        mcp.Requests.Single(r => r.Method == "tools/call")
            .Body["params"]!["name"]!.GetValue<string>().Should().Be(expectedTool);
    }

    [PythonTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutAClientName_ItStopsWithAMessage_AndSendsNothing(bool blank)
    {
        await using var mcp = await FakeMcp.StartAsync();
        var args = new[] { "--url", mcp.Url }
            .Concat(blank ? new[] { "--client-name", "  " } : [])
            .Concat(new[] { "upload-media", FixturePath.Value }).ToArray();

        var run = await RunScriptAsync(args);

        run.ExitCode.Should().NotBe(0);
        run.Stderr.Should().Contain("--client-name is required").And.Contain(ClientName)
            .And.Contain("Do not invent a new name");
        mcp.Requests.Should().BeEmpty();
    }

    [PythonTheory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task ARedirect_IsNotFollowed_AndTheBearerNeverReachesTheOtherHost(int status)
    {
        await using var elsewhere = await FakeMcp.StartAsync();
        await using var mcp = await FakeMcp.StartAsync((status, elsewhere.Url));

        var run = await RunScriptAsync("--url", mcp.Url, "--bearer", "bee_secret", "--client-name", ClientName, "upload-media", FixturePath.Value);

        elsewhere.Requests.Should().BeEmpty("the other host must see no request, and so no bearer");
        mcp.Requests.Should().ContainSingle("the redirected request is not repeated anywhere");
        run.ExitCode.Should().NotBe(0);
        run.Stderr.Should().Contain(elsewhere.Url).And.Contain("final URL");
    }

    // ───── plumbing ─────

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunScriptAsync(params string[] args) =>
        RunPythonAsync(args.Prepend(ScriptPath.Value).ToArray());

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPythonAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(PythonInterpreter.Path!) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // The fake endpoint is on loopback: keep the child off any system proxy. (Python only reads
        // no_proxy when a proxy variable is set, hence the placeholder.)
        psi.Environment["http_proxy"] = "http://127.0.0.1:9";
        psi.Environment["no_proxy"] = "127.0.0.1,localhost";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await proc.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            using var killLimit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await proc.WaitForExitAsync(killLimit.Token);
            throw;
        }
        return (proc.ExitCode, await stdout, await stderr);
    }

    /// <summary>The first interpreter that answers as Python 3: python3 (the only name on most Linux
    /// boxes), python, then the Windows launcher. Null when there is none.</summary>
    internal static class PythonInterpreter
    {
        public static readonly string? Path = Find();

        private static string? Find()
        {
            foreach (var candidate in new[] { "python3", "python", "py" })
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(candidate, "--version")
                    { RedirectStandardOutput = true, RedirectStandardError = true })!;
                    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    if (p.WaitForExit(10_000) && p.ExitCode == 0 && output.TrimStart().StartsWith("Python 3."))
                        return candidate;
                }
                catch (System.ComponentModel.Win32Exception) { /* not on PATH */ }
            }
            return null;
        }
    }

    public sealed class PythonFactAttribute : FactAttribute
    {
        public PythonFactAttribute() { Skip = PythonInterpreter.Path == null ? NoPython : null; }
    }

    public sealed class PythonTheoryAttribute : TheoryAttribute
    {
        public PythonTheoryAttribute() { Skip = PythonInterpreter.Path == null ? NoPython : null; }
    }

    private const string NoPython =
        "Python 3 not found on PATH (tried python3, python, py): the upload script cannot be run on this machine.";

    /// <summary>A tiny MCP endpoint: answers initialize, the initialized notification and tools/call,
    /// and keeps what it was sent.</summary>
    private sealed class FakeMcp : IAsyncDisposable
    {
        public sealed record Seen(string? Authorization, JsonNode Body)
        {
            public string? Method => Body["method"]?.GetValue<string>();
        }

        private readonly WebApplication _app;
        private readonly List<Seen> _seen = [];

        private FakeMcp(WebApplication app) => _app = app;

        public string Url => _app.Urls.Single() + "/mcp";

        public IReadOnlyList<Seen> Requests { get { lock (_seen) return _seen.ToList(); } }

        /// <param name="redirect">When set, every request is answered with this status and a Location
        /// header pointing at the given URL, and nothing else is done.</param>
        public static async Task<FakeMcp> StartAsync((int Status, string Location)? redirect = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var mcp = new FakeMcp(builder.Build());
            // Any method and path, so a request the script should never have made is seen too.
            mcp._app.Run(async ctx =>
            {
                var body = ctx.Request.ContentLength > 0 ? (await JsonNode.ParseAsync(ctx.Request.Body))! : new JsonObject();
                lock (mcp._seen) mcp._seen.Add(new Seen(ctx.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null, body));
                if (redirect is { } r)
                {
                    ctx.Response.StatusCode = r.Status;
                    ctx.Response.Headers.Location = r.Location;
                    return;
                }
                var id = body["id"]?.DeepClone();
                switch (body["method"]?.GetValue<string>())
                {
                    case "initialize":
                        ctx.Response.Headers["Mcp-Session-Id"] = "fake-session";
                        await ctx.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, result = new { protocolVersion = "2025-03-26", capabilities = new { }, serverInfo = new { name = "fake", version = "1" } } });
                        break;
                    case "tools/call":
                        await ctx.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, result = new { content = new[] { new { type = "text", text = "{\"mediaId\":\"00000000-0000-0000-0000-000000000001\"}" } } } });
                        break;
                    default:
                        ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                        break;
                }
            });
            await mcp._app.StartAsync();
            return mcp;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
