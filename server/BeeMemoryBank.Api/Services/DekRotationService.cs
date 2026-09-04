using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

public partial class DekRotationService : IDekRotationApplier
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SessionService _sessionService;
    private readonly DbConnectionFactory _connFactory;
    private readonly MaintenanceModeService _maintenance;
    private readonly ILogger<DekRotationService> _logger;
    private readonly string _dataPath;

    private readonly ProgressState _progress = new();
    private readonly SemaphoreSlim _executeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed class ProgressState
    {
        private volatile DekRotationFlowStep _step = DekRotationFlowStep.Idle;
        private volatile int _pct;
        private volatile string? _msg;
        private volatile string? _err;
        private volatile string? _eventId;

        public DekRotationFlowStep Step => _step;
        public int Pct => _pct;
        public string? Msg => _msg;
        public string? Err => _err;
        public string? EventId => _eventId;

        public void Update(DekRotationFlowStep step, int? pct = null, string? msg = null,
            string? err = null, string? eventId = null)
        {
            _step = step;
            if (pct.HasValue) _pct = pct.Value;
            if (msg != null) _msg = msg;
            if (err != null) _err = err;
            if (eventId != null) _eventId = eventId;
        }

        public void ClearError() => _err = null;
        public void ClearEventId() => _eventId = null;
    }

    public DekRotationService(
        IServiceScopeFactory scopeFactory,
        SessionService sessionService,
        DbConnectionFactory connFactory,
        MaintenanceModeService maintenance,
        ILogger<DekRotationService> logger,
        string dataPath)
    {
        _scopeFactory = scopeFactory;
        _sessionService = sessionService;
        _connFactory = connFactory;
        _maintenance = maintenance;
        _logger = logger;
        _dataPath = dataPath;
    }

    public DekRotationProgressResponse GetProgress()
    {
        return new DekRotationProgressResponse(
            _progress.EventId != null ? Guid.Parse(_progress.EventId) : null,
            _progress.Step,
            _progress.Pct,
            _progress.Msg,
            _progress.Err);
    }

    public async Task CancelAsync(string eventId)
    {
        if (!await _executeLock.WaitAsync(TimeSpan.Zero))
            throw new ConflictException("Cannot cancel \u2014 rotation flow is actively executing.");
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var stateRepo = scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>();
            await stateRepo.UpdateStateAsync(eventId, DekRotationState.Cancelled);

            if (_progress.EventId == eventId)
            {
                _progress.Update(DekRotationFlowStep.Idle, 0, "Cancelled");
                _progress.ClearEventId();
            }
        }
        finally
        {
            _executeLock.Release();
        }
    }
}
