using System.Text.Encodings.Web;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Startup;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Safety net for unobserved Task exceptions from `_ = Task.Run(...)` fire-and-forget
// sites (DEK rotation retry, network restore, embedding backfill, etc.). Without this,
// an exception thrown before the inner try/catch is reached (e.g. CreateScope failure,
// OOM, StackOverflow) would crash the process when GC finalizes the Task. Mark
// SetObserved so the host doesn't escalate.
TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    Console.Error.WriteLine($"[UnobservedTaskException] {e.Exception}");
    e.SetObserved();
};

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLoopbackForwardedHeaders(builder.Configuration);

// BMB_INTERNAL_KEY: shared secret for Web→API internal auth (added to every request by InternalKeyHandler).
// In production: always set by docker-entrypoint.sh before both processes start.
// In development: auto-generated per-run and stored in {dataPath}/.internal-key (shared with Web UI).
// FAIL-FAST: refuse to start in production if the key is missing — means entrypoint was bypassed.
if (builder.Environment.IsProduction() &&
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY")))
{
    throw new InvalidOperationException(
        "BMB_INTERNAL_KEY is not set. In production it must be exported by docker-entrypoint.sh " +
        "before the API process starts. Do not override ENTRYPOINT or run the API directly.");
}

var dataPath = builder.Configuration["BeeMemoryBank:DataPath"]
    ?? Environment.GetEnvironmentVariable("BMB_DATA_PATH")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "data");

Directory.CreateDirectory(dataPath);

// Dev-only: auto-generate BMB_INTERNAL_KEY from a local file shared with the Web UI process.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY")))
{
    var keyFile = Path.Combine(dataPath, ".internal-key");
    string key;
    if (File.Exists(keyFile))
    {
        key = File.ReadAllText(keyFile).Trim();
    }
    else
    {
        key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(keyFile, key);
    }
    Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", key);
}


builder.AddBeeApiServices(dataPath);

var app = builder.Build();

app.UseLoopbackForwardedHeaders();
await app.RunBeeApiStartupTasksAsync(dataPath);
app.UseBeeApiPipeline();
app.MapBeeApiEndpoints();

app.Run();

// Required for WebApplicationFactory in tests
public partial class Program { }
