namespace BeeMemoryBank.Api.Dream;

// The examined registry as tree_review sees it: one question, when did this task last examine anything.
public sealed partial class DreamStore : IDreamExaminedLookup
{
    Task<DateTime?> IDreamExaminedLookup.LastExaminedAtAsync(string task, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return GetLastExaminedAtAsync(task);
    }
}
