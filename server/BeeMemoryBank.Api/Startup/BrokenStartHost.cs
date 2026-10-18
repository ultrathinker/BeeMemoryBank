using System.Text.Json.Serialization;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.Api.Startup;

/// <summary>
/// The host of a data directory that cannot start (docs/design/step-b-spec.md 2.5, 10.6, red test 15.3.18): an encrypted database beside a plain
/// companion, a boot store without its database, leftovers of an interrupted "Encrypt this node" that cannot be placed safely. The node refuses
/// to start - it opens, creates, moves, changes and sweeps nothing, holds no key source, no relay and no hosted service - but it stays up long
/// enough to say why: the start message and the log carry the reason and the relative file names, <c>GET /api/vault/status</c> answers
/// <c>broken_reason</c> and the names (no content), <c>/health</c> is 503, and every other route is 503 <c>vault_broken</c>.
/// </summary>
public static class BrokenStartHost
{
    public const string ErrorCode = "vault_broken";

    public sealed record BrokenStatus(
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("broken_reason")] string BrokenReason,
        [property: JsonPropertyName("files")] IReadOnlyList<string> Files,
        [property: JsonPropertyName("message")] string Message);

    /// <summary>
    /// Takes over the builder <c>Program</c> already made (a test host has configured it) and runs the refusal. Returns the exit code. The vault lease the start
    /// took is the host's: it is held while the refusal is served and released with the host, so a start that follows the stop (once whatever was in the way is
    /// gone) is not turned away by the lease of the one that refused.
    /// </summary>
    public static int Run(WebApplicationBuilder builder, BrokenStart broken, BeeMemoryBank.AppPaths.VaultLease lease)
    {
        builder.Services.AddSingleton(_ => lease);   // a factory, so the host disposes it (an instance registration is never disposed)
        // The start message: on stderr for whoever runs the node by hand, and in the log for whoever reads it afterwards.
        Console.Error.WriteLine("[Error] " + broken.Message);

        var app = builder.Build();
        _ = app.Services.GetRequiredService<BeeMemoryBank.AppPaths.VaultLease>();   // resolved once, or the host has nothing of it to dispose
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogError("The node cannot start: {Reason} ({Files}). {Message}", broken.Reason, string.Join(", ", broken.Files), broken.Message);

        var status = new BrokenStatus("broken", "Broken", broken.Reason, broken.Files, broken.Message);
        app.MapGet("/health", () => Results.Json(new { status = "broken", broken_reason = broken.Reason }, statusCode: StatusCodes.Status503ServiceUnavailable));
        app.MapGet("/api/vault/status", () => Results.Json(status));
        app.MapFallback(() => Results.Json(new ErrorResponse(broken.Message, ErrorCode), statusCode: StatusCodes.Status503ServiceUnavailable));
        app.Run();
        return 1;
    }
}
