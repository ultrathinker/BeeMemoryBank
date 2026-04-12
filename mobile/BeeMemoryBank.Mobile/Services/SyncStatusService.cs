using System.ComponentModel;
using System.Runtime.CompilerServices;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Services;

public class SyncStatusService : INotifyPropertyChanged
{
    private readonly IServiceProvider _serviceProvider;
    private CancellationTokenSource? _pollCts;
    private CancellationTokenSource? _manualSyncCts;

    private string _nodeName = "";
    private string _nodeId = "";
    private string _nodeIdShort = "";
    private string _publicKeyB64 = "";
    private bool _isSyncActive;
    private DateTime _lastSyncTime;
    private int _articleCount;
    private bool _isRegisteredOnServer;
    private string _statusText = "Idle";
    private bool _syncServiceRunning;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SyncStatusService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public string NodeName { get => _nodeName; set => Set(ref _nodeName, value); }
    public string NodeId { get => _nodeId; set => Set(ref _nodeId, value); }
    public string NodeIdShort { get => _nodeIdShort; set => Set(ref _nodeIdShort, value); }
    public string PublicKeyB64 { get => _publicKeyB64; set => Set(ref _publicKeyB64, value); }
    public bool IsSyncActive { get => _isSyncActive; set => Set(ref _isSyncActive, value); }
    public DateTime LastSyncTime { get => _lastSyncTime; set => Set(ref _lastSyncTime, value); }
    public int ArticleCount { get => _articleCount; set => Set(ref _articleCount, value); }
    public bool IsRegisteredOnServer { get => _isRegisteredOnServer; set => Set(ref _isRegisteredOnServer, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public bool SyncServiceRunning
    {
        get => _syncServiceRunning;
        set
        {
            if (Set(ref _syncServiceRunning, value))
                IsSyncActive = value;
        }
    }
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

            var nodeRepo = sp.GetRequiredService<INodeIdentityRepository>();
            var identity = await nodeRepo.GetAsync();
            if (identity != null)
            {
                NodeName = identity.DisplayName;
                NodeId = identity.NodeId.ToString();
                NodeIdShort = identity.NodeId.ToString()[..8];
                PublicKeyB64 = Convert.ToBase64String(identity.Ed25519PublicKey);
            }

            var articleRepo = sp.GetRequiredService<IArticleRepository>();
            var articles = await articleRepo.ListAsync();
            ArticleCount = articles.Count;

            var syncPosRepo = sp.GetRequiredService<ISyncPositionRepository>();
            var positions = await syncPosRepo.GetAllAsync();
            if (positions.Count > 0)
                LastSyncTime = positions.Max(p => p.UpdatedAt);

            // Node is "in the network" if it knows any remote node with ApiAddress
            var whitelistRepo = sp.GetRequiredService<IWhitelistRepository>();
            var peers = await whitelistRepo.GetAllActiveAsync();
            IsRegisteredOnServer = peers.Any(p => p.NodeId != identity?.NodeId && !string.IsNullOrEmpty(p.ApiAddress));
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
            StatusText = "Invisible";
            return;
        }

        if (_manualSyncCts != null) return;
        _manualSyncCts = new CancellationTokenSource();
        var token = _manualSyncCts.Token;

        try
        {
            StatusText = "Syncing...";
            using var scope = _serviceProvider.CreateScope();
            var sp = scope.ServiceProvider;
            var whitelist = sp.GetRequiredService<IWhitelistRepository>();
            var syncClient = sp.GetRequiredService<SyncClient>();

            var nodes = await whitelist.GetAllActiveAsync();
            var remoteNodes = nodes.Where(n => !string.IsNullOrEmpty(n.ApiAddress)).ToList();

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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
            StatusText = "Idle";
            if (!token.IsCancellationRequested)
                await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SyncNowAsync error: {ex.Message}");
            StatusText = "Error";
        }
        finally
        {
            _manualSyncCts?.Dispose();
            _manualSyncCts = null;
        }
    }

    public void CancelSync()
    {
        if (_manualSyncCts != null && !_manualSyncCts.IsCancellationRequested)
        {
            _manualSyncCts.Cancel();
            StatusText = "Cancelled";
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
