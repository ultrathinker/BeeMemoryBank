using Android.App;
using Android.Content;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindMobile.Platforms.Android;

/// <summary>
/// "Save to…" (plan section 10): the system file picker (Storage Access Framework) lets the user put a
/// backup file anywhere — a USB stick, Downloads, a cloud drive app — without the app holding any
/// storage permission. The file is encrypted already; its header holds only sealed material.
/// </summary>
public static class SafExport
{
    public const int RequestCode = 4711;
    private static TaskCompletionSource<global::Android.Net.Uri?>? _pending;

    /// <summary>Asks where to save <paramref name="path"/> and copies it there. False if the user cancelled.</summary>
    public static async Task<bool> SaveAsync(string path)
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No activity to show the file picker.");
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/octet-stream");
        intent.PutExtra(Intent.ExtraTitle, Path.GetFileName(path));

        _pending = new TaskCompletionSource<global::Android.Net.Uri?>();
        activity.StartActivityForResult(intent, RequestCode);
        var uri = await _pending.Task;
        if (uri == null) return false;

        var resolver = activity.ContentResolver!;
        var saved = false;
        try
        {
            // "wt": truncate. Plain "w" may leave the tail of a longer file that was there before.
            await using (var target = resolver.OpenOutputStream(uri, "wt")
                ?? throw new IOException("The chosen place cannot be written to."))
                await BlindBackupExport.CopyAsync(path, target, progress: null, CancellationToken.None);
            saved = true;
            return true;
        }
        finally
        {
            // A cut-off file in the chosen place must not pass for a backup.
            if (!saved)
            {
                try { global::Android.Provider.DocumentsContract.DeleteDocument(resolver, uri); }
                catch { /* the provider may not allow it; the error being thrown is the one that matters */ }
            }
        }
    }

    /// <summary>Called from <see cref="MainActivity"/>'s OnActivityResult.</summary>
    public static void OnResult(Result resultCode, Intent? data) =>
        _pending?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
}
