using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The API reads the caller's role from its own <c>tbl_user</c> (<c>CallerIdentityMiddleware</c>): a request that names a person in <c>X-User-Id</c> is a person who has to
/// exist. A test that sends an id of its own (the chat's stream rate limit is one process-wide limiter keyed by user id, so a class picks an id nobody else uses) makes the
/// row first, with the role the test means to act in.
/// </summary>
internal static class ForwardedUsers
{
    public static async Task EnsureAsync(IServiceProvider services, int userId, string role = UserRoles.Superadmin)
    {
        using var scope = services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_user (id, username, display_name, password_hash, role, is_active, created_at, security_stamp, chat_access)
              VALUES (@userId, @name, @name, 'not-a-hash', @role, 1, @now, 'stamp', 1)",
            new { userId, name = "forwarded-user-" + userId, role, now = DateTime.UtcNow.ToString("o") });
        BeeMemoryBank.Core.Services.CallerStateCache.Invalidate(userId);
    }
}
