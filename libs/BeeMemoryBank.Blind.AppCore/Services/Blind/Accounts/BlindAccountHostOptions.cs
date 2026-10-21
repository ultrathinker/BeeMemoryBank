using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>What a head tells <see cref="BlindAccountHost"/>: where the app's root is, where each account's secrets and state go, and its own seams.</summary>
public sealed class BlindAccountHostOptions
{
    /// <summary>The app's private root: <c>accounts.json</c>, <c>accounts/</c>, and the 2.6 install's files (the legacy account) live here.</summary>
    public required string RootDirectory { get; init; }

    /// <summary>The database file of the legacy account (a 2.6 install's own path); a new account's database is <c>accounts/&lt;id&gt;/beememorybank.db</c>.</summary>
    public required string LegacyDatabasePath { get; init; }

    public required IBlindSecretStoreFactory Secrets { get; init; }

    public required IBlindStateStoreFactory States { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How this device names itself to a source when an account's identity is made (null: "Blind copy").</summary>
    public Func<string>? DisplayNameFactory { get; init; }

    /// <summary>What the device says about its network and its power, for the two switches of an account (the head's adapter); null: the device cannot say, and an account with a switch on waits.</summary>
    public IBlindDeviceConditions? Conditions { get; init; }

    /// <summary>The lock for a long job that every account's runtime shares; null makes the host build one.</summary>
    public BlindDeviceGate? DeviceGate { get; init; }

    /// <summary>The most accounts on this device. 8 unless a test says otherwise.</summary>
    public int MaxAccounts { get; init; } = BlindAccountHost.DefaultMaxAccounts;

    public IBlindAccountLifecycle? Lifecycle { get; init; }

    /// <summary>The courtesy call to the source when an account is removed: <see cref="BlindSourceLeaveHook"/> (<c>POST /api/sync/leave</c>) unless a host or a test says otherwise; null turns it off.</summary>
    public IBlindAccountLeaveHook? LeaveHook { get; init; } = BlindSourceLeaveHook.Instance;

    /// <summary>
    /// True on a phone (Android, iPhone): an account added from an invitation then starts with "only on unmetered networks" and "pause on battery saver" on (<see cref="BlindAccountDefaults.ForInvited"/>).
    /// A computer leaves both off. The head says which it is.
    /// </summary>
    public bool IsPhone { get; init; }

    /// <summary>The host's own services for ONE account (backup picker, notifications, scheduler, autostart...), added before <c>AddBlindAppCore</c> as every head does today.</summary>
    public Action<IServiceCollection, BlindAccountEntry>? ConfigureHostSeams { get; init; }

    /// <summary>Services that replace what <c>AddBlindAppCore</c> registered, added after it (tests put the network side behind fakes this way).</summary>
    public Action<IServiceCollection, BlindAccountEntry>? ConfigureOverrides { get; init; }

    /// <summary>Tests only: the handler at the bottom of an account's HTTP pipeline (see <see cref="BlindAppOptions.PrimaryHandlerFactory"/>).</summary>
    public Func<BlindAccountEntry, Func<HttpMessageHandler>?>? PrimaryHandlerFactory { get; init; }

    /// <summary>Tests only: where a crash is simulated between two writes.</summary>
    public IBlindCrashPoints? CrashPoints { get; init; }

    /// <summary>How long a start waits for another copy of the app (or another host in this process) to let go of the data folder before it is refused (see <see cref="BlindRootLock"/>).</summary>
    public TimeSpan RootLockWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a removal waits for the account's running work to end before it gives up and deletes nothing.</summary>
    public TimeSpan DrainTimeout { get; init; } = BlindPhoneReset.DefaultQuiesceTimeout;

    /// <summary>How long the courtesy call to the source may take.</summary>
    public TimeSpan LeaveTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
