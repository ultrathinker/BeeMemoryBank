using Android.Content;

namespace BeeMemoryBank.Mobile.Platforms.Android;

/// <summary>
/// "Save to a file" the Android way: the Storage Access Framework's create-document picker, so the
/// person chooses where the file goes and the app writes only the one file they picked. No storage
/// permission is needed (the picker grants the one URI). MainActivity forwards the activity result here.
/// </summary>
public static class TextFileSaver
{
    public const int RequestCode = 0x5AFE;

    private static TaskCompletionSource<bool>? _pending;
    private static string? _content;

    /// <summary>Opens the picker for a new text file; true when the file was written, false when cancelled.</summary>
    public static Task<bool> SaveTextAsync(string fileName, string content)
    {
        var activity = Platform.CurrentActivity;
        if (activity is null) return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>();
        _content = content;
        _pending = tcs;
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("text/plain");
        intent.PutExtra(Intent.ExtraTitle, fileName);
        try
        {
            activity.StartActivityForResult(intent, RequestCode);
        }
        catch (Exception)
        {
            _content = null;
            _pending = null;
            tcs.TrySetResult(false);
        }
        return tcs.Task;
    }

    /// <summary>Called by MainActivity.OnActivityResult for our request code.</summary>
    public static void HandleResult(int requestCode, global::Android.App.Result resultCode, Intent? data)
    {
        if (requestCode != RequestCode) return;
        var tcs = _pending;
        var content = _content;
        _pending = null;
        _content = null;
        if (tcs is null) return;

        if (resultCode != global::Android.App.Result.Ok || data?.Data is not { } uri || content is null)
        {
            tcs.TrySetResult(false);
            return;
        }
        try
        {
            var resolver = Platform.AppContext.ContentResolver;
            using var stream = resolver?.OpenOutputStream(uri);
            if (stream is null) { tcs.TrySetResult(false); return; }
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            tcs.TrySetResult(true);
        }
        catch (Exception)
        {
            tcs.TrySetResult(false);
        }
    }
}
