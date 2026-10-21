using BeeMemoryBank.Busy;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// What the Android blind app tells the shared busy helper at start. The helper (<see cref="BusyAction"/>) catches the exception of every action a page runs and shows a dialog or a result label; the story it
/// tells is its <see cref="BusyAction.Describer"/>, and an app that sets none gets the helper's default, which says the text of a local failure (a path, a key alias, a library's words). A blind app must never
/// show those, so this sets the closed rule of the blind core (<see cref="BlindFailureText.Say"/>: by TYPE, never by message) before any page can run an action. It is a method of its own so that a test runs the
/// same line the app runs.
/// </summary>
public static class BlindFailureSetup
{
    public static void Apply() => BusyAction.Describer = BlindFailureText.Say;
}
