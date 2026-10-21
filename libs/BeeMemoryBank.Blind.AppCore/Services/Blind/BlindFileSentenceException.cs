using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// A failure of the files of a copy whose message is a complete sentence THIS CODE wrote for the person (what was refused and what to do), with no path, key alias or word of the system in it.
/// It carries the mark of <see cref="IBlindOwnSentence"/>, so <see cref="BlindFailureText.Say"/> passes the message on (bounded, and only while it names no place); every other
/// <see cref="IOException"/> is told by its kind, because the message of the framework's own I/O failures carries the path of the file. Derived from <see cref="IOException"/> so that what
/// catches I/O failures still catches it.
/// </summary>
public sealed class BlindFileSentenceException : IOException, IBlindOwnSentence
{
    public BlindFileSentenceException(string sentence) : base(sentence) { }

    public BlindFileSentenceException(string sentence, Exception inner) : base(sentence, inner) { }
}
