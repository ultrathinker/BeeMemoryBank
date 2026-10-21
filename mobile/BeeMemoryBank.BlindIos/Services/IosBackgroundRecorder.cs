using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>
/// Where the outcome of a background wake is written, so that no account's log or state ever holds another account's name, result or fate:
/// <list type="bullet">
/// <item>each account's own result goes ONLY through <c>ServicesFor(thatAccount)</c> - its own "last background round" line and, for a failure or a stop, its own log;</item>
/// <item>what belongs to the whole wake goes to the DEVICE's log (<see cref="IosBlindRuntime.DeviceLog"/>) as account-free text: numbers, never a name or a result, and a failure of the wake itself
///   only by its kind (<see cref="IosFailureText"/>).</item>
/// </list>
/// Platform-free, so the rule is tested with two accounts, one of which fails.
/// </summary>
public static class IosBackgroundRecorder
{
    /// <summary>The kind the writes of the recorder are registered under in an account's <see cref="BlindActivity"/>.</summary>
    private const string RecordingKind = "background-record";

    public static void Record(IosBlindRuntime runtime, string task, IosRoundOutcome outcome, DateTimeOffset? now = null) =>
        Record(runtime.Accounts, runtime.DeviceLog, task, outcome, now);

    public static void Record(IBlindAccountsController accounts, BlindPhoneLog deviceLog, string task, IosRoundOutcome outcome, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        foreach (var round in outcome.Accounts)
        {
            try
            {
                var services = accounts.ServicesFor(round.AccountId);
                // Registered while it writes: "Disconnect and wipe" of this account waits for what is registered and refuses what comes after it, so what the wake writes after its round has ended
                // can never be written into state and a log the wipe has just deleted.
                var activity = services.GetService<BlindActivity>();
                using var operation = activity?.TryBegin(RecordingKind);
                if (activity is not null && operation is null) continue;
                if (round.Ran) services.GetRequiredService<IBlindStateStore>().Set(IosLastRound.StateKey, IosLastRound.Format(at, task, round.Result));
                // The account's own log, and only what is worth a line there: the controller already logs its failed rounds in its own words.
                if (round.Kind is IosRoundKind.Failed or IosRoundKind.Stopped)
                    BlindRunReport.Record(services.GetRequiredService<BlindPhoneLog>(), "background", $"{task}: {round.Result}");
            }
            catch (Exception)
            {
                // the account may be gone, and these are the screen's lines only
            }
        }
        Device(deviceLog, $"{task}: {outcome.DeviceSummary}");
    }

    /// <summary>A failure of the wake itself (not of one account): the device's log, by the kind of failure.</summary>
    public static void RecordFailure(IosBlindRuntime runtime, string task, Exception error) => RecordFailure(runtime.DeviceLog, task, error);

    public static void RecordFailure(BlindPhoneLog deviceLog, string task, Exception error) =>
        Device(deviceLog, $"{task}: the wake failed ({IosFailureText.Say(error)}).");

    private static void Device(BlindPhoneLog deviceLog, string message)
    {
        try { BlindRunReport.Record(deviceLog, "background", message); }
        catch (Exception) { /* nowhere left to tell it */ }
    }
}
