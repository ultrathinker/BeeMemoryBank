namespace BeeMemoryBank.Api.Services;

public class DownloadCleanupHostedService : BackgroundService
{
    private readonly DownloadTokenService _tokenService;
    private readonly ILogger<DownloadCleanupHostedService> _logger;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1);

    public DownloadCleanupHostedService(DownloadTokenService tokenService, ILogger<DownloadCleanupHostedService> logger)
    {
        _tokenService = tokenService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(ScanInterval, stoppingToken);
            try
            {
                var expired = _tokenService.CleanupExpired(MaxAge);
                foreach (var entry in expired)
                {
                    try { if (File.Exists(entry.FilePath)) File.Delete(entry.FilePath); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete expired temp file {Path}", entry.FilePath); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during download token cleanup");
            }
        }
    }
}
