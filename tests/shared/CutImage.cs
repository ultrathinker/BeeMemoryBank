using System.Text.Json.Nodes;
using BeeMemoryBank.AppPaths;

namespace BeeMemoryBank.TestSupport;

/// <summary>
/// What a power cut leaves in a data folder is the very files the operation was writing, and its marker names them (the volume and the file index of each file it
/// found, <see cref="SealIntent"/>). A test cannot cut a process at an instant, so it takes a COPY of the folder and starts a node over the copy; the copy's files are
/// files of their own, which the marker of the operation never saw, and the start would rightly refuse them as a node somebody put there. <see cref="Adopt"/> makes the
/// copy what the cut would have left: the marker of the copy names the files of the copy.
/// </summary>
public static class CutImage
{
    /// <summary>Points the pending marker of <paramref name="imageDir"/> (a copy of a data folder) at the files of the copy. Nothing when no marker is pending.</summary>
    public static void Adopt(string imageDir)
    {
        var path = SealIntent.PathFor(imageDir);
        if (!File.Exists(path)) return;
        JsonObject marker;
        try { marker = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return; }   // a damaged marker is what it is
        if (marker["pre"] is not JsonArray pre) return;
        foreach (var entry in pre.OfType<JsonObject>())
        {
            var file = Path.Combine(imageDir, entry["name"]!.GetValue<string>());
            if (!File.Exists(file) && !Directory.Exists(file)) continue;
            if (FileIdentity.TryGet(file) is not { } identity) continue;
            entry["volume"] = identity.Volume;
            entry["index"] = identity.Index;
        }
        File.WriteAllText(path, marker.ToJsonString());
    }
}
