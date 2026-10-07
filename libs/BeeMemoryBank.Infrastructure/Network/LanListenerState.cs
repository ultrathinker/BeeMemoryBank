using System.Text.Json;

namespace BeeMemoryBank.Infrastructure.Network;

/// <summary>
/// Whether the node's network listener (port 5311) is really up right now, written by the Node process and read by the Api's mDNS
/// announcer. The setting "Devices on my network" says what the person asked for; this says what exists. The two differ when the
/// port is taken or the listener has stopped, and an announcement must follow the listener, not the wish: a record for a port nothing
/// of ours serves sends other computers to a dead address.
///
/// <para>Runtime state, not configuration: the file is in <c>VaultFiles</c>' never-carry list. The node writes "not listening" at
/// start, before it starts the Api child, so a file left by a crashed run is never read as true. A missing, unreadable or damaged
/// file reads as "not listening" (nothing is announced).</para>
/// </summary>
public sealed class LanListenerState(string dataPath)
{
    /// <summary>The file in the data folder. Keep in step with <c>VaultFiles</c> in AppPaths (a test compares them).</summary>
    public const string FileName = "network.listening.json";

    private static readonly object WriteGate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string FilePath => Path.Combine(dataPath, FileName);

    /// <summary>True only if the node last wrote "listening".</summary>
    public bool IsListening()
    {
        try
        {
            if (!File.Exists(FilePath)) return false;
            return JsonSerializer.Deserialize<StateDto>(File.ReadAllText(FilePath), Json)?.Listening == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Records the state. Best effort: false is returned when the file could not be written, so the caller can log it (a failed
    /// "true" leaves the reader at "no", the safe side; a failed "false" cannot be repaired from here).
    /// </summary>
    public bool Set(bool listening)
    {
        try
        {
            lock (WriteGate)
            {
                Directory.CreateDirectory(dataPath);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new StateDto(listening), Json));
                File.Move(temp, FilePath, overwrite: true);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record StateDto(bool Listening);
}
