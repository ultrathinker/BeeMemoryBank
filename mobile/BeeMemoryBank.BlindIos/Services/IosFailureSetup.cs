using BeeMemoryBank.Busy;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>
/// The one thing the iPhone head tells the shared busy helper about failures: its closed text. Every dialog and every result a page gets from <see cref="BusyAction"/> for a failed action - the
/// default dialog, a result label, a custom result callback, <see cref="BusyAction.Describe"/> - is made by <see cref="BusyAction.Describer"/>, so a page never formats an exception and the helper's
/// own default (which says the text of a local failure: a path, a key name, a library's words) is never reached. Called first thing at start (<c>MauiProgram</c>), before any page exists.
/// </summary>
public static class IosFailureSetup
{
    public static void Apply() => BusyAction.Describer = IosFailureText.Say;
}
