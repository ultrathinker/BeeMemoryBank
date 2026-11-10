using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>
/// <c>/api/dream</c> (plan 3.8): the Dream page, its settings, runs, advice, rules and the trial comparison. Superadmin
/// only and never an agent: Dream sees every user's notes, and there is no Dream over MCP. Mapped on a full node only
/// (after the blind role's early return in ApiPipeline).
///
/// <para>This file only maps the routes; each handler lives in the partial file of the package that fills it, with
/// the signature fixed here: <c>DreamEndpoints.Status.cs</c> and <c>.Runs.cs</c> and <c>.Advice.cs</c> (P0b),
/// <c>.Actions.cs</c> (A1), <c>.Compare.cs</c> (C1). A handler may take more services as parameters, but keeps its
/// route, its body parameter (or none) and its <c>Task&lt;IResult&gt;</c> return: those decide the route metadata
/// pinned in docs/full-node/ROUTES.golden.txt. A handler never takes <c>HttpContext</c> alone: that signature binds as a
/// <c>RequestDelegate</c>, whose returned IResult is silently dropped (an empty 200), hence the CancellationToken on the
/// handlers that need nothing else.</para>
///
/// <para>Calls that change something answer 409 <c>locked</c> while the vault is locked (<see cref="DreamUnlockedFilter"/>);
/// reads answer from the plaintext columns or say what they cannot open. Errors are <see cref="ErrorResponse"/> with a
/// code from <see cref="DreamErrorCodes"/>.</para>
/// </summary>
public static partial class DreamEndpoints
{
    public static void MapDreamEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/dream").WithTags("Dream")
            .RequireInternalKey().RequireSuperadmin().RequireNonAgent();

        // State and settings (P0b)
        group.MapGet("/status", GetStatusAsync);
        group.MapGet("/settings", GetSettingsAsync);
        group.MapPut("/settings", PutSettingsAsync).RequireUnlockedVault();

        // Runs (P0b)
        group.MapPost("/runs", StartRunAsync).RequireUnlockedVault();
        group.MapPost("/runs/{id:guid}/stop", StopRunAsync);
        group.MapGet("/runs", ListRunsAsync);
        group.MapGet("/runs/{id:guid}", GetRunAsync);
        group.MapGet("/runs/{id:guid}/advice", GetRunAdviceAsync);
        group.MapGet("/runs/{id:guid}/events", GetRunEventsAsync);

        // Advice lists (P0b)
        group.MapGet("/advice", ListAdviceAsync);
        group.MapGet("/advice/{id:guid}", GetAdviceAsync);

        // Acting on advice, and the dismiss rules (A1)
        group.MapGet("/advice/{id:guid}/prompt", GetAdvicePromptAsync);
        group.MapPost("/advice/{id:guid}/apply", ApplyAdviceAsync).RequireUnlockedVault();
        group.MapPost("/advice/{id:guid}/dismiss", DismissAdviceAsync).RequireUnlockedVault();
        group.MapPost("/advice/{id:guid}/status", SetAdviceStatusAsync).RequireUnlockedVault();
        group.MapGet("/rules", ListRulesAsync);
        // What the built-in rules and the model tasks do, for the "View all rules" dialog (package ux2a)
        group.MapGet("/rulebook", GetRuleBookAsync);
        group.MapDelete("/rules/{id:guid}", DeleteRuleAsync).RequireUnlockedVault();

        // Trial comparison (C1)
        group.MapGet("/compare", CompareRunsAsync);
    }

    private static RouteHandlerBuilder RequireUnlockedVault(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<DreamUnlockedFilter>();

    /// <summary>The answer of a handler whose package has not filled it yet.</summary>
    internal static IResult NotImplementedYet() =>
        Results.Json(new ErrorResponse("This Dream call is not available yet.", "not_implemented"), statusCode: 501);

    internal static IResult Locked() =>
        Results.Json(new ErrorResponse("The vault is locked.", DreamErrorCodes.Locked), statusCode: 409);

    internal static IResult NotFound(string what) =>
        Results.Json(new ErrorResponse($"{what} not found.", DreamErrorCodes.NotFound), statusCode: 404);

    internal static IResult BadRequest(string message) =>
        Results.Json(new ErrorResponse(message, DreamErrorCodes.InvalidRequest), statusCode: 400);

    internal static IResult Conflict(string message, string code) =>
        Results.Json(new ErrorResponse(message, code), statusCode: 409);
}

/// <summary>
/// 409 <c>locked</c> for a Dream call that changes something while the vault is locked: Dream never unlocks anything
/// itself, and every write either seals under the chat key or acts on the vault.
/// </summary>
public sealed class DreamUnlockedFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var session = context.HttpContext.RequestServices.GetRequiredService<SessionService>();
        if (!session.IsUnlocked)
            return DreamEndpoints.Locked();
        return await next(context);
    }
}
