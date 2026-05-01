using System.ComponentModel;
using System.Runtime.CompilerServices;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Services;

public class SyncStatusService : INotifyPropertyChanged
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private CancellationTokenSource? _pollCts;
    private volatile CancellationTokenSource? _manualSyncCts;

    private string _nodeName = "";
    private string _nodeId = "";
    private string _nodeIdShort = "";
    private string _publicKeyB64 = "";
    private DateTime _lastSyncTime;
    private int _articleCount;
    private bool _isRegisteredOnServer;
    private string _statusText = "Idle";

    public event PropertyChangedEventHandler? PropertyChanged;

    public SyncStatusService(IServiceProvider serviceProvider, IHttpClientFactory httpClientFactory)
    {
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
    }

    public string NodeName { get => _nodeName; set => Set(ref _nodeName, value); }
    public string NodeId { get => _nodeId; set => Set(ref _nodeId, value); }
    public string NodeIdShort { get => _nodeIdShort; set => Set(ref _nodeIdShort, value); }
    public string PublicKeyB64 { get => _publicKeyB64; set => Set(ref _publicKeyB64, value); }
    public DateTime LastSyncTime { get => _lastSyncTime; set => Set(ref _lastSyncTime, value); }
    public int ArticleCount { get => _articleCount; set => Set(ref _articleCount, value); }
    public bool IsRegisteredOnServer { get => _isRegisteredOnServer; set => Set(ref _isRegisteredOnServer, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public bool IsInvisible
    {
        get => _serviceProvider.GetRequiredService<BeeMemoryBank.Core.Services.InvisibleModeService>().IsInvisible;
        set
        {
            var svc = _serviceProvider.GetRequiredService<BeeMemoryBank.Core.Services.InvisibleModeService>();
            if (svc.IsInvisible != value)
            {
                svc.IsInvisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInvisible)));
            }
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var sp = scope.ServiceProvider;

            string? nodeName = null, nodeId = null, nodeIdShort = null, publicKeyB64 = null;
            int articleCount = 0;
            DateTime lastSyncTime = default;
            bool hasLastSync = false, isRegistered = false;

            var nodeRepo = sp.GetRequiredService<INodeIdentityRepository>();
            var identity = await nodeRepo.GetAsync();
            if (identity != null)
            {
                nodeName = identity.DisplayName;
                nodeId = identity.NodeId.ToString();
                nodeIdShort = identity.NodeId.ToString()[..8];
                publicKeyB64 = Convert.ToBase64String(identity.Ed25519PublicKey);
            }

            var articleRepo = sp.GetRequiredService<IArticleRepository>();
            var articles = await articleRepo.ListAsync();
            articleCount = articles.Count;

            var syncPosRepo = sp.GetRequiredService<ISyncPositionRepository>();
            var positions = await syncPosRepo.GetAllAsync();
            if (positions.Count > 0)
            {
                lastSyncTime = positions.Max(p => p.UpdatedAt);
                hasLastSync = true;
            }

            var whitelistRepo = sp.GetRequiredService<IWhitelistRepository>();
            var peers = await whitelistRepo.GetAllActiveAsync();
            isRegistered = peers.Any(p => p.NodeId != identity?.NodeId && !string.IsNullOrEmpty(p.ApiAddress));

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (nodeName != null) { NodeName = nodeName; NodeId = nodeId!; NodeIdShort = nodeIdShort!; PublicKeyB64 = publicKeyB64!; }
                ArticleCount = articleCount;
                if (hasLastSync) LastSyncTime = lastSyncTime;
                IsRegisteredOnServer = isRegistered;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SyncStatusService.RefreshAsync error: {ex.Message}");
        }
    }

    public async Task SyncNowAsync()
    {
        if (IsInvisible)
        {
            MainThread.BeginInvokeOnMainThread(() => StatusText = "Invisible");
            return;
        }

        var newCts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _manualSyncCts, newCts, null) != null)
        {
            newCts.Dispose();
            return;
        }
        var token = newCts.Token;

        try
        {
            MainThread.BeginInvokeOnMainThread(() => StatusText = "Syncing...");
            using var scope = _serviceProvider.CreateScope();
            var sp = scope.ServiceProvider;
            var whitelist = sp.GetRequiredService<IWhitelistRepository>();
            var syncClient = sp.GetRequiredService<SyncClient>();

            var nodes = await whitelist.GetAllActiveAsync();
            var remoteNodes = nodes.Where(n => !string.IsNullOrEmpty(n.ApiAddress)).ToList();

            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            foreach (var node in remoteNodes)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    await syncClient.SyncWithAsync(http, node.ApiAddress!, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Sync error with {node.ApiAddress}: {ex.Message}");
                }
            }
            MainThread.BeginInvokeOnMainThread(() => StatusText = "Idle");
            if (!token.IsCancellationRequested)
                await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            MainThread.BeginInvokeOnMainThread(() => StatusText = "Cancelled");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SyncNowAsync error: {ex.Message}");
            MainThread.BeginInvokeOnMainThread(() => StatusText = "Error");
        }
        finally
        {
            Interlocked.Exchange(ref _manualSyncCts, null)?.Dispose();
        }
    }

    public void CancelSync()
    {
        var cts = _manualSyncCts;
        if (cts != null && !cts.IsCancellationRequested)
        {
            cts.Cancel();
            MainThread.BeginInvokeOnMainThread(() => StatusText = "Cancelled");
        }
    }

    public void StartPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await RefreshAsync();
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
        }, token);
    }

    public void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
