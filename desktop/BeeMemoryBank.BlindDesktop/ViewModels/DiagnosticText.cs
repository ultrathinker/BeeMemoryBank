using System.Diagnostics;
using System.Text;

namespace BeeMemoryBank.BlindDesktop.ViewModels;

/// <summary>
/// What a DIAGNOSTIC log of the desktop head (the error log file, the console output of the headless self-check) records about an exception: its TYPE and the frames of its stack, and nothing the exception
/// SAID. The message of an exception carries what a support request must not include - a path, an address, a token, a key alias, a word of a library - and so does the message of everything inside it,
/// so no message is read, outer or inner. The frames are the names of methods (<c>Type.Method</c>), taken without file information, so no build path comes with them; the chain inside the exception
/// (an inner exception, the failures of an aggregate) is told by type and frames as well. A screen says the KIND of failure instead (<see cref="FailureNotice"/>); this is for the person who reads the log.
/// </summary>
internal static class DiagnosticText
{
    private const int MaxFrames = 8;
    private const int MaxExceptions = 6;
    private const int MaxLength = 800;

    /// <summary>"IOException at FileStream.Open &lt; Store.Load &lt; App.Start | inner UnauthorizedAccessException at ..." - one line, no message.</summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var line = new StringBuilder();
        var seen = 0;
        var queue = new Queue<(Exception Error, bool Inner)>();
        queue.Enqueue((ex, false));
        while (queue.Count > 0 && seen < MaxExceptions)
        {
            var (current, inner) = queue.Dequeue();
            seen++;
            if (line.Length > 0) line.Append(" | ");
            if (inner) line.Append("inner ");
            line.Append(current.GetType().Name);
            var frames = Frames(current);
            if (frames.Count > 0) line.Append(" at ").Append(string.Join(" < ", frames));

            if (current is AggregateException aggregate)
                foreach (var failure in aggregate.InnerExceptions) queue.Enqueue((failure, true));
            else if (current.InnerException is { } next)
                queue.Enqueue((next, true));
        }
        if (queue.Count > 0) line.Append(" | ...");
        return line.Length <= MaxLength ? line.ToString() : line.ToString()[..MaxLength] + "...";
    }

    private static List<string> Frames(Exception ex)
    {
        var frames = new List<string>();
        try
        {
            // No file information: a frame is a method's name, never the path of the source it was built from.
            foreach (var frame in new StackTrace(ex, fNeedFileInfo: false).GetFrames())
            {
                if (frame.GetMethod() is not { } method) continue;
                frames.Add(method.DeclaringType is { } type ? $"{type.Name}.{method.Name}" : method.Name);
                if (frames.Count == MaxFrames) break;
            }
        }
        catch (Exception)
        {
            // A trace that cannot be read leaves the type alone.
        }
        return frames;
    }
}
