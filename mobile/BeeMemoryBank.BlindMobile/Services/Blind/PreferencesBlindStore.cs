using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary><see cref="IBlindPhoneStore"/> over MAUI Preferences. Holds nothing secret.</summary>
public sealed class PreferencesBlindStore : IBlindPhoneStore
{
    public string? Get(string key) => Preferences.Default.Get<string?>(key, null);

    public void Set(string key, string? value)
    {
        if (value is null) Preferences.Default.Remove(key);
        else Preferences.Default.Set(key, value);
    }
}
