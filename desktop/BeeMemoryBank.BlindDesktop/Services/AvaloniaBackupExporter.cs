using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Services;

/// <summary>
/// "Save to...": the system save-file dialog of Avalonia. The stream it returns goes to <see cref="BlindBackupExport"/> (inside
/// AppCore), which checks that the file is a finished backup and that the chosen place kept all of it.
/// </summary>
public sealed class AvaloniaBackupExporter(Func<TopLevel?> window) : IBlindBackupExporter
{
    public const string BackupExtension = "bmbbackup";

    public async Task<Stream> CreateAsync(string suggestedName, CancellationToken ct)
    {
        var file = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var top = window() ?? throw new InvalidOperationException("There is no window to show the save dialog in.");
            return await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the backup to...",
                SuggestedFileName = suggestedName,
                DefaultExtension = BackupExtension,
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("BeeMemoryBank backup") { Patterns = ["*." + BackupExtension] }],
            });
        });

        if (file is null) throw new OperationCanceledException("No place was chosen; nothing was saved.");

        var stream = await file.OpenWriteAsync();
        // The picker may hand back a stream on an existing file: the backup must replace it, not overwrite its first bytes.
        if (stream.CanSeek && stream.Length > 0) stream.SetLength(0);
        return stream;
    }
}
