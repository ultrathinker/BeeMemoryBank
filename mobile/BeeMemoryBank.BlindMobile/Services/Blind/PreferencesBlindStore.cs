using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// One SharedPreferences file as the platform answers, with no Android type in it. Every write says whether the platform really wrote it (<c>Editor.commit()</c>, <c>Context.deleteSharedPreferences</c>
/// return false when it did not): <see cref="PreferencesBlindStore"/> takes a false as a failure, and the tests give it a file that answers false, which an in-memory dictionary cannot.
/// </summary>
public interface IBlindPreferencesFile
{
    string? GetString(string key);

    /// <summary>Puts the value and commits; false when the commit did not write.</summary>
    bool PutString(string key, string value);

    /// <summary>Removes the key and commits; false when the commit did not write.</summary>
    bool RemoveKey(string key);

    /// <summary>Removes every key and commits; false when the commit did not write.</summary>
    bool Clear();

    /// <summary>Deletes the file itself; false when it is still there afterwards.</summary>
    bool Delete();
}

/// <summary><see cref="IBlindPhoneStore"/> over one SharedPreferences file. Holds nothing secret.</summary>
public sealed class PreferencesBlindStore : IBlindStateStore
{
    private readonly Func<string, string?> _getter;
    private readonly Action<string, string> _setter;
    private readonly Action<string> _remover;
    private readonly Action? _eraser;

#if ANDROID
    /// <summary>
    /// The state of one account lives in an own SharedPreferences file (<see cref="BlindAccountNames.AndroidPreferencesName"/>: <c>bmb_blind_state</c> for the account of a 2.6 install, <c>bmb_blind_state.&lt;id&gt;</c>
    /// for every other) and every write is a <c>commit()</c>, which returns after the file is written. MAUI Preferences uses <c>apply()</c> (a background write): "Disconnect and wipe" cleared the state through it and
    /// killed the process at once, and the app came back still paired, with the old node id, over an empty database.
    /// </summary>
    public static PreferencesBlindStore ForFile(string preferencesName) => new(new AndroidFile(preferencesName));

    private sealed class AndroidFile(string name) : IBlindPreferencesFile
    {
        private global::Android.Content.ISharedPreferences Prefs() =>
            global::Android.App.Application.Context.GetSharedPreferences(name, global::Android.Content.FileCreationMode.Private)!;

        public string? GetString(string key) => Prefs().GetString(key, null);

        public bool PutString(string key, string value)
        {
            using var editor = Prefs().Edit()!;
            editor.PutString(key, value);
            return editor.Commit();
        }

        public bool RemoveKey(string key)
        {
            using var editor = Prefs().Edit()!;
            editor.Remove(key);
            return editor.Commit();
        }

        public bool Clear()
        {
            using var editor = Prefs().Edit()!;
            editor.Clear();
            return editor.Commit();
        }

        public bool Delete() => global::Android.App.Application.Context.DeleteSharedPreferences(name);
    }
#endif

    public PreferencesBlindStore(Func<string, string?> getter, Action<string, string> setter, Action<string> remover, Action? eraser = null)
    {
        _getter = getter;
        _setter = setter;
        _remover = remover;
        _eraser = eraser;
    }

    /// <summary>
    /// The store over a platform file. A write the platform reports as not done is an <see cref="IOException"/> (a fixed sentence: no key, no value, no file name), never a silent success: the removal of an account
    /// leaves it in <c>deleting</c> and runs again, and a state that was not written is not taken as written. Every erase step runs even when an earlier one failed, and all failures are reported together.
    /// </summary>
    public PreferencesBlindStore(IBlindPreferencesFile file) : this(
        file.GetString,
        (key, value) => Written(file.PutString(key, value), "The state of this account was not written."),
        key => Written(file.RemoveKey(key), "The state of this account was not written."),
        () => EraseFile(file))
    {
    }

    private static void Written(bool done, string sentence)
    {
        if (!done) throw new IOException(sentence);
    }

    private static void EraseFile(IBlindPreferencesFile file)
    {
        // EVERY step is tried, whatever an earlier one did: the keys out and committed, then the file deleted (a removed account leaves no preferences file behind). A step that answers false OR throws
        // is a failure of the erase; the failures are reported together, with fixed words (never the platform's own text), and the account stays "deleting" for the retry.
        var failed = new List<string>();
        List<Exception>? causes = null;
        Step(file.Clear, "its keys were not cleared");
        Step(file.Delete, "its file was not deleted");
        if (failed.Count == 0) return;
        var sentence = "The state of this account was not erased: " + string.Join(" and ", failed) + ".";
        throw causes is null ? new IOException(sentence) : new IOException(sentence, causes.Count == 1 ? causes[0] : new AggregateException(causes));

        void Step(Func<bool> action, string what)
        {
            try
            {
                if (!action()) failed.Add(what);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed.Add(what);
                (causes ??= []).Add(ex);
            }
        }
    }

    public string? Get(string key) => _getter(key);

    public void Set(string key, string? value)
    {
        if (value is null) _remover(key);
        else _setter(key, value);
    }

    /// <summary>Removes everything this account's state file held (and the file, where the host can): the wipe and the removal of an account call it after every key was set to null. Throws when the platform did not do it.</summary>
    public void Erase() => _eraser?.Invoke();
}

/// <summary>
/// One state store per account, over one preferences file each, named by <see cref="BlindAccountNames.AndroidPreferencesName"/>. The state is not secret (call code, times, schedule); what it must not be is shared:
/// two accounts never read or write one file, and erasing one account leaves the other.
/// </summary>
public sealed class PreferencesBlindStateStoreFactory(Func<string, PreferencesBlindStore> open) : IBlindStateStoreFactory
{
#if ANDROID
    /// <summary>The factory over the app's own SharedPreferences files.</summary>
    public static PreferencesBlindStateStoreFactory OverAndroidPreferences() => new(PreferencesBlindStore.ForFile);
#endif

    public IBlindStateStore For(BlindAccountEntry account) => open(BlindAccountNames.AndroidPreferencesName(account));
}
