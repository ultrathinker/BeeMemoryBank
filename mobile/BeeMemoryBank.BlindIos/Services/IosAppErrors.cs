using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>
/// Where an error of the APP as a whole is told - going to the background, opening a link, starting the copy. It belongs to the device, not to an account, so it goes to the
/// DEVICE's log (<see cref="IosBlindRuntime.DeviceLog"/>, the one <see cref="IosBackgroundRecorder"/> writes the wakes to), by the kind of failure (<see cref="IosFailureText"/>) and never
/// by the text of the exception. No account's log is touched: on a phone with several accounts the first one is not "the" account of an app-wide error. Platform-free, so the rule is
/// tested with two accounts.
/// </summary>
public static class IosAppErrors
{
    /// <summary>The kind of log line an app-wide error is written under.</summary>
    public const string Kind = "app";

    /// <summary>Writes "<paramref name="what"/>: &lt;kind of failure&gt;" to the device's log. Never throws: there is nowhere left to tell a failure of the log itself.</summary>
    public static void Report(IosBlindRuntime runtime, string what, Exception error)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Report(runtime.DeviceLog, what, error);
    }

    public static void Report(BlindPhoneLog deviceLog, string what, Exception error)
    {
        try { BlindRunReport.Record(deviceLog, Kind, $"{what}: {IosFailureText.Say(error)}"); }
        catch (Exception) { /* nowhere left to tell it */ }
    }
}
