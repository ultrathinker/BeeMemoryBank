using System.Text.Json;

namespace BeeMemoryBank.Infrastructure.Network;

/// <summary>
/// What the node's LAN listener really serves right now, written by the Node process and read by the Api: its mDNS announcer (a record must follow the
/// listener, not the wish: a record for a port nothing of ours serves sends other computers to a dead address) and the announcer of this node's own
/// endpoints (<c>node_endpoints</c>, which names only the addresses the sync surface is served on, so a café's address is never published).
///
/// <para>Runtime state, not configuration: the file is in <c>VaultFiles</c>' never-carry list. The node writes "not listening" at start, before it starts
/// the Api child, so a file left by a crashed run is never read as true. A missing, unreadable or damaged file reads as "not listening" (nothing is
/// announced). A file of an older build carries only <c>listening</c>: it reads as the web surface on the default port, which is what that build served.</para>
/// </summary>
public sealed class LanListenerState(string dataPath)
{
    /// <summary>The file in the data folder. Keep in step with <c>VaultFiles</c> in AppPaths (a test compares them).</summary>
    public const string FileName = "network.listening.json";

    private static readonly object WriteGate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string FilePath => Path.Combine(dataPath, FileName);

    /// <summary>True only if the node last wrote that some surface listens.</summary>
    public bool IsListening() => Read().Listening;

    /// <summary>What the node last wrote; <see cref="LanListenerSnapshot.Closed"/> when nothing usable is there.</summary>
    public LanListenerSnapshot Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return LanListenerSnapshot.Closed;
            var dto = JsonSerializer.Deserialize<StateDto>(File.ReadAllText(FilePath), Json);
            if (dto is null || !dto.Listening) return LanListenerSnapshot.Closed;
            // An older build wrote {"listening": true} for its whole-front listener on 5311 and nothing else.
            if (dto.Port is null && dto.Web is null && dto.Sync is null)
                return new LanListenerSnapshot(true, LanPorts.Default, Sync: false, Web: true, Join: false, SyncAddresses: []);
            var port = dto.Port is { } p && p is > 0 and <= 65535 ? p : LanPorts.Default;
            return new LanListenerSnapshot(true, port, dto.Sync == true, dto.Web == true, dto.Join == true,
                (dto.SyncAddresses ?? []).Where(a => System.Net.IPAddress.TryParse(a, out _)).Take(16).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return LanListenerSnapshot.Closed;
        }
    }

    /// <summary>
    /// Records "the web surface listens" or "nothing listens" (the BMB_HTTPS_ENABLED front, and older callers). Best effort: false is returned when the
    /// file could not be written, so the caller can log it (a failed "true" leaves the reader at "no", the safe side; a failed "false" cannot be repaired
    /// from here).
    /// </summary>
    public bool Set(bool listening) =>
        Set(listening ? new LanListenerSnapshot(true, LanPorts.Default, Sync: false, Web: true, Join: false, SyncAddresses: []) : LanListenerSnapshot.Closed);

    /// <summary>Records what the listener serves. Best effort, as <see cref="Set(bool)"/>.</summary>
    public bool Set(LanListenerSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            lock (WriteGate)
            {
                Directory.CreateDirectory(dataPath);
                var temp = FilePath + ".tmp";
                var dto = state.Listening
                    ? new StateDto(true, state.Port, state.Sync, state.Web, state.Join, state.SyncAddresses.ToArray())
                    : new StateDto(false, null, null, null, null, null);
                File.WriteAllText(temp, JsonSerializer.Serialize(dto, Json));
                File.Move(temp, FilePath, overwrite: true);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record StateDto(bool Listening, int? Port, bool? Sync, bool? Web, bool? Join, string[]? SyncAddresses);
}

/// <summary>
/// One reading of the LAN listener (<see cref="LanListenerState"/>).
/// </summary>
/// <param name="Listening">Some surface is served on at least one address.</param>
/// <param name="Port">The port every surface is served on.</param>
/// <param name="Sync">The sync surface is served (on <paramref name="SyncAddresses"/>).</param>
/// <param name="Web">The web surface ("Devices on my network") is served.</param>
/// <param name="Join">The join door of "Connect a device" is open.</param>
/// <param name="SyncAddresses">The IPv4 addresses of the trusted networks the sync surface is served on: what this node may announce about itself.</param>
public sealed record LanListenerSnapshot(bool Listening, int Port, bool Sync, bool Web, bool Join, IReadOnlyList<string> SyncAddresses)
{
    public static readonly LanListenerSnapshot Closed = new(false, LanPorts.Default, false, false, false, []);

    /// <summary>Worth an mDNS record: peers can sync here, or the web surface is open.</summary>
    public bool Announceable => Listening && (Sync && SyncAddresses.Count > 0 || Web);
}
