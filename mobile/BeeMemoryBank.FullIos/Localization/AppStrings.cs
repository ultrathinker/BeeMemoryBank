using System.Globalization;
using System.Resources;

namespace BeeMemoryBank.FullIos.Localization;

/// <summary>
/// The interface strings of the app, in the language of the phone. The English text lives in
/// <c>Resources/Strings/AppResources.resx</c> (the default), the Russian text in
/// <c>AppResources.ru.resx</c>; the resource manager picks the satellite assembly by
/// <see cref="CultureInfo.CurrentUICulture"/>, which iOS sets from the phone's language
/// (the launch language of the end-to-end check included). Copied from the Android app's
/// localization, the same keys and the same wording on both phones.
/// </summary>
public static class AppStrings
{
    private static readonly ResourceManager Manager =
        new("BeeMemoryBank.FullIos.Resources.Strings.AppResources", typeof(AppStrings).Assembly);

    /// <summary>The string for <paramref name="key"/> in the phone's language; the key itself when it is missing (never a blank screen).</summary>
    public static string Get(string key)
        => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>The same, with placeholders filled (<c>{0}</c>, <c>{1}</c> ...).</summary>
    public static string Format(string key, params object[] args)
        => string.Format(CultureInfo.CurrentUICulture, Get(key), args);
}
