using BeeMemoryBank.Api.Dream;

namespace BeeMemoryBank.Api.Endpoints;

// GET /api/dream/rulebook: the descriptions of the "View all rules" dialog of the Dream page.
public static partial class DreamEndpoints
{
    /// <summary>
    /// Every built-in rule and model task the node really runs (the detectors and tasks of the container, so a rule added or
    /// taken out shows here at once), described with the thresholds of the run and the limits of the settings. The settings
    /// row is plaintext: this answers while the vault is locked too.
    /// </summary>
    private static async Task<IResult> GetRuleBookAsync(HttpContext ctx, CancellationToken ct)
    {
        var services = ctx.RequestServices;
        var settings = await services.GetRequiredService<DreamStore>().GetSettingsAsync();
        var thresholds = (services.GetService<DreamEngineOptions>() ?? DreamEngineOptions.Default).Thresholds;
        return Results.Ok(DreamRuleBook.Build(services.GetServices<IDreamDetector>(), services.GetServices<IDreamModelTask>(), thresholds, settings));
    }
}
