using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary><see cref="IBlindPhoneStore"/> over MAUI Preferences. Holds nothing secret.</summary>
public sealed class PreferencesBlindStore : IBlindPhoneStore
{
    private readonly Func<string, string?> _getter;
    private readonly Action<string, string> _setter;
    private readonly Action<string> _remover;

#if ANDROID || IOS || MACCATALYST || WINDOWS
    public PreferencesBlindStore() : this(
        key => Microsoft.Maui.Storage.Preferences.Default.Get<string?>(key, null),
        (key, val) => Microsoft.Maui.Storage.Preferences.Default.Set(key, val),
        key => Microsoft.Maui.Storage.Preferences.Default.Remove(key))
    {
    }
#endif

    public PreferencesBlindStore(Func<string, string?> getter, Action<string, string> setter, Action<string> remover)
    {
        _getter = getter;
        _setter = setter;
        _remover = remover;
    }

    public string? Get(string key) => _getter(key);

    public void Set(string key, string? value)
    {
        if (value is null) _remover(key);
        else _setter(key, value);
    }
}
