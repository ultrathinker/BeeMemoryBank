using BeeMemoryBank.Profiles;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Resolves which profile the app should start with, per
/// docs/implementation plans/_СУПЕРПЛАН-МУЛЬТИАККАУНТ.md §4.6: <c>autostartMode == FixedProfile</c>
/// pins a specific profile; otherwise (the default, <c>LastUsed</c>) the last-used profile wins.
/// A stale/missing <c>autostartProfileId</c> (e.g. the pinned profile was forgotten) falls back to
/// <see cref="ProfileService.GetLastUsedOrDefault"/> rather than throwing — a broken autostart
/// setting must never prevent the app from starting at all.
/// </summary>
public static class AutostartProfileResolver
{
    public static ProfileEntry Resolve(ProfileService profiles)
    {
        if (profiles.AutostartMode == AutostartMode.FixedProfile
            && !string.IsNullOrEmpty(profiles.AutostartProfileId))
        {
            try
            {
                return profiles.GetById(profiles.AutostartProfileId);
            }
            catch (KeyNotFoundException)
            {
                // Pinned profile no longer exists (forgotten) - fall through to lastUsed/default.
            }
        }

        return profiles.GetLastUsedOrDefault();
    }
}
