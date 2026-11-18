using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Services;

public class SyncStatusService : INotifyPropertyChanged
{
    /// <summary>Problem codes the screens turn into their own words. "unreachable" is not an error: the badge stays yellow.</summary>
    public const string ProblemUnreachable = "unreachable";
    public const string ProblemWrongKey = "wrongkey";
    public const string ProblemRefused = "refused";
    public const string ProblemBusy = "busy";
    public const string ProblemSnapshot = "snapshot";
    public const string ProblemOther = "other";
    /// <summary>A computer proved that an administrator disconnected this phone from the bank (2.12): one calm sentence, not an error.</summary>
    public const string ProblemDisconnected = "disconnected";

    /// <summary>
    /// Whether a failed contact is "the computer is not reachable for now" (yellow) or a real failure (red). A timeout, a refused
    /// connection, no route, no answer or a DNS failure only mean the peer is away; a refusal (401/403), a wrong pinned key or a
    /// broken copy are real failures. The same classification as the iPhone's FullSync.Classify; libs is not changed.
    /// </summary>
    public static string Classify(Exception ex, bool pinned) => ex switch
    {
        ThisDeviceRevokedException => ProblemDisconnected,
        TaskCanceledException or OperationCanceledException => ProblemUnreachable,
        SocketException => ProblemUnreachable,
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => pinned ? ProblemWrongKey : ProblemUnreachable,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => ProblemRefused,
        HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable } => ProblemBusy,
        HttpRequestException { StatusCode: null } => ProblemUnreachable,
        SnapshotRequiredException => ProblemSnapshot,
        _ => ProblemOther,
    };

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
    /// <summary>True once a refresh has actually read the peer channels; before that the registration flag is only a default, not a finding.</summary>
    public bool IsStatusKnown { get => _isStatusKnown; private set => Set(ref _isStatusKnown, value); }
    private bool _isStatusKnown;
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    /// <summary>The first REAL failure's code of the last manual round (null when nothing failed for real); the screens say it in their own words.</summary>
    public string? LastProblemCode { get => _lastProblemCode; private set => Set(ref _lastProblemCode, value); }
    private string? _lastProblemCode;

    /// <summary>How many peers the last manual round simply could not reach (not an error).</summary>
    public int LastUnreachable { get => _lastUnreachable; private set => Set(ref _lastUnreachable, value); }
    private int _lastUnreachable;

    /// <summary>The first unreachable peer's name, for the yellow "not reachable for now" line.</summary>
    public string? LastUnreachablePeer { get => _lastUnreachablePeer; private set => Set(ref _lastUnreachablePeer, value); }
    private string? _lastUnreachablePeer;

    /// <summary>Every REAL failure of the last manual round, as codes the screens say in their own words.</summary>
    public IReadOnlyList<PeerProblem> LastProblems { get => _lastProblems; private set => Set(ref _lastProblems, value); }
    private IReadOnlyList<PeerProblem> _lastProblems = [];
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
            // "Registered" is: the phone knows some way to reach a peer - its address, what it announced, what NSD saw, an address typed here.
            isRegistered = await PhonePeers.AnyChannelAsync(sp, peers.Where(p => p.NodeId != identity?.NodeId));

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (nodeName != null) { NodeName = nodeName; NodeId = nodeId!; NodeIdShort = nodeIdShort!; PublicKeyB64 = publicKeyB64!; }
                ArticleCount = articleCount;
                if (hasLastSync) LastSyncTime = lastSyncTime;
                IsRegisteredOnServer = isRegistered;
                IsStatusKnown = true;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SyncStatusService.RefreshAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// One sync with every peer that has a way to be reached, now. Returns what each peer that could not be served said (empty when all were): a wrong
    /// address answers "no sync server at this address", never a clean pass (BMB-307). Callers that have a place to say it (the Status page) show it.
    /// </summary>
    public async Task<IReadOnlyList<PeerSyncFailure>> SyncNowAsync()
    {
        var failures = new List<PeerSyncFailure>();
        if (IsInvisible)
        {
            MainThread.BeginInvokeOnMainThread(() => StatusText = "Invisible");
            return failures;
        }

        var newCts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _manualSyncCts, newCts, null) != null)
        {
            newCts.Dispose();
            return failures;
        }
        var token = newCts.Token;

        try
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusText = "Syncing...";
                LastProblemCode = null;
                LastUnreachable = 0;
                LastUnreachablePeer = null;
                LastProblems = [];
            });
            using var scope = _serviceProvider.CreateScope();
            var sp = scope.ServiceProvider;
            var whitelist = sp.GetRequiredService<IWhitelistRepository>();

            var nodes = await whitelist.GetAllActiveAsync();
            // A look around the Wi-Fi first (a few seconds at most; it starts and ends here): a desktop that moved to another address is found by NSD.
            if (nodes.Count > 0 && _serviceProvider.GetService<ILanPeerScanner>() is { } scanner)
                await scanner.ScanAsync(PhonePeers.LookFor, token);
            // Every peer that has a way to be reached now, not only those whose row has an address.
            var remoteNodes = await PhonePeers.CallableAsync(sp, nodes);

            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            var unreachable = 0;
            var disconnected = false;
            var problems = new List<PeerProblem>();
            string? firstProblem = null, firstUnreachable = null;
            foreach (var node in remoteNodes)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    await PeerSessions.SyncWithPeerAsync(sp, http, node, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Sync error with {node.NodeId}: {ex.GetType().Name}");   // no address, no message: either may carry what must not be logged
                    var code = Classify(ex, node.EffectivePin is not null);
                    if (code == ProblemDisconnected)
                    {
                        // An administrator disconnected this phone (2.12): the status page says it calmly; not a failure dialog.
                        disconnected = true;
                    }
                    else if (code == ProblemUnreachable)
                    {
                        // A computer that is simply away is not an error: yellow, never red.
                        unreachable++;
                        firstUnreachable ??= node.DisplayName;
                    }
                    else
                    {
                        firstProblem ??= code;
                        problems.Add(new PeerProblem(node.NodeId, node.DisplayName, code));
                        failures.Add(PeerSyncFailure.From(node, ex));
                    }
                }
            }
            // The state is a token for the badge; the reasons are said by the screens in the app's own words.
            var said = disconnected ? "Disconnected" : failures.Count > 0 ? "Error" : unreachable > 0 ? "Unreachable" : "Idle";
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusText = said;
                LastProblemCode = disconnected ? ProblemDisconnected : firstProblem;
                LastUnreachable = unreachable;
                LastUnreachablePeer = firstUnreachable;
                LastProblems = problems;
            });
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
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusText = "Error";
                LastProblemCode = ProblemOther;
            });
        }
        finally
        {
            Interlocked.Exchange(ref _manualSyncCts, null)?.Dispose();
        }
        return failures;
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

/// <summary>One real sync failure, as a code the screens turn into their own words (never the raw English reason).</summary>
public sealed record PeerProblem(Guid NodeId, string Name, string Code);
