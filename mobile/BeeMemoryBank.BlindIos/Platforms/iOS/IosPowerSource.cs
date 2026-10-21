using BeeMemoryBank.BlindIos.Services;
using Foundation;

namespace BeeMemoryBank.BlindIos.Platforms.iOS;

/// <summary>Low Power Mode (<c>NSProcessInfo.LowPowerModeEnabled</c>) is the phone's battery saver. Read live; iOS always knows it.</summary>
public sealed class IosPowerSource : IIosPowerSource
{
    public bool? LowPowerModeEnabled => NSProcessInfo.ProcessInfo.LowPowerModeEnabled;
}
