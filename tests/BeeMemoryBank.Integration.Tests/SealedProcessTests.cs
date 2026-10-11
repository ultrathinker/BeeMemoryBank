using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The product's Api as a real operating-system process over a data folder (docs/design/step-b-spec.md 15.3.7 and 15.3.10): <c>dotnet BeeMemoryBank.Api.dll</c>
/// with its output captured the way the desktop application captures its node into <c>bmbd-*.log</c>. Two things only a real process shows: a process that is
/// killed (by itself, after the step the test names - the Testing-only hook <c>BMB_SEAL_TEST_KILL_AFTER</c>) leaves a folder the next start resolves, and
/// the log of a whole scripted session on an encrypted node holds no title, path, tag or text of the vault.
/// </summary>
public sealed class SealedProcessTests(ActivatedPlainTemplate template) : IClassFixture<ActivatedPlainTemplate>
{
    private const string Password = ActivatedPlainTemplate.Password;

    /// <summary>The Api as a child process. Its standard output and error are kept line by line.</summary>
    private sealed class ApiChild : IDisposable
    {
        private readonly Process _process;
        private readonly List<string> _output = [];
        private readonly HttpClient _client;

        private ApiChild(Process process, HttpClient client)
        {
            _process = process;
            _client = client;
        }

        public string Output
        {
            get { lock (_output) return string.Join("\n", _output); }
        }

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public static ApiChild Start(string dataDir, string? killAfter = null, bool traceLogs = false)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var info = new ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.Api.dll")}\"")
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
            info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            info.Environment["BMB_DATA_PATH"] = dataDir;
            info.Environment.Remove("BMB_ROLE");
            if (killAfter is not null) info.Environment["BMB_SEAL_TEST_KILL_AFTER"] = killAfter;
            if (traceLogs) info.Environment["Logging__LogLevel__Default"] = "Trace";
            var key = Convert.ToHexString(Guid.NewGuid().ToByteArray());
            info.Environment["BMB_INTERNAL_KEY"] = key;

            var process = new Process { StartInfo = info };
            var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(90) };
            client.DefaultRequestHeaders.Add("X-Internal-Key", key);
            client.DefaultRequestHeaders.Add("X-User-Id", "1");
            client.DefaultRequestHeaders.Add("X-User-Role", "superadmin");
            var child = new ApiChild(process, client);
            process.OutputDataReceived += (_, e) => child.Keep(e.Data);
            process.ErrorDataReceived += (_, e) => child.Keep(e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return child;
        }

        private void Keep(string? line)
        {
            if (line is null) return;
            lock (_output) _output.Add(line);
        }

        public async Task WaitHealthyAsync()
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < until)
            {
                if (_process.HasExited) throw new InvalidOperationException("The Api exited before it was healthy (exit " + _process.ExitCode + "):\n" + Output);
                try
                {
                    using var response = await _client.GetAsync("/health");
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(500);
            }
            throw new TimeoutException("The Api was not healthy in 90 seconds:\n" + Output);
        }

        public async Task<(int Code, string Body)> CallAsync(HttpMethod method, string path, string? json = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            try
            {
                using var response = await _client.SendAsync(request);
                return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return (-1, ex.Message);   // the process died under the request
            }
        }

        public bool WaitForExit(TimeSpan limit) => _process.WaitForExit((int)limit.TotalMilliseconds);

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(15_000);   // the kill is asynchronous on Linux and macOS
                }
            }
            catch (InvalidOperationException) { }
            _process.Dispose();
            _client.Dispose();
        }
    }

    private static string NewDir() => Path.Combine(Path.GetTempPath(), "bmb_seal_proc_" + Guid.NewGuid().ToString("N"));

    private static string Json(string password) => "{\"password\":\"" + password + "\"}";

    private async Task UnlockAsync(ApiChild api)
    {
        var (code, body) = await api.CallAsync(HttpMethod.Post, "/api/session/unlock", Json(Password));
        code.Should().Be(200, body);
    }

    [Theory]
    [InlineData("journal-building", false)]
    [InlineData("renamed-beememorybank.db", true)]
    [InlineData("journal-deleted", true)]
    public async Task A_real_process_that_dies_after_a_step_restarts_plain_with_its_old_files_or_sealed_with_the_new_ones(string step, bool sealedAfter)
    {
        var dir = NewDir();
        SealedNode.CopyTree(template.PlainDir, dir, skipDatabases: false);
        using (var first = ApiChild.Start(dir, killAfter: step))
        {
            await first.WaitHealthyAsync();
            await UnlockAsync(first);
            var (code, body) = await first.CallAsync(HttpMethod.Post, "/api/vault/seal", Json(Password));
            code.Should().Be(202, body);
            first.WaitForExit(TimeSpan.FromSeconds(120)).Should().BeTrue($"the process kills itself after {step}\n{first.Output}");
            first.ExitCode.Should().NotBe(0, "it did not leave by itself");
        }

        using var second = ApiChild.Start(dir);
        await second.WaitHealthyAsync();
        var status = await second.CallAsync(HttpMethod.Get, "/api/vault/status");
        status.Code.Should().Be(200, status.Body);
        status.Body.Should().Contain(sealedAfter ? "\"mode\":\"sealed\"" : "\"mode\":\"plain\"", $"after {step} the start goes {(sealedAfter ? "forward" : "back")}");

        await UnlockAsync(second);
        var articles = await second.CallAsync(HttpMethod.Get, "/api/articles");
        articles.Code.Should().Be(200, articles.Body);
        template.Canaries.Any(c => articles.Body.Contains(c, StringComparison.Ordinal)).Should().BeTrue("no third state: the vault is fully readable");

        Directory.EnumerateFileSystemEntries(dir, "*.seal-*").Should().BeEmpty("nothing of the migration survives the next start");
        if (sealedAfter)
        {
            // The raw bytes of the whole folder, once the node has stopped holding its files.
            second.Dispose();
            MetadataCanaries.ScanDirectory(dir, template.Canaries).Should().BeEmpty("no plain copy survives the next start");
        }
    }

    [Fact]
    public async Task The_log_of_a_whole_scripted_session_on_an_encrypted_node_holds_no_canary()
    {
        var dir = NewDir();
        SealedNode.CopyTree(template.PlainDir, dir, skipDatabases: false);
        var tail = Guid.NewGuid().ToString("N")[..10];
        var title = "logcanary-title-" + tail;
        var renamed = "logcanary-renamed-" + tail;
        var path = "/logcanary-folder-" + tail;
        var tagA = "logcanary-tag-a-" + tail;
        var tagB = "logcanary-tag-b-" + tail;
        var text = "logcanary-text-" + tail;
        string[] mine = [title, renamed, path.TrimStart('/'), tagA, tagB, text];
        var everything = template.Canaries.Concat(mine).ToList();

        // Encrypt it first (that run is part of the capture), then restart: the session below starts on a closed node.
        var log = new StringBuilder();
        using (var seal = ApiChild.Start(dir, traceLogs: true))
        {
            await seal.WaitHealthyAsync();
            await UnlockAsync(seal);
            var started = await seal.CallAsync(HttpMethod.Post, "/api/vault/seal", Json(Password));
            started.Code.Should().Be(202, started.Body);
            for (var i = 0; i < 300; i++)
            {
                var progress = await seal.CallAsync(HttpMethod.Get, "/api/vault/seal/progress");
                if (progress.Body.Contains("\"phase\":\"done\"")) break;
                progress.Body.Should().NotContain("\"phase\":\"failed\"", progress.Body);
                await Task.Delay(1000);
            }
            log.AppendLine(seal.Output);
        }

        using var api = ApiChild.Start(dir, traceLogs: true);
        await api.WaitHealthyAsync();
        (await api.CallAsync(HttpMethod.Get, "/api/articles")).Code.Should().Be(503, "the node starts closed");
        await UnlockAsync(api);

        var create = await api.CallAsync(HttpMethod.Post, "/api/articles",
            $"{{\"title\":\"{title}\",\"treePath\":\"{path}\",\"content\":\"{text}\",\"conceptTags\":[\"{tagA}\",\"{tagB}\"]}}");
        create.Code.Should().Be(201, create.Body);
        var id = System.Text.Json.JsonDocument.Parse(create.Body).RootElement.GetProperty("id").GetString();
        (await api.CallAsync(HttpMethod.Put, $"/api/articles/{id}", $"{{\"title\":\"{renamed}\"}}")).Code.Should().Be(200);
        (await api.CallAsync(HttpMethod.Post, "/api/concept-tags/merge", $"{{\"source\":\"{tagA}\",\"target\":\"{tagB}\"}}")).Code.Should().BeLessThan(500);
        // The query is a word of the vault's texts, not one of its values: the typed query is the caller's own and a framework may log it.
        (await api.CallAsync(HttpMethod.Get, "/api/search?q=logcanary&content=true")).Code.Should().BeLessThan(500);
        (await api.CallAsync(HttpMethod.Post, "/api/snapshots")).Code.Should().BeLessThan(500);
        (await api.CallAsync(HttpMethod.Post, "/api/session/lock")).Code.Should().Be(200);
        (await api.CallAsync(HttpMethod.Get, "/api/sync/identity")).Code.Should().BeLessThan(500);   // the relay set, answered while closed

        await Task.Delay(1500);   // the last lines of the lock and the close
        log.AppendLine(api.Output);
        var text2 = log.ToString();
        text2.Should().Contain("listening", "the capture holds the process's own output (the control)");
        foreach (var canary in everything)
            text2.Should().NotContain(canary, "the log of a sealed node names no title, path, tag or text of the vault");
    }
}
