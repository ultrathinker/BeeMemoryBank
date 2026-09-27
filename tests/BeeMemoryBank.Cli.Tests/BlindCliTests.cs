using System.Net;
using System.CommandLine.Parsing;
using BeeMemoryBank.Cli.Commands;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// `bmb blind …` against a scripted Api: the commands are thin — they must call the right route
/// with the right body and exit 0 on 2xx, 2 on the "endpoint not merged yet" 404, 1 otherwise.
/// The HTTP seam (BlindApiOptions.Handler) stands in for the node so no socket is opened.
/// </summary>
public class BlindCliTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "bmb_cli_blind_" + Guid.NewGuid().ToString("N"));

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<(string Method, Uri Url, string? Body)> Calls { get; } = [];
        public List<string?> Roles { get; } = [];
        public (int Status, string Body) Answer { get; set; } = (200, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            string? body = null;
            if (request.Content is not null)
                body = await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, request.RequestUri!, body));
            Roles.Add(request.Headers.TryGetValues("X-User-Role", out var r) ? r.Single() : null);
            // The password route answers 204 in the real Api; the scripted default stays 200 for
            // everything else so each test only pins what it cares about.
            var answer = request.RequestUri!.AbsolutePath.EndsWith("/console/password")
                ? (Status: 204, Body: "")
                : Answer;
            return new HttpResponseMessage((HttpStatusCode)answer.Status) { Content = new StringContent(answer.Body) };
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static BlindApiOptions Options(ScriptedHandler handler, string key = "test-key") =>
        new() { BaseUrl = "http://node", InternalKey = key, Handler = handler };

    [Fact]
    public async Task Status_PrintsNodeNameAndStorage()
    {
        var handler = new ScriptedHandler
        {
            Answer = (200, """
                {"node_name":"BlindNode","node_id":"b11d0000-0000-8000-8000-000000000000","role":"blind",
                 "version":"1.0.11","protocol":3,"data_path":"/app/data","backups_path":"/backups",
                 "stored":{"articles":12,"blobs":3,"bytes":4096},"free_bytes":1000000,"cpu_mode":"economy",
                 "peers":[{"name":"hub","lag":0,"last_contact":"2026-09-27T10:00:00Z"}],"jobs":[]}
                """),
        };
        var sw = new StringWriter();
        (await BlindCommand.HandleStatusAsync(_tempDir, Options(handler), sw)).Should().Be(0);

        var text = sw.ToString();
        text.Should().Contain("BlindNode");
        text.Should().Contain("blind");
        text.Should().Contain("/app/data");
        text.Should().Contain("hub");
        // The numeric fields of the status JSON, not just its strings.
        text.Should().Contain("protocol 3");
        text.Should().Contain("12 articles, 3 blobs");
        text.Should().Contain("lag 0 events");
        handler.Calls.Should().ContainSingle(c => c.Url.PathAndQuery == "/api/blind/status");
        handler.Roles.Should().OnlyContain(r => r == "superadmin",
            "the /api/blind routes are .RequireSuperadmin(); the CLI on the box is the node's local administrator");
    }

    [Fact]
    public async Task Init_SetsConsolePassword_ThenSettings()
    {
        var handler = new ScriptedHandler();
        var sw = new StringWriter();
        var secrets = new Dictionary<string, string>
        {
            [BlindSecrets.ConsolePassword] = "console-pw-9",
            [BlindSecrets.ResticPassword] = "restic-pw",
        };
        var rc = await BlindCommand.HandleInitAsync(_tempDir, secrets,
            repoFolder: "/backups/restic", s3: null, bucket: null, prefix: null, ak: null, Options(handler), sw);

        rc.Should().Be(0);
        handler.Calls.Should().Contain(c => c.Method == "POST" && c.Url.PathAndQuery == "/api/blind/console/password"
            && c.Body!.Contains("console-pw-9"));
        handler.Calls.Should().Contain(c => c.Method == "PUT" && c.Url.PathAndQuery == "/api/blind/backup/settings"
            && c.Body!.Contains("/backups/restic") && c.Body!.Contains("restic-pw"));
    }

    [Fact]
    public async Task BackupList_PrintsSnapshots()
    {
        var handler = new ScriptedHandler
        {
            Answer = (200, """[{"id":"aaaa1111bbbb2222","time":"2026-09-27T03:05:00Z","hostname":"bmb-blind"}]"""),
        };
        var sw = new StringWriter();
        (await BlindCommand.HandleListAsync(_tempDir, Options(handler), sw)).Should().Be(0);
        sw.ToString().Should().Contain("aaaa1111");
        sw.ToString().Should().Contain("bmb-blind");
    }

    [Fact]
    public async Task Verify_StartsSubset_OrFull()
    {
        var handler = new ScriptedHandler { Answer = (202, """{"id":"20260927-0001","state":"running"}""") };
        var sw = new StringWriter();
        (await BlindCommand.HandleVerifyAsync(_tempDir, full: false, Options(handler), sw)).Should().Be(0);
        handler.Calls.Should().ContainSingle(c => c.Body!.Contains("\"full\":false"));

        (await BlindCommand.HandleVerifyAsync(_tempDir, full: true, Options(handler), sw)).Should().Be(0);
        handler.Calls.Should().ContainSingle(c => c.Body!.Contains("\"full\":true"));
    }

    [Fact]
    public async Task BackupCopy_PostsTheDestination()
    {
        var handler = new ScriptedHandler { Answer = (202, """{"id":"x","state":"running"}""") };
        var sw = new StringWriter();
        (await BlindCommand.HandleCopyAsync(_tempDir, "/backups/usb/bmb", Options(handler), sw)).Should().Be(0);
        handler.Calls.Should().ContainSingle(c => c.Url.PathAndQuery == "/api/blind/backup/copy"
            && c.Body!.Contains("/backups/usb/bmb"));
    }

    [Fact]
    public async Task Wipe_SendsBothConfirmations()
    {
        var handler = new ScriptedHandler { Answer = (200, """{"wiped":true}""") };
        var sw = new StringWriter();
        (await BlindCommand.HandleWipeAsync(_tempDir, "console-pw-9", "BlindNode", Options(handler), sw))
            .Should().Be(0);
        handler.Calls.Should().ContainSingle(c => c.Url.PathAndQuery == "/api/blind/wipe/cli"
            && c.Body!.Contains("console-pw-9") && c.Body!.Contains("BlindNode"));
    }

    [Fact]
    public async Task Jobs_PrintsJobRows()
    {
        var handler = new ScriptedHandler
        {
            Answer = (200, """{"jobs":[{"id":"20260927-0001","kind":"backup","state":"running","progress":0.42,"speed_bytes_per_sec":1024,"detail":"vacuuming database"}]}"""),
        };
        var sw = new StringWriter();
        (await BlindCommand.HandleJobsAsync(_tempDir, Options(handler), sw)).Should().Be(0);
        sw.ToString().Should().Contain("backup").And.Contain("42%").And.Contain("vacuuming");
    }

    // ── secrets never on the command line ────────────────────────────────────

    [Theory]
    [InlineData("blind init --console-password x")]
    [InlineData("blind init --restic-password x")]
    [InlineData("blind init --s3-secret-key x")]
    [InlineData("blind wipe --name n --console-password x")]
    public void SecretOptions_DoNotExist(string args)
    {
        var root = new System.CommandLine.RootCommand();
        var data = new System.CommandLine.Option<string>("--data", () => _tempDir, "data");
        root.AddGlobalOption(data);
        BlindCommand.AddTo(root, data);

        new Parser(root).Parse(args).Errors.Should().NotBeEmpty(
            "argv is readable by every account on the host (ps, /proc, docker inspect)");
    }

    [Fact]
    public void SecretsFile_ReadableByOthers_IsRefused()
    {
        if (OperatingSystem.IsWindows()) return; // unix permission bits
        Directory.CreateDirectory(_tempDir);
        var file = Path.Combine(_tempDir, "secrets.env");
        File.WriteAllText(file, "console_password=console-pw-9\n");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var read = () => BlindSecrets.FromFile(file, [BlindSecrets.ConsolePassword]);
        read.Should().Throw<UnauthorizedAccessException>().WithMessage("*chmod 600*");

        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        read().Should().ContainKey(BlindSecrets.ConsolePassword).WhoseValue.Should().Be("console-pw-9");
    }

    [Fact]
    public void Secrets_FromStdin_KeyValueLines()
    {
        var secrets = BlindSecrets.Parse(new StringReader("# node\nconsole_password=a=b c\n\nrestic_password=r\n"),
            [BlindSecrets.ConsolePassword, BlindSecrets.ResticPassword]);
        secrets.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [BlindSecrets.ConsolePassword] = "a=b c",
            [BlindSecrets.ResticPassword] = "r",
        });
        var unknown = () => BlindSecrets.Parse(new StringReader("password=x"), [BlindSecrets.ConsolePassword]);
        unknown.Should().Throw<FormatException>();
    }
}
