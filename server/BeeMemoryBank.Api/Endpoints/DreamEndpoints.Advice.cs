using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Core.Exceptions;

namespace BeeMemoryBank.Api.Endpoints;

// GET /api/dream/advice and /advice/{id}: the open list and one advice.
public static partial class DreamEndpoints
{
    private const int MaxAdvicePageSize = 100;

    /// <summary>What <c>status=open</c> means: both states of an advice nobody has finished with.</summary>
    private const string OpenStatusFilter = "open";

    /// <param name="status">A DreamAdviceStatus wire name, or open for new and in_chat together; default new.</param>
    /// <param name="kind">A DreamAdviceKind wire name, or none.</param>
    /// <param name="sort">score or date (DreamAdviceSort).</param>
    private static async Task<IResult> ListAdviceAsync(HttpContext ctx, string? status, string? kind, int? minScore, string? sort,
        int? page, int? pageSize)
    {
        IReadOnlyList<DreamAdviceStatus> statuses;
        if (status == OpenStatusFilter) statuses = [DreamAdviceStatus.New, DreamAdviceStatus.InChat];
        else if (status is null) statuses = [DreamAdviceStatus.New];
        else if (DreamWire.TryParse<DreamAdviceStatus>(status, out var one)) statuses = [one];
        else return BadRequest("Unknown status.");

        DreamAdviceKind? kindFilter = null;
        if (kind is not null)
        {
            if (!DreamWire.TryParse<DreamAdviceKind>(kind, out var parsed)) return BadRequest("Unknown kind.");
            kindFilter = parsed;
        }

        var order = DreamAdviceSort.Score;
        if (sort is not null && !DreamWire.TryParse(sort, out order)) return BadRequest("Unknown sort.");

        var (safePage, size) = Paging(page, pageSize, 20, MaxAdvicePageSize);
        try
        {
            var (total, items) = await ctx.RequestServices.GetRequiredService<DreamStore>()
                .ListAdviceAsync(new DreamAdviceQuery(statuses, kindFilter, minScore, order, safePage, size));

            // Checked as served (plan 3.4): one that no longer stands comes back expired, with its reason, this once.
            var checkedItems = await ctx.RequestServices.GetRequiredService<DreamAdviceValidator>().CheckListAsync(items);
            return Results.Ok(new DreamAdviceListResponse(total, checkedItems.Select(a => a.ToDto()).ToList()));
        }
        catch (SessionLockedException)
        {
            return Locked();
        }
    }

    private static async Task<IResult> GetAdviceAsync(HttpContext ctx, Guid id)
    {
        try
        {
            var advice = await ctx.RequestServices.GetRequiredService<DreamStore>().GetAdviceAsync(id);
            if (advice is null) return NotFound("Advice");
            return Results.Ok((await ctx.RequestServices.GetRequiredService<DreamAdviceValidator>().CheckOneAsync(advice)).ToDto());
        }
        catch (SessionLockedException)
        {
            return Locked();
        }
    }
}
