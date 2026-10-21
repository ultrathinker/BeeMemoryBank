using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.ViewModels;

/// <summary>
/// What a screen of the desktop head says about an exception it caught: the KIND of failure (<see cref="BlindFailureText"/>), never the message of the exception, which can carry a local path, an address,
/// a token or a word of a library. The raw exception goes to the log callback of the view model (the error log) and nowhere else.
/// </summary>
internal static class FailureNotice
{
    /// <summary>"That did not work: a file or the storage of this device could not be read or written." - <paramref name="what"/> is what the person tried.</summary>
    public static string Say(string what, Exception ex)
    {
        var said = BlindFailureText.Say(ex);
        if (!said.EndsWith('.')) said += ".";
        return $"{what}: {said}";
    }
}
