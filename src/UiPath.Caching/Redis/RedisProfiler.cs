using System.Collections.Concurrent;
using StackExchange.Redis.Profiling;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;


internal sealed partial class RedisProfiler : IRedisProfiler, IDisposable
{
    private const string NoSessionId = "(null)";
    private static readonly AsyncLocal<string?> _currentSessionId = new();

    private readonly RedisConnectionOptions _options;
    private readonly ConcurrentDictionary<string, RedisProfileEntry> _sessions = new();
    private readonly ProfilingSession? _defaultSession;
    private readonly PeriodicTimer? _timer;
    private readonly Task? _flushWorker;
    private readonly TimeProvider _clock;
    private readonly IProfiledCommandProcessor _profiledCommandProcessor;
    private readonly IProfilingSessionCommandReader _profilingSessionCommandReader;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly ILogger<RedisProfiler> _logger;

    // Under _followers: the profilers of named stacks, which open each session this one opens.
    private readonly List<RedisProfiler> _followers = [];
    private RedisProfiler? _leader;
    private bool _disposed;

    public RedisProfiler(
        IProfiledCommandProcessor profiledCommandProcessor,
        IProfilingSessionCommandReader profilingSessionCommandReader,
        ICachingTelemetryProvider telemetryProvider,
        ILogger<RedisProfiler> logger,
        IOptions<RedisConnectionOptions> optionsAccessor,
        TimeProvider clock)
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

        _clock = clock;
        _profiledCommandProcessor = profiledCommandProcessor;
        _profilingSessionCommandReader = profilingSessionCommandReader;
        _telemetryProvider = telemetryProvider;
        _logger = logger;
    }

    public int Count => _sessions.Count;

    public ProfilingSession? GetSession()
    {
        if (_disposed || !_options.ProfilerEnabled)
        {
            return null;
        }

        var sessionId = _currentSessionId.Value;
        if (sessionId == NoSessionId)
        {
            return null;
        }

        if (string.IsNullOrEmpty(sessionId))
        {
            return _defaultSession;
        }

        return _sessions.TryGetValue(sessionId, out var entry) ? entry.Session : _defaultSession;
    }

    /// <summary>
    /// sessionId null => no session
    /// sessionId empty/white space => default session
    /// sessionId not empty => custom session
    /// </summary>
    public IDisposable CreateSession(string? sessionId)
    {
        if (_disposed || !_options.ProfilerEnabled)
        {
            return Disposable.Empty;
        }

        var outerSessionId = _currentSessionId.Value;
        sessionId = sessionId?.Trim() ?? NoSessionId;
        if (string.IsNullOrEmpty(sessionId) || sessionId == NoSessionId)
        {
            _currentSessionId.Value = sessionId;
            return Disposable.Create(outerSessionId, static sId =>
            {
                _currentSessionId.Value = sId;
            });
        }
        else
        {
            RedisProfiler[] followers;
            lock (_followers)
            {
                followers = [.. _followers];
            }

            Open(sessionId);
            foreach (var follower in followers)
            {
                follower.Open(sessionId);
            }

            _currentSessionId.Value = sessionId;
            return Disposable.Create((outerSessionId, sessionId, followers), args =>
            {
                _currentSessionId.Value = args.outerSessionId;
                Close(args.sessionId);
                foreach (var follower in args.followers)
                {
                    follower.Close(args.sessionId);
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_leader is { } leader)
        {
            lock (leader._followers)
            {
                leader._followers.Remove(this);
            }
        }

        _timer?.Dispose();
        if (_flushWorker is not null)
        {
            _flushWorker.Wait(_options.ProfilerFlushInterval.Multiply(10));
            _flushWorker.Dispose();
        }
        DrainAllSessions();
        GC.SuppressFinalize(this);
    }

    /// <summary>Opens each later session of this profiler on <paramref name="follower"/> as well, so the commands of its connection keep the session's id.</summary>
    internal void Lead(RedisProfiler follower)
    {
        lock (_followers)
        {
            _followers.Add(follower);
        }

        follower._leader = this;
    }

    private void Open(string sessionId) =>
        _sessions.TryAdd(sessionId, new RedisProfileEntry(new ProfilingSession(sessionId), _clock.GetUtcNow()));

    private void Close(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var entry))
        {
            Process(_profilingSessionCommandReader.Get(entry.Session));
        }
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
                remove = _clock.GetUtcNow().Subtract(entry.Value.Created) > _options.ProfilerSessionMaxLifespan;
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
            LogDisposingProfilingSession(profileInfo.SessionId, profileInfo.Count);
            foreach (var command in profileInfo.Commands)
            {
                _profiledCommandProcessor.Process(command, profileInfo.SessionId);
            }
        }
        catch (Exception ex)
        {
            LogFailedToProcessProfiledCommands(ex, profileInfo.SessionId);
        }

    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Disposing profiling session {SessionId}. Count:{Count}")]
    private partial void LogDisposingProfilingSession(string? sessionId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to process redis profiled commands in {SessionId}")]
    private partial void LogFailedToProcessProfiledCommands(Exception ex, string? sessionId);

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
