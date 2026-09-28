using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Rekey;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// The report of the last offline re-key (rekey-offline.md §8.4): <c>rekey-report.json</c> in the data directory,
/// written by <c>bmb rekey</c>, read here as it is. Superadmin only and never an agent: it names the old vault's
/// folder, the revoked peers and the cleared slots.
/// </summary>
public static class RekeyReportEndpoints
{
    public static void MapRekeyReportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/rekey")
            .WithTags("Rekey")
            .RequireInternalKey().RequireSuperadmin().RequireNonAgent();

        // GET /api/rekey/report — 404 when this vault was never re-keyed.
        group.MapGet("/report", (IConfiguration config) =>
        {
            var dataPath = ResolveDataPath(config);
            RekeyReport? report;
            try
            {
                report = RekeyReport.TryRead(dataPath);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return Results.Problem($"{RekeyReport.FileName} cannot be read: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
            }
            if (report is null) return Results.NotFound();
            return Results.Ok(new
            {
                report,
                oldVaultExists = report.OldVault is { Length: > 0 } old && Directory.Exists(old),
            });
        });
    }

    private static string ResolveDataPath(IConfiguration config) =>
        config["BeeMemoryBank:DataPath"]
        ?? Environment.GetEnvironmentVariable("BMB_DATA_PATH")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
}
