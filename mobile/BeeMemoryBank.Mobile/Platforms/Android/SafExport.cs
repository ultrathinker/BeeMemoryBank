using Android.App;
using Android.Content;

namespace BeeMemoryBank.Mobile.Platforms.Android;

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

        await using var source = File.OpenRead(path);
        await using var target = activity.ContentResolver!.OpenOutputStream(uri, "w")
            ?? throw new IOException("The chosen place cannot be written to.");
        await source.CopyToAsync(target);
        return true;
    }

    /// <summary>Called from <see cref="MainActivity"/>'s OnActivityResult.</summary>
    public static void OnResult(Result resultCode, Intent? data) =>
        _pending?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
}
