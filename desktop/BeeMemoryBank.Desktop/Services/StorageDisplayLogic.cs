using System;
using BeeMemoryBank.Profiles;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Pure (no Avalonia, no IO) helpers for the storage-UX shell. Extracted so the
/// "≥ 2 profiles" title/tooltip rule from §4.5 and the create-dialog name validation can be
/// unit-tested without spinning up a <c>Window</c> — exactly the carve-out the brief makes
/// for "logic (not UI) → separate testable function/class".
/// </summary>
public static class StorageDisplayLogic
{
    /// <summary>
    /// Window title / tray tooltip text. When two or more profiles exist, the active
    /// profile's name is appended so the user can tell which vault the shell is bound to.
    /// With a single profile the text is the bare product name — "без шума" per §4.5, a
    /// single-account install must be visually indistinguishable from today.
    /// </summary>
    /// <param name="profileCount">Number of profiles currently registered.</param>
    /// <param name="activeProfileName">Display name of the currently-active profile, if any.</param>
    /// <returns>The string to show in <c>MainWindow.Title</c> / tray tooltip.</returns>
    public static string FormatShellTitle(int profileCount, string? activeProfileName)
    {
        const string productName = "BeeMemoryBank";
        if (profileCount < 2 || string.IsNullOrWhiteSpace(activeProfileName))
        {
            return productName;
        }
        return $"{productName} — {activeProfileName}";
    }

    /// <summary>
    /// Overload that resolves the count and name from a <see cref="ProfileService"/> snapshot.
    /// </summary>
    public static string FormatShellTitle(ProfileService profiles, string? activeProfileId)
    {
        if (profiles == null) return FormatShellTitle(0, null);

        var list = profiles.GetAll();
        if (list.Count < 2 || string.IsNullOrEmpty(activeProfileId))
        {
            return FormatShellTitle(list.Count, null);
        }

        string? name = null;
        foreach (var p in list)
        {
            if (string.Equals(p.Id, activeProfileId, StringComparison.Ordinal))
            {
                name = p.Name;
                break;
            }
        }
        return FormatShellTitle(list.Count, name);
    }
}

/// <summary>
/// Result of validating user input from the "create storage" dialog. Carries a
/// human-readable error (Russian, matches the rest of the shell) when invalid, or a trimmed
/// name + an explicitly-normalized data path ready to hand to
/// <see cref="ProfileService.AddProfile"/> when valid.
/// </summary>
public sealed record StorageNameValidation
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public string? Name { get; init; }
    public string? ExplicitDataPath { get; init; }

    public static StorageNameValidation Ok(string name, string? explicitDataPath)
        => new() { IsValid = true, Name = name, ExplicitDataPath = explicitDataPath };

    public static StorageNameValidation Fail(string error)
        => new() { IsValid = false, Error = error };
}

/// <summary>
/// Pure validation for the create-storage dialog. Mirrors the few checks
/// <see cref="ProfileService.AddProfile"/> would itself raise on, but gives them back as
/// friendly localized messages for inline display in the dialog (the brief: "не отдельным
/// MessageBox"). Does NOT touch the filesystem or the registry — duplicate-dataPath
/// detection stays in <see cref="ProfileService"/> (which holds the lock and knows the
/// on-disk truth), so a race between two create dialogs can still surface as an exception
/// from <c>AddProfile</c> that the dialog must handle separately.
/// </summary>
public static class StorageInputValidator
{
    /// <summary>
    /// Validates the name + (optional) explicit data path the user typed in the create dialog.
    /// </summary>
    /// <param name="rawName">The name field, exactly as typed.</param>
    /// <param name="rawDataPath">The advanced data-path field, or null/empty when collapsed.</param>
    public static StorageNameValidation ValidateCreate(string? rawName, string? rawDataPath)
    {
        var name = rawName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return StorageNameValidation.Fail("Укажите название хранилища.");
        }
        if (name.Length > 100)
        {
            return StorageNameValidation.Fail("Название слишком длинное (максимум 100 символов).");
        }

        string? explicitPath = null;
        var rawPath = rawDataPath?.Trim() ?? string.Empty;
        if (rawPath.Length > 0)
        {
            if (!System.IO.Path.IsPathRooted(rawPath))
            {
                return StorageNameValidation.Fail("Каталог данных должен быть абсолютным путём.");
            }
            explicitPath = rawPath;
        }

        return StorageNameValidation.Ok(name, explicitPath);
    }

    /// <summary>
    /// Validates the rename dialog input. Same name rules as create, minus the data path.
    /// </summary>
    public static StorageNameValidation ValidateRename(string? rawName)
    {
        var name = rawName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return StorageNameValidation.Fail("Укажите новое название.");
        }
        if (name.Length > 100)
        {
            return StorageNameValidation.Fail("Название слишком длинное (максимум 100 символов).");
        }
        return StorageNameValidation.Ok(name, explicitDataPath: null);
    }
}
