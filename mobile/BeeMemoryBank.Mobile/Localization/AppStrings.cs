using System.Globalization;
using System.Resources;

namespace BeeMemoryBank.Mobile.Localization;

/// <summary>
/// The interface strings of the app, in the language of the phone. The English text lives in
/// <c>Resources/Strings/AppResources.resx</c> (the default), the Russian text in
/// <c>AppResources.ru.resx</c>; the resource manager picks the satellite assembly by
/// <see cref="CultureInfo.CurrentUICulture"/>, which Android sets from the device locale.
/// A locale change re-creates the activity, so every screen re-reads its strings.
/// </summary>
public static class AppStrings
{
    private static readonly ResourceManager Manager =
        new("BeeMemoryBank.Mobile.Resources.Strings.AppResources", typeof(AppStrings).Assembly);

    /// <summary>The string for <paramref name="key"/> in the phone's language; the key itself when it is missing (never a blank screen).</summary>
    public static string Get(string key)
        => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>The same, with placeholders filled (<c>{0}</c>, <c>{1}</c> ...).</summary>
    public static string Format(string key, params object[] args)
        => string.Format(CultureInfo.CurrentUICulture, Get(key), args);
}
