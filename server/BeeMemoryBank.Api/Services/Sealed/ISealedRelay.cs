using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Api.Services.Sealed;

/// <summary>
/// The sync routes a sealed node answers from its boot store while the database is closed or importing (docs/design/step-b-spec.md 6.1).
/// Implemented by track 2; the sealed-vault middleware (track 3) hands such requests to it instead of the endpoints of SyncEndpoints.cs.
/// </summary>
public interface ISealedRelay
{
    /// <summary>True for a path and method of the relay set (spec 6.1 table).</summary>
    bool Handles(PathString path, string method);

    /// <summary>Answers the request completely (status, body). Never opens the sealed database.</summary>
    Task HandleAsync(HttpContext context);
}
