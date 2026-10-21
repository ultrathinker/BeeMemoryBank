using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>
/// An exception in one line for a log or a screen of the iPhone head. It is <see cref="BlindFailureText"/> - the shared rule for every blind app, decided by the TYPE of the failure and never by
/// reading its message: a failure of a call to the other computer (however many exceptions wrap it) is the kind of failure from the closed set of <c>PeerContactLog.Describe</c>, a local failure
/// is a bounded sentence of this code or just its kind - with one iOS addition: a Keychain error says its OSStatus number (a code of the system, not a message). Every place of the head that
/// shows or logs a caught exception goes through here; no page reads an exception's message.
/// </summary>
public static class IosFailureText
{
    public static string Say(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex is IosKeychainException { Status: not 0 } keychain
            ? $"the Keychain did not answer (OSStatus {keychain.Status})"
            : BlindFailureText.Say(ex);
    }
}
