using System.Globalization;
using System.Net;
using System.Reflection;
using StackExchange.Redis.Profiling;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed class RedisConnector : IRedisConnector
{
    private readonly RedisConnectionOptions _redisOptions;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IRedisProfiler _redisProfiler;
    private readonly Timer? _hangDetectionTimer;
    private readonly Lazy<Version> _version;

    private Lazy<ConnectionMultiplexer> _lazyCacheConnectionMultiplexer;
    private int _reconnecting;
    private ReadWriteStatus? _lastMasterMetrics;

    public RedisConnector(
        IRedisProfiler redisProfiler,
        ICachingTelemetryProvider telemetryProvider,
        ILoggerFactory loggerFactory,
        IOptions<RedisConnectionOptions> redisOptions)
    {
        _redisOptions = redisOptions.Value;

        _lazyCacheConnectionMultiplexer = new Lazy<ConnectionMultiplexer>(CreateConnectionMultiplexer);

        _telemetryProvider = telemetryProvider;
        _loggerFactory = loggerFactory;
        _redisProfiler = redisProfiler;
        if (_redisOptions.EnableHangDetection)
        {
            var hangDetectionDueTime = _redisOptions.HangDetectionDueTime ?? TimeSpan.FromSeconds(30);
            var hangDetectionPeriod = _redisOptions.HangDetectionPeriod ?? TimeSpan.FromSeconds(5);
            _hangDetectionTimer = new Timer(_ => OnHangScan(), null, hangDetectionDueTime, hangDetectionPeriod);
        }
        _version = new Lazy<Version>(GetVersion);
    }

    public event EventHandler? OnConnectionFailed;

    public event EventHandler? OnConnectionRestored;

    public event EventHandler? OnReconnected;

    public ISubscriber Subscriber => ConnectionMultiplexer.GetSubscriber();

    public IDatabase Database => ConnectionMultiplexer.GetDatabase();

    public Version Version => _version.Value;

    private ConnectionMultiplexer ConnectionMultiplexer => _lazyCacheConnectionMultiplexer.Value;

    public bool IsConnected => ConnectionMultiplexer.IsConnected;

    public EndPoint[] GetEndPoints(bool configuredOnly = false) => ConnectionMultiplexer.GetEndPoints(configuredOnly);

    public void ForceReconnect()
    {
        if (!_lazyCacheConnectionMultiplexer.IsValueCreated)
        {
            // Multiplexer hasn't been created yet or is in the process of being created
            return;
        }

        _ = Task.Run(async () =>
        {
            if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0)
            {
                return;
            }

            try
            {
                ConnectionMultiplexer? newMultiplexer = null;
                try
                {
                    newMultiplexer = CreateConnectionMultiplexer();
                }
                catch (Exception ex)
                {
                    _telemetryProvider.TrackException(ex);

                    // Failed to connect multiplexer, return
                    return;
                }

                var oldMultiplexer = _lazyCacheConnectionMultiplexer.Value;
                _lazyCacheConnectionMultiplexer = new Lazy<ConnectionMultiplexer>(() => newMultiplexer);
                _telemetryProvider.TrackEvent("Redis.ForcedReconnect", properties: null, metrics: null);

                try
                {
                    OnReconnected?.Invoke(this, EventArgs.Empty);

                    // Waits AsyncTimeout (default 5 sec) for all commands to complete
                    await oldMultiplexer.CloseAsync(allowCommandsToComplete: true);
                }
                catch (Exception ex)
                {
                    _telemetryProvider.TrackException(ex);
                }
                finally
                {
                    // Likely to trigger lots of ObjectDisposedException
                    DisposeMultiplexer(oldMultiplexer);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnecting, 0);
            }
        });
    }

    private Version GetVersion()
    {
        Version defaultVersion = new Version(6, 0);

        Version DefaultVersion()
        {
            if (Version.TryParse(_redisOptions.DefaultVersion, out var version))
            {
                return version;
            }

            if (string.IsNullOrWhiteSpace(_redisOptions.ConnectionString))
            {
                return defaultVersion;
            }
            try
            {
                return _redisOptions.CreateConfigurationOptions().DefaultVersion;
            }
            catch (Exception)
            {
                return defaultVersion;
            }
        }

        try
        {
            var endpoint = ConnectionMultiplexer.GetEndPoints().FirstOrDefault();
            if (endpoint == null)
            {
                return defaultVersion;
            }

            var server = ConnectionMultiplexer.GetServer(endpoint);
            return server.Version;
        }
        catch (Exception ex)
        {
            _telemetryProvider.TrackException(ex);
            return DefaultVersion();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_lazyCacheConnectionMultiplexer.IsValueCreated)
            {
                _lazyCacheConnectionMultiplexer.Value.Dispose();
            }

            _hangDetectionTimer?.Dispose();
        }
    }

    private ConnectionMultiplexer CreateConnectionMultiplexer()
    {
        var multiplexer = CreateMultiplexer();
        return ConfigureMultiplexerEvents(multiplexer);
    }

    private static Dictionary<string, string> GetEventProperties(ConnectionFailedEventArgs e)
    {
        return new Dictionary<string, string>(4)
        {
            [nameof(e.EndPoint)] = e.EndPoint?.ToString() ?? string.Empty,
            [nameof(e.FailureType)] = e.FailureType.ToString(),
            ["ExceptionMessage"] = e.Exception?.Message ?? string.Empty,
            ["ExceptionType"] = e.Exception?.GetType()?.FullName ?? string.Empty,
        };
    }

#pragma warning disable IDE0079 // Remove unnecessary suppression
    [SuppressMessage("SonarQube", "S3011:Reflection should not be used to create instances of types", Justification = "By design")]
#pragma warning restore IDE0079 // Remove unnecessary suppression
    private ReadWriteStatus? GetMasterPhysicalConnectionMetrics(ConnectionMultiplexer multiplexer)
    {
        // single shard only is supported
        if (multiplexer.GetEndPoints().Select(x => multiplexer.GetServer(x)).FirstOrDefault(x => !x.IsReplica && x.IsConnected) is not IServer master)
        {
            // probably not connected yet
            return null;
        }

        try
        {
#pragma warning disable CS8602 // Dereference of a possibly null reference.
            var serverEndpoint = master.GetType().GetField("server", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(master);
            var interactivePhysicalBridge = serverEndpoint.GetType().GetField("interactive", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(serverEndpoint);
            var physicalConnection = interactivePhysicalBridge.GetType().GetField("physical", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(interactivePhysicalBridge);
            var lastWriteTickCount = physicalConnection.GetType().GetField("lastWriteTickCount", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(physicalConnection);
            var writeStatus = physicalConnection.GetType().GetField("_writeStatus", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(physicalConnection);
            var lastReadTickCount = physicalConnection.GetType().GetField("lastReadTickCount", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(physicalConnection);
            var readStatus = physicalConnection.GetType().GetField("_readStatus", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(physicalConnection);
            var awaitingResponseCount = physicalConnection.GetType().GetMethod("GetSentAwaitingResponseCount", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(physicalConnection, Array.Empty<object>());
#pragma warning restore CS8602 // Dereference of a possibly null reference.

            return new ReadWriteStatus(
                master.EndPoint,
                Convert.ToInt32(awaitingResponseCount, CultureInfo.InvariantCulture),
                Convert.ToInt32(lastWriteTickCount, CultureInfo.InvariantCulture),
                Convert.ToInt32(writeStatus, CultureInfo.InvariantCulture),
                Convert.ToInt32(lastReadTickCount, CultureInfo.InvariantCulture),
                Convert.ToInt32(readStatus, CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            _telemetryProvider.TrackException(ex);
            return null;
        }
    }

    private void OnHangScan()
    {
        if (!_lazyCacheConnectionMultiplexer.IsValueCreated)
        {
            return;
        }

        if (Volatile.Read(ref _reconnecting) > 0)
        {
            // The reconnect hasn't completed since the last scan
            return;
        }

        var lastScanMetrics = _lastMasterMetrics;
        var currentMasterMetrics = GetMasterPhysicalConnectionMetrics(_lazyCacheConnectionMultiplexer.Value);
        if (currentMasterMetrics == null)
        {
            // No master is connected yet
            return;
        }
        else
        {
            _lastMasterMetrics = currentMasterMetrics;
        }

        // First scan or master has changed (due to failover)
        if (lastScanMetrics?.EndPoint.Equals(currentMasterMetrics.EndPoint) != true)
        {
            return;
        }

        var now = Environment.TickCount;

        // WriteStatus = 3 (Flushing), ReadStatus = 5 (ReadAsync)
        if (currentMasterMetrics.AwaitingResponseCount > 100 &&
            now - currentMasterMetrics.LastWrite > _redisOptions.LastWriteIntervalThresholdMilliseconds && currentMasterMetrics.WriteStatus == 3 &&
            now - currentMasterMetrics.LastRead > _redisOptions.LastReadIntervalThresholdMilliseconds)
        {
            _telemetryProvider.TrackEvent(
                "Redis.HangDetected",
                new Dictionary<string, string>
                {
                    ["Now"] = now.ToString(CultureInfo.InvariantCulture),
                    ["LastWrite"] = currentMasterMetrics.LastWrite.ToString(CultureInfo.InvariantCulture),
                    ["LastRead"] = currentMasterMetrics.LastRead.ToString(CultureInfo.InvariantCulture),
                },
                null);

            // Reconnects on a background thread
            ForceReconnect();
        }

        _lastMasterMetrics = currentMasterMetrics;
    }

    private ConnectionMultiplexer ConfigureMultiplexerEvents(ConnectionMultiplexer multiplexer)
    {
        multiplexer.ConnectionFailed += OnInternalConnectionFailed;
        multiplexer.ConnectionRestored += OnInternalConnectionRestored;

        // Depends on Application Insights being enabled, otherwise NullTelemetryProvider is used
        if (_redisOptions.LogConnectionFailedEvents)
        {
            multiplexer.InternalError += OnInternalError;
            multiplexer.ErrorMessage += OnInternalErrorMessage;
        }

        return multiplexer;
    }

    private void DisposeMultiplexer(IConnectionMultiplexer multiplexer)
    {
        multiplexer.ConnectionFailed -= OnInternalConnectionFailed;
        multiplexer.ConnectionRestored -= OnInternalConnectionRestored;

        if (_redisOptions.LogConnectionFailedEvents)
        {
            multiplexer.InternalError -= OnInternalError;
            multiplexer.ErrorMessage -= OnInternalErrorMessage;
        }

        multiplexer.Dispose();
    }

    private void OnInternalError(object? send, InternalErrorEventArgs e)
    {
        _telemetryProvider.TrackEvent(
            "Redis.InternalError",
            new Dictionary<string, string>(4)
            {
                ["Endpoint"] = e.EndPoint?.ToString() ?? string.Empty,
                ["Origin"] = e.Origin ?? string.Empty,
                ["ExceptionMessage"] = e.Exception?.Message ?? string.Empty,
                ["ExceptionType"] = e.Exception?.GetType()?.FullName ?? string.Empty,
            },
            null);
    }

    private void OnInternalErrorMessage(object? send, RedisErrorEventArgs e)
    {
        _telemetryProvider.TrackEvent(
            "Redis.ErrorMessage",
            new Dictionary<string, string>(2)
            {
                ["Endpoint"] = e.EndPoint?.ToString() ?? string.Empty,
                ["Message"] = e.Message,
            },
            null);
    }

    private void OnInternalConnectionRestored(object? sender, ConnectionFailedEventArgs e)
    {
        OnConnectionRestored?.Invoke(sender, e);
        if (_redisOptions.LogConnectionRestoredEvents)
        {
            _telemetryProvider.TrackEvent("Redis.ConnectionRestored", GetEventProperties(e), metrics: null);
        }
    }

    private void OnInternalConnectionFailed(object? sender, ConnectionFailedEventArgs e)
    {
        OnConnectionFailed?.Invoke(sender, e);
        if (_redisOptions.LogConnectionFailedEvents)
        {
            _telemetryProvider.TrackEvent("Redis.ConnectionFailed", GetEventProperties(e), metrics: null);
        }
    }

    private ConfigurationOptions CreateRedisConfiguration()
    {
        var config = string.IsNullOrWhiteSpace(_redisOptions.ConnectionString) ? new ConfigurationOptions() : _redisOptions.CreateConfigurationOptions();
        config.LoggerFactory = _loggerFactory;
        return config;
    }

    private ConnectionMultiplexer CreateMultiplexer()
    {
        var options = CreateRedisConfiguration();
        var cnn = ConnectionMultiplexer.Connect(options);
        if (_redisOptions.ProfilerEnabled)
        {
            var factory = ProfilingSessionFactory();
            cnn.RegisterProfiler(factory);
        }

        return cnn;
    }

    public Func<ProfilingSession?> ProfilingSessionFactory() =>
        _redisOptions.ProfilingSessionFactory ?? _redisProfiler.GetSession;

    private sealed record ReadWriteStatus(EndPoint EndPoint, int AwaitingResponseCount, int LastWrite, int WriteStatus, int LastRead, int ReadStatus);
}
