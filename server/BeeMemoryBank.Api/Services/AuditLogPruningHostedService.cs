using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Services;

public class AuditLogPruningHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SessionService _session;
    private readonly ILogger<AuditLogPruningHostedService> _logger;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromHours(24);
    private const int DefaultRetentionDays = 90;

    public AuditLogPruningHostedService(IServiceScopeFactory scopeFactory, SessionService session,
        ILogger<AuditLogPruningHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _session = session;
        _logger = logger;
    }

    /// <summary>
    /// Effective retention. <c>BMB_AUDIT_RETENTION_DAYS=0</c> disables pruning entirely;
    /// any positive int overrides the default. Negative / unparseable values fall back to default.
    /// </summary>
    private static int RetentionDays
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("BMB_AUDIT_RETENTION_DAYS");
            return int.TryParse(raw, out var v) && v >= 0 ? v : DefaultRetentionDays;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken);

        using var timer = new PeriodicTimer(ScanInterval);

        do
        {
            try
            {
                var retention = RetentionDays;
                if (retention == 0)
                {
                    _logger.LogInformation("Audit log pruning: disabled via BMB_AUDIT_RETENTION_DAYS=0");
                    continue;
                }

                // Skip pruning while the node is locked. Without this, a node sitting locked for
                // >91 days could have its still-relevant pre-lock audit trail silently deleted
                // by a timer fire with no operator present to react. (Claude security review MED-2.)
                if (!_session.IsUnlocked)
                {
                    _logger.LogInformation("Audit log pruning: skipped (session locked).");
                    continue;
                }

                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
                var cutoff = DateTime.UtcNow.AddDays(-retention);
                var deleted = await repo.DeleteOlderThanAsync(cutoff, stoppingToken);

                // Meta-audit: write a row INTO tbl_audit_log itself so the deletion leaves an
                // in-DB trail. Without this, an attacker whose footprint is >retention days old
                // has their audit rows silently scrubbed by the system. (Claude security review HIGH-1.)
                if (deleted > 0)
                {
                    await repo.LogAsync("system", "audit_log", "prune", "system",
                        $"deleted={deleted} cutoff={cutoff:O} retentionDays={retention}");
                    _logger.LogInformation("Audit log pruning: deleted {Count} rows older than {Date}", deleted, cutoff.ToString("yyyy-MM-dd"));
                }
                else
                {
                    _logger.LogInformation("Audit log pruning: no rows to prune");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Audit log pruning failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
