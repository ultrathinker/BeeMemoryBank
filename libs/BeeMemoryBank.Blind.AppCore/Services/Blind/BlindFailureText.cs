using System.Data.Common;
using System.Security.Cryptography;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// What a blind app's screen or log says about an exception it caught, decided by the TYPE of the failure and never by reading its text (the rule of
/// <see cref="FailureText"/>, applied to a head):
/// <list type="bullet">
/// <item>anything in the chain that is a failure of a call to the other computer (HTTP, sockets, TLS, JSON, timeouts, the typed refusals of a peer: <see cref="FailureText.IsPeerFailure"/>)
///   is told as the KIND of failure from the closed set of <see cref="PeerContactLog.Describe"/> - no address, no redirect target, no token, no word of the other side. This holds
///   however many exceptions of this app wrap it (<see cref="BlindHttpHandler"/> puts the request URI and a peer-supplied redirect location into its messages);</item>
/// <item>a LOCAL failure is told by a deliberately small bounded text. The TYPE of an exception does not prove where its message came from (the framework, a library or the other side throw
///   <see cref="InvalidOperationException"/>, <see cref="ArgumentException"/> and <see cref="UnauthorizedAccessException"/> with a URL, a path, an account id or a token in the words), so a
///   sentence is said as it is ONLY when the exception carries the mark of <see cref="IBlindOwnSentence"/> / <see cref="BlindOwnSentence"/> - this code threw it on purpose, to be read
///   (<see cref="BlindSentenceException"/>, <see cref="BlindFileSentenceException"/>, <see cref="BlindRegistryDamagedException"/>, <see cref="BlindNotEnoughRoomException"/>, a marked
///   <see cref="ArgumentException"/> or <see cref="InvalidDataException"/>) - cut to one line of at most <see cref="MaxSentence"/> characters, and still not when the sentence names a place
///   (<see cref="LooksLikeALocation"/>). Every other exception, whatever its type or message, is told as its kind (a file, access, a database, a key, a package, stored data), with the name of its
///   type at most - never its message, and never the message of anything inside it.</item>
/// </list>
/// The rule applies where a failure is RECORDED (the log line, the stored last failure, a job's outcome, a card's problem), so that what a head reads later is already clean; a text stored by an
/// earlier version, which holds the raw message, is cleaned when it is read back (<see cref="ForDisplay"/>). See docs/blind-node/FAILURE-TEXT.md.
/// </summary>
public static class BlindFailureText
{
    /// <summary>The longest sentence of this code that is passed on.</summary>
    public const int MaxSentence = 160;

    private const int MaxDepth = 8;

    public static string Say(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        // The outermost failure of a call to the other side names the kind; what wraps it is never read.
        foreach (var candidate in Chain(ex))
            if (FailureText.IsPeerFailure(candidate))
                return PeerContactLog.Describe(candidate);
        return Local(Unwrap(ex));
    }

    /// <summary>The exception and everything inside it, outermost first (an aggregate contributes all of its failures), to a bounded depth.</summary>
    private static IEnumerable<Exception> Chain(Exception ex)
    {
        var queue = new Queue<(Exception Error, int Depth)>();
        queue.Enqueue((ex, 0));
        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            yield return current;
            if (depth >= MaxDepth) continue;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) queue.Enqueue((inner, depth + 1));
            else if (current.InnerException is { } next)
                queue.Enqueue((next, depth + 1));
        }
    }

    /// <summary>An exception that only wraps another one says what the other says.</summary>
    private static Exception Unwrap(Exception ex)
    {
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            Exception? next = ex switch
            {
                AggregateException aggregate => aggregate.InnerExceptions.FirstOrDefault(),
                TypeInitializationException or System.Reflection.TargetInvocationException => ex.InnerException,
                _ => null,
            };
            if (next is null) return ex;
            ex = next;
        }
        return ex;
    }

    private static string Local(Exception ex) => ex switch
    {
        OperationCanceledException => "it was cancelled",
        KeyNotFoundException => "the account is not there any more",
        // A sentence this code threw on purpose to be read (the mark). Nothing else is passed on by its words: not an InvalidOperationException or an ArgumentException (the framework and the
        // libraries throw those with paths, URLs and ids in them), and not the message of an UnauthorizedAccessException ("Access to the path '...' is denied").
        _ when OwnSentence(ex) is { } sentence => sentence,
        UnauthorizedAccessException => "access was denied",
        IOException => "a file or the storage of this device could not be read or written",
        DbException => "the database of this copy could not be used",
        CryptographicException => "a key or encrypted data could not be used",
        FormatException => "stored data could not be read",
        InvalidDataException => "a package or file could not be read or verified",
        _ => $"an unexpected problem ({ex.GetType().Name})",
    };

    /// <summary>
    /// The message of an exception that carries the mark of this code's own sentence (<see cref="BlindOwnSentence.IsMarked"/>), bounded to one line; null for any other exception and for a
    /// sentence that names a place.
    /// </summary>
    private static string? OwnSentence(Exception ex)
    {
        if (!BlindOwnSentence.IsMarked(ex)) return null;
        var sentence = OneLine(ex.Message);
        return sentence.Length > 0 && !LooksLikeALocation(sentence) ? Bounded(sentence) : null;
    }

    /// <summary>What <see cref="ForDisplay"/> shows in place of a stored text that names a file, an address or a key.</summary>
    public const string StoredTextHidden = "The details were written by an older version of this app and are not shown.";

    /// <summary>The longest stored text <see cref="ForDisplay"/> passes on.</summary>
    public const int MaxStored = 800;

    /// <summary>
    /// The stamp that a line this code stores carries (the <c>v</c> of a line of the phone's log): the line was written by this version, from a text that went through this formatter (a failure
    /// through <see cref="Say"/>) or that this code composed itself. Versions up to 2.6.4 stored the raw message of an exception and no stamp; what a stored text says cannot tell the two apart,
    /// so the stamp does: a stored text without it is never shown (<see cref="ForDisplay(string?, int)"/>). The stamp is provenance, not authentication: it is not a defence against a person who
    /// can write the app's private folder, only against the text an older version left behind.
    /// </summary>
    public const int StoredVersion = 2;

    /// <summary>
    /// A STORED text made fit for a screen by where it came from: without the exact stamp of this version (<paramref name="version"/> other than <see cref="StoredVersion"/>) it is replaced by
    /// <see cref="StoredTextHidden"/>, whatever it says; with the stamp it goes through <see cref="ForDisplay(string?)"/>. Idempotent.
    /// </summary>
    public static string ForDisplay(string? stored, int version) => version != StoredVersion ? StoredTextHidden : ForDisplay(stored);

    /// <summary>
    /// The second layer of the rule for a text that is stamped, or that exists only in memory and was made by <see cref="Say"/> in this process: not a guarantee, a cheap check. One line, no
    /// control characters, at most <see cref="MaxStored"/> characters, and a text that looks like a path, an address or a key alias (<see cref="LooksLikeALocation"/>) is replaced by
    /// <see cref="StoredTextHidden"/>. What was written by 2.6.4 is NOT decided here (the raw message of an exception can name a parameter, a token or an account id without any
    /// of those marks): it is decided by the stamp (<see cref="ForDisplay(string?, int)"/>). Idempotent; null and white space are the empty text.
    /// </summary>
    public static string ForDisplay(string? stored)
    {
        var line = new string(OneLine(stored).Replace('\t', ' ').Where(c => !char.IsControl(c)).ToArray());
        if (line.Length == 0) return "";
        if (LooksLikeALocation(line)) return StoredTextHidden;
        return line.Length <= MaxStored ? line : line[..MaxStored] + "...";
    }

    /// <summary>True for a text that holds a path separator, an address (<c>://</c>) or the name of one of this app's keys (<c>bmb_blind_</c>).</summary>
    public static bool LooksLikeALocation(string text) =>
        text.Contains('\\') || text.Contains('/') || text.Contains("://", StringComparison.Ordinal) || text.Contains("bmb_blind_", StringComparison.OrdinalIgnoreCase);

    /// <summary><see cref="Say"/> with the name of the exception's type in front ("InvalidOperationException: ..."), unless the sentence already names it; for a place that is read by whoever diagnoses the problem.</summary>
    public static string Typed(Exception ex)
    {
        var said = Say(ex);
        var type = ex.GetType().Name;
        return said.Contains(type, StringComparison.Ordinal) ? said : $"{type}: {said}";
    }

    /// <summary>The text without one closing full stop (not the "..." of a cut sentence), for a place that puts it in brackets or goes on with a sentence of its own.</summary>
    internal static string WithoutFullStop(string text) => text.EndsWith('.') && !text.EndsWith("...", StringComparison.Ordinal) ? text[..^1] : text;

    /// <summary><see cref="Say"/> as a sentence that ends in a full stop, for a place that goes on with another sentence.</summary>
    public static string Sentence(Exception ex)
    {
        var said = Say(ex);
        return said.EndsWith('.') ? said : said + ".";
    }

    private static string OneLine(string? message) => (message ?? "").ReplaceLineEndings(" ").Trim();

    private static string Bounded(string sentence)
    {
        var clean = new string(sentence.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= MaxSentence ? clean : clean[..MaxSentence] + "...";
    }
}
