using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;
using FluentAssertions;
using BeeMemoryBank.Hosting;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

public class EndToEndIntegrationTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly string _stubDllPath;
    private readonly List<WebApplication> _stubApps = new();

    public EndToEndIntegrationTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "bmb-e2e-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDataDir);

        _stubDllPath = Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.Node.Tests.StubProcess.dll");
        if (!File.Exists(_stubDllPath))
        {
            throw new FileNotFoundException($"Stub process DLL not found at: {_stubDllPath}.");
        }
    }

    public void Dispose()
    {
        foreach (var app in _stubApps)
        {
            try
            {
                app.StopAsync().GetAwaiter().GetResult();
                app.DisposeAsync().GetAwaiter().GetResult();
            }
            catch { }
        }

        try
        {
            if (Directory.Exists(_testDataDir))
            {
                Directory.Delete(_testDataDir, recursive: true);
            }
        }
        catch { }
    }

    private async Task<WebApplication> StartKestrelStubAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0); // Bind to random port
        });
        var app = builder.Build();
        configure(app);
        await app.StartAsync();
        _stubApps.Add(app);
        return app;
    }

    [Fact]
    public async Task E2E_OrchestratorSpawnsChildren_FrontProxiesSuccessfully()
    {
        // 1. Start real Kestrel backends in the test
        var apiBackend = await StartKestrelStubAsync(app =>
        {
            app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "api" }));
        });

        var webBackend = await StartKestrelStubAsync(app =>
        {
            app.MapFallback((HttpContext ctx) => Results.Ok(new { path = ctx.Request.Path.Value, service = "web" }));
        });

        var apiBackendUrl = apiBackend.Urls.First();
        var webBackendUrl = webBackend.Urls.First();

        // 2. Setup child process configs for the orchestrator
        var apiReadyFile = Path.Combine(_testDataDir, "api.ready");
        var webReadyFile = Path.Combine(_testDataDir, "web.ready");

        var apiConfig = new ChildProcessConfig(
            ApplicationName: "BeeMemoryBank.Api",
            ExecutablePath: "dotnet",
            WorkingDirectory: AppContext.BaseDirectory,
            ReadyFilePath: apiReadyFile,
            Arguments: $"\"{_stubDllPath}\" --ready-file \"{apiReadyFile}\" --app-name BeeMemoryBank.Api --urls \"{apiBackendUrl}\""
        );

        var webConfig = new ChildProcessConfig(
            ApplicationName: "BeeMemoryBank.Web",
            ExecutablePath: "dotnet",
            WorkingDirectory: AppContext.BaseDirectory,
            ReadyFilePath: webReadyFile,
            Arguments: $"\"{_stubDllPath}\" --ready-file \"{webReadyFile}\" --app-name BeeMemoryBank.Web --urls \"{webBackendUrl}\""
        );

        // 3. Start orchestrator
        using var orchestrator = new NodeOrchestrator(_testDataDir, new[] { apiConfig, webConfig });
        await orchestrator.StartAsync(CancellationToken.None);

        orchestrator.AllReady.Should().BeTrue();
        orchestrator.ReadyChildren.Should().HaveCount(2);

        // 4. Build and start front using the wired logic in Program.BuildFront
        // We tell Kestrel to listen on a random loopback port
        var frontApp = Program.BuildFront(new[] { "--urls", "http://127.0.0.1:0" }, orchestrator.ReadyChildren);
        await frontApp.StartAsync();

        var frontUrl = frontApp.Urls.FirstOrDefault();
        frontUrl.Should().NotBeNullOrEmpty();

        // 5. Update front URL in orchestrator/status manager
        orchestrator.UpdateFrontUrl(frontUrl!);

        // 6. Verify status files on disk
        var runtimePath = Path.Combine(_testDataDir, ".runtime.json");
        File.Exists(runtimePath).Should().BeTrue();
        var runtimeJson = File.ReadAllText(runtimePath);
        var runtime = JsonSerializer.Deserialize<RuntimeDescriptor>(runtimeJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        runtime.Should().NotBeNull();
        runtime!.FrontUrl.Should().Be(frontUrl);

        var statusPath = Path.Combine(_testDataDir, "node.status.json");
        File.Exists(statusPath).Should().BeTrue();
        var statusJson = File.ReadAllText(statusPath);
        var status = JsonSerializer.Deserialize<NodeStatus>(statusJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        status.Should().NotBeNull();
        status!.Status.Should().Be("Ready");

        // 7. Make requests through the front and verify YARP proxies correctly
        using var httpClient = new HttpClient();

        // A. Request to /health should route to Api
        var apiRes = await httpClient.GetAsync($"{frontUrl}/health");
        apiRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiContent = await apiRes.Content.ReadFromJsonAsync<JsonElement>();
        apiContent.GetProperty("service").GetString().Should().Be("api");
        apiContent.GetProperty("status").GetString().Should().Be("healthy");

        // B. Request to arbitrary path should fall back to Web
        var webRes = await httpClient.GetAsync($"{frontUrl}/some-random-page");
        webRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var webContent = await webRes.Content.ReadFromJsonAsync<JsonElement>();
        webContent.GetProperty("service").GetString().Should().Be("web");
        webContent.GetProperty("path").GetString().Should().Be("/some-random-page");

        // 8. Test clean shutdown of front and orchestrator
        await frontApp.StopAsync();
        await frontApp.DisposeAsync();

        await orchestrator.StopAsync();

        // Verify status files are cleaned up
        File.Exists(statusPath).Should().BeFalse();
        File.Exists(runtimePath).Should().BeFalse();
    }
}
