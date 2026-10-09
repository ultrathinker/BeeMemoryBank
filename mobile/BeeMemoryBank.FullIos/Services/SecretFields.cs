namespace BeeMemoryBank.FullIos.Services;

/// <summary>
/// What a screen does with a password or passphrase it has read from a field (review revios F3): the field is emptied on every way out of
/// the operation that used it — success, a wrong password, any exception, a cancellation — not just the success path, so the secret does
/// not stay in the native text control (an app-switcher screenshot, a shoulder, a later navigation state) after nothing needs it any more.
/// The screens' own try/catch stays around this, to show the error.
/// </summary>
public static class SecretFields
{
    /// <summary>
    /// Runs <paramref name="operation"/>, then calls every <paramref name="clear"/> whatever happened. Not on a background context: the awaits
    /// resume where the screen's controls may be touched. A clear that throws neither skips the others nor replaces the operation's own outcome.
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> operation, params Action[] clear)
    {
        try
        {
            return await operation();
        }
        finally
        {
            Clear(clear);
        }
    }

    /// <inheritdoc cref="RunAsync{T}"/>
    public static async Task RunAsync(Func<Task> operation, params Action[] clear)
    {
        try
        {
            await operation();
        }
        finally
        {
            Clear(clear);
        }
    }

    private static void Clear(Action[] clear)
    {
        foreach (var action in clear)
        {
            try { action(); }
            catch { /* a field that cannot be emptied must not hide why the operation ended */ }
        }
    }
}
