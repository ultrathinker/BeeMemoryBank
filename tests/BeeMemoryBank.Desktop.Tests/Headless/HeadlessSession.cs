using Avalonia.Headless;

namespace BeeMemoryBank.Desktop.Tests.Headless;

/// <summary>
/// One headless Avalonia session for the whole test assembly: a single UI thread, a single
/// Application, created lazily on the first test that needs it. Tests dispatch their actions onto
/// that thread; nothing here runs in parallel with itself.
/// </summary>
internal static class HeadlessSession
{
    private static readonly Lazy<HeadlessUnitTestSession> LazySession = new(
        () => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static Task RunAsync(Action action) =>
        LazySession.Value.Dispatch(action, CancellationToken.None);

    public static Task<T> RunAsync<T>(Func<T> action) =>
        LazySession.Value.Dispatch(action, CancellationToken.None);
}