using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary><see cref="IBlindPhoneStore"/> over MAUI Preferences. Holds nothing secret.</summary>
public sealed class PreferencesBlindStore : IBlindStateStore
{
    private readonly Func<string, string?> _getter;
    private readonly Action<string, string> _setter;
    private readonly Action<string> _remover;

#if ANDROID
    /// <summary>
    /// The state lives in the app's own SharedPreferences file and every write is a <c>commit()</c>, which returns after the file
    /// is written. MAUI Preferences uses <c>apply()</c> (a background write): "Disconnect and wipe" cleared the state through it and
    /// killed the process at once, and the app came back still paired, with the old node id, over an empty database.
    /// </summary>
    public PreferencesBlindStore() : this(AndroidState.Get, AndroidState.Set, AndroidState.Remove)
    {
    }

    private static class AndroidState
    {
        private static global::Android.Content.ISharedPreferences Prefs =>
            global::Android.App.Application.Context.GetSharedPreferences("bmb_blind_state", global::Android.Content.FileCreationMode.Private)!;

        public static string? Get(string key) => Prefs.GetString(key, null);

        public static void Set(string key, string value)
        {
            using var editor = Prefs.Edit()!;
            editor.PutString(key, value);
            editor.Commit();
        }

        public static void Remove(string key)
        {
            using var editor = Prefs.Edit()!;
            editor.Remove(key);
            editor.Commit();
        }
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
