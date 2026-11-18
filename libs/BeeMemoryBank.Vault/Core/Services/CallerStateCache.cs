using System.Collections.Concurrent;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// What this node itself says a forwarded <c>X-User-Id</c> is: the role and the active flag of the row in <c>tbl_user</c>, read by the API
/// and never taken from the caller. The Web layer vouches for WHO is asking (its cookie); what that person may do is decided here, so a
/// cookie that was issued to a superadmin stops being one the moment the row says otherwise.
///
/// <para>The answer is kept for a few seconds, so a page that makes twenty calls reads the table once. Every change of a user that can alter
/// the answer — role, active flag, password, security stamp, deletion — ends in <see cref="Invalidate"/> (<c>UserService</c>), and the
/// generation counter makes an invalidation win over a read that was already under way: a value loaded before the change is never stored
/// after it. The cache is process-wide and keyed by database, like the folder-rule cache (user ids restart at 1 in every database).</para>
/// </summary>
public static class CallerStateCache
{
    /// <summary>How long an answer is reused when nothing invalidated it. Short on purpose: the invalidation is the mechanism, this is the net.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private readonly record struct Entry(string? Role, long ExpiresAtTicks);

    private static readonly ConcurrentDictionary<string, Entry> Cache = new();
    private static long _generation;

    private static string Key(string databaseId, int userId) => $"{databaseId}|u:{userId}";

    /// <summary>
    /// The role of an ACTIVE user, or null when the row is missing or inactive (a deleted account: not authenticated, not a superadmin) or being
    /// removed (<see cref="User.IsBeingRemoved"/>).
    /// <paramref name="users"/> is only read on a miss.
    /// </summary>
    public static async ValueTask<string?> GetRoleAsync(string databaseId, int userId, IUserRepository users)
    {
        var key = Key(databaseId, userId);
        var now = Environment.TickCount64;
        if (Cache.TryGetValue(key, out var hit) && hit.ExpiresAtTicks > now)
            return hit.Role;

        var generation = Volatile.Read(ref _generation);
        var user = await users.GetByIdAsync(userId);   // active rows only
        // An account a removal process holds (release 2.12.0) has no rights at all from the moment that process locked it out, whatever its role still says.
        var role = user is { IsActive: true, IsBeingRemoved: false } ? user.Role : null;

        // An invalidation that ran while we were reading means the value may be the one it was meant to throw away.
        if (Volatile.Read(ref _generation) == generation)
            Cache[key] = new Entry(role, now + (long)Ttl.TotalMilliseconds);
        return role;
    }

    /// <summary>Forgets what is known about one user, in every database of this process. Called, synchronously, before a change of that user returns.</summary>
    public static void Invalidate(int userId)
    {
        Interlocked.Increment(ref _generation);
        var suffix = $"|u:{userId}";
        foreach (var key in Cache.Keys)
            if (key.EndsWith(suffix, StringComparison.Ordinal))
                Cache.TryRemove(key, out _);
    }

    /// <summary>Forgets everything (a restore replaced the users table, a test reset the world).</summary>
    public static void InvalidateAll()
    {
        Interlocked.Increment(ref _generation);
        Cache.Clear();
    }

    /// <summary>True when the role is the superadmin role. Ordinal, like every other role comparison (role names are lower-case by construction).</summary>
    public static bool IsSuperadmin(string? role) => role == UserRoles.Superadmin;
}
