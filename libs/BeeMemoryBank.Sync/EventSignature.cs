using System.Text;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Deterministic serialization of event fields for Ed25519 signing/verification.
/// Uses length-prefixed encoding to avoid collisions.
/// </summary>
public static class EventSignature
{
    public static byte[] BuildPayload(SyncEvent evt)
    {
        using var ms = new MemoryStream();
        WriteString(ms, evt.EventId.ToString("D"));
        WriteString(ms, evt.NodeId.ToString("D"));
        WriteLong(ms, evt.LamportTs);
        WriteString(ms, evt.EventType);
        WriteString(ms, evt.ArticleId?.ToString("D") ?? "");
        WriteString(ms, evt.Payload);
        WriteInt(ms, evt.ProtocolVersion);
        // Always serialize as UTC (Kind may be Unspecified after round-trip through SQLite)
        WriteString(ms, new DateTime(evt.CreatedAt.Ticks, DateTimeKind.Utc).ToString("o"));
        return ms.ToArray();
    }

    private static void WriteString(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteInt(ms, bytes.Length);
        ms.Write(bytes);
    }

    private static void WriteInt(MemoryStream ms, int v)
        => ms.Write(BitConverter.GetBytes(v));

    private static void WriteLong(MemoryStream ms, long v)
        => ms.Write(BitConverter.GetBytes(v));
}
