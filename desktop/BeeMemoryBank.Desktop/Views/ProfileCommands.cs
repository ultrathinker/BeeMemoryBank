using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace BeeMemoryBank.Desktop.Views;

/// <summary>
/// "New profile" and "Add existing profile" flows, shared by the tray menu and the Profiles
/// window so both entry points behave the same: show the dialog, then switch to the result.
/// </summary>
internal static class ProfileCommands
{
    public static async Task NewProfileAsync(Window dialogOwner, MainWindow mainWindow)
    {
        var dialog = new CreateStorageDialog(mainWindow.Profiles);
        var ok = await dialog.ShowDialog<bool?>(dialogOwner);
        if (ok != true || dialog.CreatedProfile == null) return;

        mainWindow.NotifyProfilesChanged();
        // The empty data dir of a new profile meets the existing /Setup wizard after the switch.
        await SwitchAsync(mainWindow, dialog.CreatedProfile.Id);
    }

    public static async Task AddExistingAsync(Window dialogOwner, MainWindow mainWindow)
    {
        var dialog = new AddExistingProfileDialog(mainWindow.Profiles);
        var ok = await dialog.ShowDialog<bool?>(dialogOwner);
        if (ok != true || dialog.AddedProfile == null) return;

        mainWindow.NotifyProfilesChanged();
        await SwitchAsync(mainWindow, dialog.AddedProfile.Id);
    }

    private static async Task SwitchAsync(MainWindow mainWindow, string profileId)
    {
        mainWindow.ShowAndFocusWindow();
        try
        {
            await mainWindow.SwitchProfileAsync(profileId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error switching to profile '{profileId}': {ex.Message}");
        }
    }
}
