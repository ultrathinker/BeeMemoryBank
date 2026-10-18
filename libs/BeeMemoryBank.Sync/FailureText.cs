using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync;

/// <summary>
/// What a screen, an API answer or a status says about a failure met while working with another device or reading its data (BMB-306/307 fix round 2, T3: the one
/// rule for the whole tree). Two kinds only, decided by the TYPE of the failure and never by looking at its text:
/// <list type="bullet">
/// <item>a failure this code threw ITSELF to be read - an <see cref="InvalidOperationException"/>, an <see cref="ArgumentException"/>, an
///   <see cref="UnauthorizedAccessException"/>, and the typed refusals derived from them - carries a sentence written in this repository, and is said as it is;</item>
/// <item>everything else - HTTP, sockets, TLS, JSON, base64, crypto, I/O, SQLite, whatever the framework or the other side produced - is told as the kind of failure
///   from the closed set of <see cref="PeerContactLog.Describe"/>: no address, no path, no exception text, no word of the other side. The details are in the node's
///   log, where the caller wrote the exception itself.</item>
/// </list>
/// A new place that shows a failure calls this (or <see cref="PeerContactLog.Describe"/> when the failure is a call to a peer); a guard test lists the places that
/// still use <c>.Message</c> so that a new one is reviewed.
/// </summary>
public static class FailureText
{
    public static string Say(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException && !IsWrapper(ex)
            ? ex.Message
            : PeerContactLog.Describe(ex);

    /// <summary>
    /// True for the failures whose own text can carry words of the other side or of the network: an HTTP failure (its message holds the answer's reason phrase and
    /// the address that was dialled), a JSON failure (it names the other side's properties), a socket, TLS or timeout failure. Their text is never shown; they are told
    /// by <see cref="PeerContactLog.Describe"/>.
    /// </summary>
    public static bool IsRemoteInfluenced(Exception ex) =>
        ex is HttpRequestException or System.Text.Json.JsonException or System.Net.Sockets.SocketException
            or System.Security.Authentication.AuthenticationException or TimeoutException or TaskCanceledException;

    /// <summary>
    /// The whole story of a failure for a screen that runs LOCAL work (a button that does something): the text of the failure and of everything inside it, so that
    /// nothing is hidden from the person who has to act. A failure that came from a call to another device - HTTP, sockets, TLS, JSON, timeouts, and the typed
    /// refusals of a peer (<see cref="IsPeerFailure"/>) - is never read: it is told as the kind of failure from <see cref="PeerContactLog.Describe"/>, and nothing
    /// inside it is looked at (an <see cref="HttpRequestException"/> holds the address that was dialled and the other side's words). Every other failure - a disk,
    /// a database, a file the person chose - cannot carry the other side's text, and says what it says. The kinds are told apart by TYPE, never by the text.
    /// </summary>
    public static string Story(Exception ex)
    {
        var parts = new List<string>();
        Collect(ex, parts);
        return parts.Count == 0 ? ex.GetType().Name : string.Join(" ", parts);
    }

    private static void Collect(Exception e, List<string> parts)
    {
        // An aggregate says "One or more errors occurred": what it holds is the news.
        if (e is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions) Collect(inner, parts);
            return;
        }
        if (IsPeerFailure(e))
        {
            var told = PeerContactLog.Describe(e);
            if (!parts.Contains(told)) parts.Add(told);
            return;
        }
        if (!string.IsNullOrWhiteSpace(e.Message) && !parts.Contains(e.Message)) parts.Add(e.Message);
        if (e.InnerException != null) Collect(e.InnerException, parts);
    }

    /// <summary>A failure of a call to another device: the remote-influenced kinds, and the typed refusals the sync code raises about a peer.</summary>
    public static bool IsPeerFailure(Exception ex) =>
        IsRemoteInfluenced(ex)
        || ex is NoSyncServerException or PeerIncompatibleException or PeerIdentityMismatchException or PeerKeyMismatchException
            or PeerAddressRefusedException or PushGapException or SnapshotRequiredException;

    /// <summary>An exception of those types that only wraps another one's text is not a sentence of this code.</summary>
    private static bool IsWrapper(Exception ex) => ex is AggregateException or TypeInitializationException or System.Reflection.TargetInvocationException;
}
