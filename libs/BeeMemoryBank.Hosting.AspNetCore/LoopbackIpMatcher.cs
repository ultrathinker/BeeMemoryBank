using System.Net;

namespace BeeMemoryBank.Hosting.AspNetCore;

/// <summary>
/// Utility to check whether a given IP address is a trusted loopback address.
/// </summary>
public static class LoopbackIpMatcher
{
    private static readonly List<IPAddress> DirectLoopbackIps = new()
    {
        IPAddress.Loopback,     // 127.0.0.1
        IPAddress.IPv6Loopback, // ::1
        IPAddress.Parse("::ffff:127.0.0.1") // IPv4-mapped IPv6 loopback
    };

    /// <summary>
    /// Returns true if the provided IP address is a loopback address (127.0.0.0/8, ::1, or IPv4-mapped loopback).
    /// </summary>
    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
            return false;

        // 1. Direct check using standard BCL method
        if (IPAddress.IsLoopback(address))
            return true;

        // 2. Check predefined common loopback addresses
        if (DirectLoopbackIps.Contains(address))
            return true;

        // 3. Check if mapped to IPv6 loopback
        if (address.IsIPv4MappedToIPv6)
        {
            var mapped = address.MapToIPv4();
            if (IPAddress.IsLoopback(mapped))
                return true;
        }

        // 4. Check subnet 127.0.0.0/8 (standard IPv4 loopback block)
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4 && bytes[0] == 127)
        {
            return true;
        }

        // Check IPv4-mapped IPv6 loopback network (::ffff:127.0.0.0/104)
        if (bytes.Length == 16 && address.IsIPv4MappedToIPv6)
        {
            var mapped = address.MapToIPv4();
            var mappedBytes = mapped.GetAddressBytes();
            if (mappedBytes.Length == 4 && mappedBytes[0] == 127)
            {
                return true;
            }
        }

        return false;
    }
}
