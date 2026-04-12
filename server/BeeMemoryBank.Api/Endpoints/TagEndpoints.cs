using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Api.Endpoints;

public static class TagEndpoints
{
    public static void MapTagEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tags", async (HttpContext ctx, IArticleRepository repo) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);
            // Tags are returned unfiltered — tag names aren't secret and counts being slightly
            // off (due to restricted folders) is acceptable. Proper filtering would require
            // loading all articles and recalculating counts, which is not worth the cost.
            var tags = await repo.GetAllTagsAsync();
            return Results.Ok(tags.Select(t => new TagResponse(t.Name, t.ArticleCount)));
        }).WithTags("Tags");
    }
}
