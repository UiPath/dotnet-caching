using System.Collections.Concurrent;
using StackExchange.Redis.Profiling;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

internal sealed class RedisProfiler : IRedisProfiler, IDisposable
{
    private readonly RedisConnectionOptions _options;
    private readonly ConcurrentDictionary<string, RedisProfileEntry> _sessions = new();
    private readonly ProfilingSession? _defaultSession;
    private readonly PeriodicTimer? _timer;
    private readonly Task? _flushWorker;
    private readonly ISystemClock _clock;
    private readonly IProfiledCommandProcessor _profiledCommandProcessor;
    private readonly IProfilingSessionCommandReader _profilingSessionCommandReader;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly ILogger<RedisProfiler> _logger;
    private readonly AsyncLocal<string?> _contextSessionId = new();
    private bool _disposed;

    public RedisProfiler(
        IProfiledCommandProcessor profiledCommandProcessor,
        IProfilingSessionCommandReader profilingSessionCommandReader,
        ICachingTelemetryProvider telemetryProvider,
        ILogger<RedisProfiler> logger,
        IOptions<RedisConnectionOptions> optionsAccessor)
    {
        _options = optionsAccessor.Value;
        if (_options.ProfilerEnabled)
        {
            if (_options.ProfilerSessionMaxLifespan is null && _options.ProfilerSessionMaxChecks is null)
            {
                throw new InvalidOperationException("ProfilerSessionMaxLifespan or ProfilerSessionMaxChecks must be set");
            }

            if (_options.ProfilerHasDefaultSession)
            {
                _defaultSession = new ProfilingSession("default");
            }
            _timer = new PeriodicTimer(_options.ProfilerFlushInterval);
            _flushWorker = FlushSessionsAsync();
        }

        _clock = _options.Clock ?? new SystemClock();
        _profiledCommandProcessor = profiledCommandProcessor;
        _profilingSessionCommandReader = profilingSessionCommandReader;
        _telemetryProvider = telemetryProvider;
        _logger = logger;
    }

    public int Count => _sessions.Count;

    public ProfilingSession? GetSession(string? sessionId = null)
    {
        if (_disposed)
        {
            return null;
        }

        sessionId ??= _contextSessionId.Value;

        if (sessionId is not null && _sessions.TryGetValue(sessionId, out var entry))
        {
            return entry.Session;
        }

        return _defaultSession;
    }

    public IDisposable CreateSession(string? sessionId = null)
    {
        if (_disposed || !_options.ProfilerEnabled)
        {
            return Disposable.Empty;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = Guid.NewGuid().ToString();
        }

        _contextSessionId.Value = sessionId;

        _sessions.TryAdd(sessionId, new RedisProfileEntry(new ProfilingSession(sessionId), _clock.UtcNow));
        return System.Reactive.Disposables.Disposable.Create(() =>
        {
            if (_sessions.TryRemove(sessionId, out var entry))
            {
                var profileInfo = _profilingSessionCommandReader.Get(entry.Session);
                Process(profileInfo);
            }
        });
    }

    private async Task FlushSessionsAsync()
    {
        while (!_disposed && await _timer!.WaitForNextTickAsync())
        {
            DrainAllSessions();
        }
    }

    private void DrainDefaultSession()
    {
        if (_defaultSession == null)
        {
            return;
        }

        var profileInfo = _profilingSessionCommandReader.Get(_defaultSession);

        if (profileInfo.Count > 0)
        {
            Process(profileInfo);
        }
    }
    private void DrainAllSessions()
    {
        if (_options.ProfilerTrackMetricEnabled)
        {
            _telemetryProvider.TrackMetric(Metrics.RedisProfilerSessions, _sessions.Count);
        }

        DrainDefaultSession();
        foreach (var entry in _sessions)
        {
            var profileInfo = _profilingSessionCommandReader.Get(entry.Value.Session);
            var remove = _disposed;

            if (!remove && _options.ProfilerSessionMaxLifespan.HasValue)
            {
                remove = _clock.UtcNow.Subtract(entry.Value.Created) > _options.ProfilerSessionMaxLifespan;
            }

            if (!remove && _options.ProfilerSessionMaxChecks.HasValue)
            {
                remove = entry.Value.Count++ >= _options.ProfilerSessionMaxChecks;
            }
            if (remove)
            {
                _sessions.TryRemove(entry.Key, out _);
            }

            Process(profileInfo);
        }
    }

    private void Process(ProfileInfo profileInfo)
    {
        try
        {
            _logger.LogDebug("Disposing profiling session {SessionId}. Count:{Count}", profileInfo.SessionId, profileInfo.Count);
            foreach (var command in profileInfo.Commands)
            {
                _profiledCommandProcessor.Process(command, profileInfo.SessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process redis profiled commands in {SessionId}", profileInfo.SessionId);
        }

    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer?.Dispose();
        if (_flushWorker is not null)
        {
            _flushWorker.Wait(_options.ProfilerFlushInterval.Multiply(10));
            _flushWorker.Dispose();
        }
        DrainAllSessions();
        GC.SuppressFinalize(this);
    }

    private sealed record RedisProfileEntry
    {
        public RedisProfileEntry(ProfilingSession session, DateTimeOffset created)
        {
            Session = session;
            Created = created;
        }
        public ProfilingSession Session { get; }

        public DateTimeOffset Created { get; }

        public int Count { get; set; }
    }
}
