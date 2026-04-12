using System.Text.Encodings.Web;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// BMB_INTERNAL_KEY: shared secret for internal auth between Web and API servers.
// When set, admin endpoints require X-Internal-Key header matching this value.
// When unset, admin endpoints are only accessible from localhost (loopback).

var dataPath = builder.Configuration["BeeMemoryBank:DataPath"]
    ?? Environment.GetEnvironmentVariable("BMB_DATA_PATH")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "data");

Directory.CreateDirectory(dataPath);

builder.Services.AddStorage(dataPath);
builder.Services.AddCore();
builder.Services.AddSync();
builder.Services.AddSingleton<SyncTokenStore>();
builder.Services.AddSyncScheduler(periodicCleanupFactory: sp =>
    sp.GetRequiredService<SyncTokenStore>().CleanupExpired);
builder.Services.AddCleanupService();
builder.Services.AddEmbeddingProcessor();
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IActorProvider, BeeMemoryBank.Api.Services.HttpActorProvider>();
builder.Services.AddSingleton(sp =>
    new SnapshotService(dataPath, sp.GetRequiredService<DbConnectionFactory>()));
builder.Services.AddSingleton(new McpResponseManager(dataPath));
var mediaDir = Path.Combine(dataPath, "media");
Directory.CreateDirectory(mediaDir);
builder.Services.AddSingleton(new BeeMemoryBank.Core.Services.MediaStorageOptions(mediaDir));
builder.Services.AddOpenApi();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<BeeSearchTools>()
    .WithTools<BeeReadTools>()
    .WithTools<BeeWriteTools>()
    .WithTools<BeeSessionTools>()
    .WithTools<BeeUploadTools>()
    .WithTools<BeeAuditTools>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

var app = builder.Build();

// Run migrations on startup
using (var scope = app.Services.CreateScope())
{
    var migrator = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
    await migrator.RunMigrationsAsync();
}

// Bootstrap tbl_folder from existing article tree_path values (one-time, idempotent)
using (var scope = app.Services.CreateScope())
{
    var bootstrapper = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Storage.Sqlite.FolderBootstrapper>();
    await bootstrapper.RunIfNeededAsync();
}

// Restore Lamport clock from DB (must run before backfill, which emits new events)
{
    using var scope = app.Services.CreateScope();
    var maxTs = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync();
    app.Services.GetRequiredService<LamportClock>().Initialize(maxTs);
}

// Backfill whitelist_revoke events for rows silently revoked by older JoinEndpoints code
using (var scope = app.Services.CreateScope())
{
    var backfill = scope.ServiceProvider.GetRequiredService<WhitelistRevokeBackfill>();
    var count = await backfill.RunIfNeededAsync();
    if (count > 0)
        scope.ServiceProvider.GetRequiredService<ILogger<Program>>()
            .LogInformation("Backfilled {Count} whitelist_revoke events for silently-revoked nodes", count);
}

// Session cleanup: securely wipe master DEK from memory on shutdown
app.Lifetime.ApplicationStopping.Register(() =>
{
    var session = app.Services.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>();
    session.Lock();
    app.Services.GetRequiredService<ILogger<Program>>()
        .LogInformation("Session locked on application shutdown — master DEK wiped from memory");
});

// Rate limiting for sensitive endpoints (unlock, join) — brute-force protection
app.UseMiddleware<BeeMemoryBank.Api.Middleware.RateLimitMiddleware>();

// Maintenance mode — blocks all requests except snapshot restore and session unlock
app.UseMiddleware<BeeMemoryBank.Api.Middleware.MaintenanceMiddleware>();

// Agent bearer auth (non-blocking, auto-unlock)
app.UseMiddleware<BeeMemoryBank.Api.Middleware.AgentAuthMiddleware>();

// Error handling
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var ex = feature?.Error;
        var (statusCode, message) = ex switch
        {
            KeyNotFoundException e => (404, e.Message),
            ArgumentException e => (400, e.Message),
            UnauthorizedAccessException e => (403, e.Message),
            InvalidOperationException e => (409, e.Message),
            _ => (500, "Internal server error")
        };
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(message));
    });
});

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }))
   .WithTags("Health");

app.MapGet("/api/version", () =>
{
    var asm = System.Reflection.Assembly.GetExecutingAssembly();
    var location = asm.Location;
    var deployedAt = File.Exists(location)
        ? File.GetLastWriteTimeUtc(location)
        : DateTime.UtcNow;
    return Results.Ok(new { deployedAt });
}).WithTags("Health").AllowAnonymous();

app.MapSessionEndpoints();
app.MapArticleEndpoints();
app.MapTreeEndpoints();
app.MapTagEndpoints();
app.MapFolderEndpoints();
app.MapSearchEndpoints();
app.MapKeyEndpoints();
app.MapWhitelistEndpoints();
app.MapAgentEndpoints();
app.MapUserEndpoints();
app.MapJoinEndpoints();
app.MapSyncEndpoints();
app.MapSnapshotEndpoints();
app.MapDeployEndpoints();
app.MapActivityEndpoints();
app.MapCommentEndpoints();
app.MapRestrictionEndpoints();
    app.MapMediaEndpoints();
    app.MapVersionEndpoints();
    app.MapMcp("/mcp");

app.Run();

// Required for WebApplicationFactory in tests
public partial class Program { }
