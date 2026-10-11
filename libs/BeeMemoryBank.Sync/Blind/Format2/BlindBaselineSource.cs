namespace BeeMemoryBank.Sync.Blind.Format2;

/// <summary>
/// What a blind host serves (docs/design/step-a-spec.md 9.7): the baseline it adopted, with the position of its own log that
/// baseline covers. Null until the first format-2 adoption; an activated host then answers 503 instead of a format-1 package.
/// </summary>
public sealed class BlindBaselineSource(IBaselineStore store) : IBaselineSource
{
    public async Task<ServableBaseline?> GetServableAsync(CancellationToken ct = default) =>
        await store.GetCurrentAsync(ct) is { } current ? new ServableBaseline(current.Files, current.LocalCut) : null;
}
