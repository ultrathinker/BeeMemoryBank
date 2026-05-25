using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Background poller that pulls snapshots from every configured remote
/// subscription and feeds them to <see cref="RemoteEventApplier"/>.
/// Runs forever once the host starts; tolerates locked sessions, network
/// errors, and 401/403/404 from owner nodes by recording status on the
/// account and continuing with the next subscription.
/// </summary>
public class RemoteAccountSyncScheduler(
    IServiceProvider services,
    ILogger<RemoteAccountSyncScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so app startup isn't fighting the first poll.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "RemoteAccountSyncScheduler cycle failed");
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var session = sp.GetRequiredService<SessionService>();
        if (!session.IsUnlocked)
            return; // can't decrypt local replicas without master DEK; try next cycle

        var accountRepo = sp.GetRequiredService<IRemoteAccountRepository>();
        var subRepo = sp.GetRequiredService<IRemoteSubscriptionRepository>();
        var applier = sp.GetRequiredService<RemoteEventApplier>();
        var accountSvc = sp.GetRequiredService<RemoteAccountService>();
        var http = sp.GetRequiredService<HttpClient>();

        var accounts = (await accountRepo.ListAllAsync()).ToDictionary(a => a.Id);
        var subs = await subRepo.ListAllAsync();

        foreach (var sub in subs)
        {
            if (ct.IsCancellationRequested) return;
            if (!accounts.TryGetValue(sub.RemoteAccountId, out var account))
                continue;

            try
            {
                var token = accountSvc.DecryptToken(account);
                var url = $"{account.BaseUrl}/api/folders/by-path/snapshot?path={Uri.EscapeDataString(sub.RemoteFolderPath)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Authorization", $"Bearer {token}");
                using var resp = await http.SendAsync(req, ct);

                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    await accountRepo.UpdateStatusAsync(account.Id, "auth_failed",
                        "Token rejected by owner (401). Re-enter credentials.", null);
                    continue;
                }
                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                    || resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Could be true revocation OR a transient 403/404 (nginx
                    // maintenance, load balancer, owner reboot). Don't hard-delete
                    // the subscription on the first failure — record the status
                    // and let the user explicitly detach via the Remote Accounts
                    // page. The applier's safety-net also blocks mass cleanup in
                    // the empty-snapshot case (caught by Claude+kilo round-3).
                    await accountRepo.UpdateStatusAsync(account.Id, "access_lost",
                        $"Owner returned HTTP {(int)resp.StatusCode} for {sub.RemoteFolderPath}. " +
                        "If permanent, detach the subscription manually.", DateTime.UtcNow);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    await accountRepo.UpdateStatusAsync(account.Id, "error",
                        $"HTTP {(int)resp.StatusCode} from {account.BaseUrl}", null);
                    continue;
                }

                var snap = await resp.Content.ReadFromJsonAsync<RemoteSnapshot>(JsonOpts, ct);
                if (snap == null) continue;

                await applier.ApplySnapshotAsync(sub, snap);
                await subRepo.UpdateCursorAsync(sub.Id, snap.Cursor.ToString(), DateTime.UtcNow);
                await accountRepo.UpdateStatusAsync(account.Id, "ok", null, DateTime.UtcNow);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Remote sync HTTP error for account {Account}", account.Id);
                await accountRepo.UpdateStatusAsync(account.Id, "unreachable", ex.Message, null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Remote sync failed for subscription {Sub}", sub.Id);
                await accountRepo.UpdateStatusAsync(account.Id, "error", ex.Message, null);
            }
        }
    }
}
