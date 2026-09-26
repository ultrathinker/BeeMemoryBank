using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace BeeMemoryBank.Desktop.Views;

/// <summary>Opens the system folder picker and returns a local path, or null if cancelled.</summary>
internal static class FolderPicker
{
    public static async Task<string?> PickAsync(Window owner, string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
