using BeeMemoryBank.BlindNode.Startup;
using BeeMemoryBank.Hosting.AspNetCore;

// The blind node's process: a store that relays the mesh's ciphertext and can read none of it. Its startup is the
// blind half of BeeMemoryBank.Api's Program.cs, written out on its own (docs/blind-node/STARTUP-CONTRACT.md lists
// every step and where it came from).

// Safety net for unobserved Task exceptions from `_ = Task.Run(...)` fire-and-forget sites: an exception thrown
// before the inner try/catch is reached must not take the process down when the GC finalizes the Task.
TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    Console.Error.WriteLine($"[UnobservedTaskException] {e.Exception}");
    e.SetObserved();
};

BeeMemoryBank.Hosting.Utf8Console.EnableForRedirectedOutput();
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLoopbackForwardedHeaders(builder.Configuration);

// BMB_INTERNAL_KEY: the secret the console and the CLI present on every call (X-Internal-Key). In production the
// entrypoint exports it before the process starts; refuse to run without it - it means the entrypoint was bypassed.
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
// Same vault gate as every node: refused while an offline re-key runs, an interrupted swap finished or rolled
// back, and vault.lease held shared for the life of the process.
var (rekeySwap, vaultLease) = BeeMemoryBank.AppPaths.VaultStartup.Enter(dataPath);
dataPath = rekeySwap.DataDir;
// One node process per data folder: the lease above is shared by design, so it would let a second container on the same
// volume start and write different events under this node's id (week review F4).
var instanceLock = BeeMemoryBank.AppPaths.InstanceGuard.AcquireOrExit(dataPath);
// Created by a factory so the host disposes it (an instance registration is never disposed).
builder.Services.AddSingleton<BeeMemoryBank.AppPaths.VaultLease>(_ => vaultLease);

Directory.CreateDirectory(dataPath);

// Development only: a key from a local file, so the console process started by hand shares it.
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

builder.AddBlindNodeServices(dataPath);

var app = builder.Build();
app.Lifetime.ApplicationStopped.Register(instanceLock.Dispose); // the folder is free again once this host has stopped
app.Services.GetRequiredService<BeeMemoryBank.AppPaths.VaultLease>(); // the lease now lives, and ends, with the host

app.UseLoopbackForwardedHeaders();
await app.RunBlindNodeStartupTasksAsync(dataPath);
// The first start on a swapped-in vault succeeded: the re-key journal and lock go.
BeeMemoryBank.AppPaths.RekeySwapResolver.CompleteFirstStart(dataPath);
app.UseBlindNodePipeline();
app.MapBlindNodeSurface();

app.Run();

// Required for WebApplicationFactory in tests
public partial class Program { }
