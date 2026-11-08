using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Pages.Admin;

namespace BeeMemoryBank.Web.Services;

/// <summary>How a device row is marked: in step (<see cref="Ok"/>), elsewhere or waiting for a call (<see cref="Quiet"/>), needing a look (<see cref="Notice"/>), or an alarm (<see cref="Alarm"/>).</summary>
public enum DeviceTone { Ok, Quiet, Notice, Alarm }

/// <summary>
/// One device of the Admin &gt; Devices list: what the row says, ready to print. <c>Status</c> is the node's own sentence about how it reaches the device
/// (<see cref="NetworkNodeDto.ReachabilityText"/>); <c>Delivery</c> says whether the two are in step; <c>Contact</c> is the last contact with whose call it was.
/// </summary>
public sealed record DeviceRow(
    Guid NodeId, string Name, string Kind, string? Platform, string Version, string Status, string? Delivery, string? Contact, DeviceTone Tone,
    string? Address, bool IsBlind)
{
    /// <summary>An alarm marker: the device is silent or does not answer.</summary>
    public bool IsAlarm => Tone == DeviceTone.Alarm;

    /// <summary>The icon of the marker (Shoelace's own set).</summary>
    public string Icon => Tone switch
    {
        DeviceTone.Ok => "circle-fill",
        DeviceTone.Quiet => "circle-half",
        DeviceTone.Notice => "exclamation-circle",
        _ => "exclamation-triangle-fill",
    };

    /// <summary>The marker in words, for a screen reader and the tooltip.</summary>
    public string ToneWords => Tone switch
    {
        DeviceTone.Ok => "In contact",
        DeviceTone.Quiet => "Not in contact now, nothing wrong",
        DeviceTone.Notice => "Needs a look",
        _ => "Alarm",
    };
}

/// <summary>
/// Turns what the network map says about a node into the row of the Devices list and the status line of a device page. One place, so the list, the page of a
/// device and the tests agree: the sentence is the node's (<c>reachabilityText</c>); a device that is elsewhere (<c>away</c>, <c>via_relay</c>) is never
/// called "not answering" and gets no alarm colour; <c>silent</c> and a real <c>not_answering</c> do.
/// </summary>
public static class DeviceRows
{
    public const string Computer = "Computer";
    public const string Server = "Server";
    public const string Phone = "Phone";
    public const string BlindNode = "Blind node";
    public const string Device = "Device";

    /// <summary>The words for what kind of device a node is: a computer, a server, a blind node or a phone.</summary>
    public static string KindOf(NetworkNodeDto node)
    {
        if (node.Kind == "blind") return BlindNode;
        if (node.Platform is "android" or "ios") return Phone;
        if (node.InContainer) return Server;
        return node.Platform switch
        {
            "windows" or "macos" => Computer,
            "linux" => Server,
            _ => node.Kind == "device" ? Device : Server,
        };
    }

    /// <summary>The system a node says it runs on, or null when it never said (a build before 2.6.4, or not heard since this node started).</summary>
    public static string? PlatformOf(NetworkNodeDto node)
    {
        if (node.InContainer) return "Docker";
        return node.Platform switch
        {
            "windows" => "Windows",
            "macos" => "Mac",
            "linux" => "Linux",
            "android" => "Android",
            "ios" => "iPhone",
            _ => null,
        };
    }

    /// <summary>The row of a peer. <paramref name="blindAlarms"/>: the alarms the Api judged for a blind node (<c>silent</c>, <c>old_protocol</c>, ...).</summary>
    public static DeviceRow For(NetworkNodeDto node, IReadOnlyCollection<string>? blindAlarms, DateTime nowUtc) =>
        new(node.NodeId, node.DisplayName, KindOf(node), PlatformOf(node), VersionOf(node), StatusText(node, nowUtc), DeliveryText(node), ContactText(node, nowUtc),
            ToneOf(node, blindAlarms), node.Address, node.Kind == "blind");

    public static string VersionOf(NetworkNodeDto node) => string.IsNullOrWhiteSpace(node.AppVersion) ? "version not reported" : node.AppVersion;

    /// <summary>The one sentence that says how the device stands, from this node's point of view.</summary>
    public static string StatusText(NetworkNodeDto node, DateTime nowUtc)
    {
        if (node.LeftAt is not null) return "Removed from the device by its owner; revoke it here to forget it";
        if (!string.IsNullOrEmpty(node.PausedText)) return node.PausedText;
        switch (node.Reachability)
        {
            case "direct_lan" or "direct_internet" or "calls_us" or "via_relay" or "away" or "old_version" or "silent":
                return node.ReachabilityText ?? Fallback(node.Reachability);
        }
        if (node.State == "not_answering")
        {
            var since = node.NotAnsweringSince is { } at ? $" since {at.ToLocalTime():g} ({AdminText.RelativeTime(at)})" : "";
            return "Not answering" + since + (string.IsNullOrEmpty(node.NotAnsweringReason) ? "" : ": " + node.NotAnsweringReason);
        }
        // The node did not give a LAN state (a blind node, an older Api): the contact state says it.
        return node.State switch
        {
            "away" => node.ReachabilityText ?? "Not on this network",
            "calls_us" => "Calls this device",
            "answering" => node.Kind == "blind" ? "Direct" : "Answering",
            _ => "Not checked since this node started; the next check will say",
        };
    }

    private static string Fallback(string reachability) => reachability switch
    {
        "direct_lan" => "Direct, on this network",
        "direct_internet" => "Direct, over the internet",
        "calls_us" => "Calls this device",
        "via_relay" => "Not on this network; its changes arrive through another device",
        "away" => "Not on this network",
        "old_version" => "Direct sync on this network needs an update",
        _ => "Not heard from",
    };

    /// <summary>"in step", or how many changes wait and for what.</summary>
    public static string? DeliveryText(NetworkNodeDto node)
    {
        if (node.IsSelf) return null;
        if (node.IsSynced) return "in step";
        var s = node.UnsyncedCount == 1 ? "" : "s";
        return node.Kind == "device" || node.State == "calls_us"
            ? $"{node.UnsyncedCount} change{s} waiting for it to call"
            : $"{node.UnsyncedCount} change{s} not delivered yet";
    }

    /// <summary>"Last contact 3 min ago (we → it)", or null when there was none.</summary>
    public static string? ContactText(NetworkNodeDto node, DateTime nowUtc)
    {
        if (node.LastContactAt is not { } at) return null;
        var direction = AdminText.ContactDirectionWords(node.LastContactDirection);
        return "Last contact " + AdminText.RelativeTime(at) + (direction.Length > 0 ? " (" + direction + ")" : "");
    }

    /// <summary>The marker of a row.</summary>
    public static DeviceTone ToneOf(NetworkNodeDto node, IReadOnlyCollection<string>? blindAlarms)
    {
        if (node.Reachability == "silent") return DeviceTone.Alarm;
        if (node.State == "not_answering" && node.Reachability is not ("away" or "via_relay" or "old_version")) return DeviceTone.Alarm;
        if (blindAlarms is not null && (blindAlarms.Contains("silent") || blindAlarms.Contains("not_answering"))) return DeviceTone.Alarm;
        if (node.Reachability == "old_version" || node.LeftAt is not null) return DeviceTone.Notice;
        if (blindAlarms is { Count: > 0 }) return DeviceTone.Notice;
        return node.Reachability switch
        {
            "direct_lan" or "direct_internet" => DeviceTone.Ok,
            "calls_us" or "via_relay" or "away" => DeviceTone.Quiet,
            _ => node.State == "answering" || node.Online ? DeviceTone.Ok : DeviceTone.Quiet,
        };
    }
}
