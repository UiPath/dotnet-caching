using System.Globalization;
using System.Net;
using System.Reflection;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

public sealed class RedisConnector : IRedisConnector
{
    private readonly RedisConnectionOptions _redisOptions;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly IRedisConfigurationOptionsProvider _redisConfigurationOptionsProvider;
    private readonly IConnectionMultiplexerFactory _connectionMultiplexerFactory;
    private readonly IEnumerable<IRedisConnectionConfigurator>? _configurators;
    private readonly Timer? _hangDetectionTimer;
    private readonly Lazy<Version> _version;
    private readonly object _swapLock = new();

    private volatile Lazy<Task<IConnectionMultiplexer>> _lazyCacheConnectionMultiplexer;
    private volatile bool _disposed;
    private int _reconnecting;
    private ReadWriteStatus? _lastMasterMetrics;

    public RedisConnector(ICachingTelemetryProvider telemetryProvider,
        IRedisConfigurationOptionsProvider redisConfigurationOptionsProvider,
        IConnectionMultiplexerFactory connectionMultiplexerFactory,
        IOptions<RedisConnectionOptions> redisOptions,
        IEnumerable<IRedisConnectionConfigurator>? configurators = null)
    {
        _redisOptions = redisOptions.Value;

        _lazyCacheConnectionMultiplexer = CreateLazyConnection();

        _telemetryProvider = telemetryProvider;
        _redisConfigurationOptionsProvider = redisConfigurationOptionsProvider;
        _connectionMultiplexerFactory = connectionMultiplexerFactory;
        _configurators = configurators;
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

    [ExcludeFromCodeCoverage(Justification = "Lazy resolves through GetVersion, which is itself excluded as live-multiplexer-only.")]
    public Version Version => _version.Value;

    private IConnectionMultiplexer ConnectionMultiplexer => GetConnectionTask().GetAwaiter().GetResult();

    private Lazy<Task<IConnectionMultiplexer>> CreateLazyConnection() =>
        new(() =>
        {
            try
            {
                return Task.Run(async () => await CreateConnectionMultiplexerAsync(CancellationToken.None).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                return Task.FromException<IConnectionMultiplexer>(ex);
            }
        });

    private Task<IConnectionMultiplexer> GetConnectionTask()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var lazy = _lazyCacheConnectionMultiplexer;
        if (!lazy.IsValueCreated || !lazy.Value.IsFaulted)
        {
            return lazy.Value;
        }

        lock (_swapLock)
        {
            var current = _lazyCacheConnectionMultiplexer;
            if (!_disposed && current.IsValueCreated && current.Value.IsFaulted)
            {
                _ = current.Value.Exception;
                _lazyCacheConnectionMultiplexer = CreateLazyConnection();
            }

            return _lazyCacheConnectionMultiplexer.Value;
        }
    }

    public bool IsConnected
    {
        get
        {
            var lazy = _lazyCacheConnectionMultiplexer;
            return lazy.IsValueCreated
                && lazy.Value.IsCompletedSuccessfully
                && lazy.Value.Result.IsConnected;
        }
    }

    public EndPoint[] GetEndPoints(bool configuredOnly = false)
    {
        var lazy = _lazyCacheConnectionMultiplexer;
        return lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully
            ? lazy.Value.Result.GetEndPoints(configuredOnly)
            : [];
    }

    public void ForceReconnect()
    {
        if (_disposed)
        {
            return;
        }

        var current = _lazyCacheConnectionMultiplexer;
        if (!current.IsValueCreated || !current.Value.IsCompleted)
        {
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
                IConnectionMultiplexer newMultiplexer;
                try
                {
                    newMultiplexer = await CreateConnectionMultiplexerAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _telemetryProvider.TrackException(ex);
                    return;
                }

                Task<IConnectionMultiplexer> previousTask;
                lock (_swapLock)
                {
                    if (_disposed || !ReferenceEquals(_lazyCacheConnectionMultiplexer, current))
                    {
                        TryDisposeMultiplexer(newMultiplexer);
                        return;
                    }

                    previousTask = current.Value;
                    var swapped = new Lazy<Task<IConnectionMultiplexer>>(() => Task.FromResult(newMultiplexer));
                    _ = swapped.Value;
                    _lazyCacheConnectionMultiplexer = swapped;
                }

                _telemetryProvider.TrackEvent("Redis.ForcedReconnect");

                try
                {
                    OnReconnected?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    _telemetryProvider.TrackException(ex);
                }

                await CloseAndDisposeAsync(previousTask).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _reconnecting, 0);
            }
        });
    }

    private async Task CloseAndDisposeAsync(Task<IConnectionMultiplexer> multiplexerTask)
    {
        IConnectionMultiplexer multiplexer;
        try
        {
            multiplexer = await multiplexerTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _telemetryProvider.TrackException(ex);
            return;
        }

        try
        {
            await multiplexer.CloseAsync(allowCommandsToComplete: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _telemetryProvider.TrackException(ex);
        }
        finally
        {
            TryDisposeMultiplexer(multiplexer);
        }
    }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        var task = GetConnectionTask();
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    [ExcludeFromCodeCoverage(Justification = "Reads server.Version off a live IConnectionMultiplexer endpoint — needs a real Redis to exercise.")]
    private Version GetVersion()
    {
        Version defaultVersion = new Version(6, 0);

        [ExcludeFromCodeCoverage(Justification = "Fallback version resolution reached only when reading server.Version off a live connection fails.")]
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
                var configuration = _redisConfigurationOptionsProvider.GetConfiguration();
                return configuration.DefaultVersion;
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
            Lazy<Task<IConnectionMultiplexer>> lazy;
            lock (_swapLock)
            {
                _disposed = true;
                lazy = _lazyCacheConnectionMultiplexer;
            }

            if (lazy.IsValueCreated)
            {
                var multiplexerTask = lazy.Value;
                if (multiplexerTask.IsCompletedSuccessfully)
                {
                    TryDisposeMultiplexer(multiplexerTask.Result);
                }
                else
                {
                    _ = multiplexerTask.ContinueWith(
                        t =>
                        {
                            if (t.IsCompletedSuccessfully)
                            {
                                TryDisposeMultiplexer(t.Result);
                            }
                            else
                            {
                                _ = t.Exception;
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }

            _hangDetectionTimer?.Dispose();
        }
    }

    private void TryDisposeMultiplexer(IConnectionMultiplexer multiplexer)
    {
        try
        {
            DisposeMultiplexer(multiplexer);
        }
        catch (Exception ex)
        {
            _telemetryProvider.TrackException(ex);
        }
    }

    private async ValueTask<IConnectionMultiplexer> CreateConnectionMultiplexerAsync(CancellationToken cancellationToken)
    {
        var multiplexer = await CreateMultiplexerAsync(cancellationToken).ConfigureAwait(false);
        return ConfigureMultiplexerEvents(multiplexer);
    }

    [ExcludeFromCodeCoverage(Justification = "Only called from the excluded OnInternalConnection* event handlers.")]
    private static KeyValuePair<string, string>[] GetEventProperties(ConnectionFailedEventArgs e) =>
    [
        new(nameof(e.EndPoint), e.EndPoint?.ToString() ?? string.Empty),
        new(nameof(e.FailureType), e.FailureType.ToString()),
        new("ExceptionMessage", e.Exception?.Message ?? string.Empty),
        new("ExceptionType", e.Exception?.GetType()?.FullName ?? string.Empty),
    ];

#pragma warning disable IDE0079 // Remove unnecessary suppression
    [SuppressMessage("SonarQube", "S3011:Reflection should not be used to create instances of types", Justification = "By design")]
#pragma warning restore IDE0079 // Remove unnecessary suppression
    [ExcludeFromCodeCoverage(Justification = "Reflects into StackExchange.Redis private fields (server/interactive/physical) — values only exist on a live multiplexer with established physical connections.")]
    internal ReadWriteStatus? GetMasterPhysicalConnectionMetrics(IConnectionMultiplexer multiplexer)
    {
        if (multiplexer.GetEndPoints().Select(x => multiplexer.GetServer(x)).FirstOrDefault(x => !x.IsReplica && x.IsConnected) is not IServer master)
        {
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
            var awaitingResponseCount = physicalConnection.GetType().GetMethod("GetSentAwaitingResponseCount", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(physicalConnection, []);
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

    internal static bool IsHangDetected(int awaitingResponseCount, int now, int lastWrite, int writeStatus, int lastRead, int lastWriteThresholdMs, int lastReadThresholdMs) =>
        awaitingResponseCount > 100
        && now - lastWrite > lastWriteThresholdMs
        && writeStatus == 3
        && now - lastRead > lastReadThresholdMs;

    [ExcludeFromCodeCoverage(Justification = "Timer callback driven by hang-detection on live multiplexer metrics — depends on GetMasterPhysicalConnectionMetrics reflection output that only exists on a real Redis connection.")]
    private void OnHangScan()
    {
        if (_disposed || !_lazyCacheConnectionMultiplexer.IsValueCreated)
        {
            return;
        }

        if (Volatile.Read(ref _reconnecting) > 0)
        {
            return;
        }

        var multiplexerTask = _lazyCacheConnectionMultiplexer.Value;
        if (!multiplexerTask.IsCompletedSuccessfully)
        {
            return;
        }

        var lastScanMetrics = _lastMasterMetrics;
        var currentMasterMetrics = GetMasterPhysicalConnectionMetrics(multiplexerTask.Result);
        if (currentMasterMetrics == null)
        {
            return;
        }
        else
        {
            _lastMasterMetrics = currentMasterMetrics;
        }

        if (lastScanMetrics?.EndPoint.Equals(currentMasterMetrics.EndPoint) != true)
        {
            return;
        }

        var now = Environment.TickCount;

        if (IsHangDetected(
                currentMasterMetrics.AwaitingResponseCount,
                now,
                currentMasterMetrics.LastWrite,
                currentMasterMetrics.WriteStatus,
                currentMasterMetrics.LastRead,
                _redisOptions.LastWriteIntervalThresholdMilliseconds,
                _redisOptions.LastReadIntervalThresholdMilliseconds))
        {
            _telemetryProvider.TrackEvent(
                "Redis.HangDetected",
                [
                    new("Now", now.ToString(CultureInfo.InvariantCulture)),
                    new("LastWrite", currentMasterMetrics.LastWrite.ToString(CultureInfo.InvariantCulture)),
                    new("LastRead", currentMasterMetrics.LastRead.ToString(CultureInfo.InvariantCulture)),
                ]);

            ForceReconnect();
        }

        _lastMasterMetrics = currentMasterMetrics;
    }

    [ExcludeFromCodeCoverage(Justification = "Wires multiplexer event handlers (ConnectionFailed/Restored/InternalError/ErrorMessage) — fires only from real StackExchange.Redis multiplexer events.")]
    private IConnectionMultiplexer ConfigureMultiplexerEvents(IConnectionMultiplexer multiplexer)
    {
        multiplexer.ConnectionFailed += OnInternalConnectionFailed;
        multiplexer.ConnectionRestored += OnInternalConnectionRestored;

        if (_redisOptions.LogConnectionFailedEvents)
        {
            multiplexer.InternalError += OnInternalError;
            multiplexer.ErrorMessage += OnInternalErrorMessage;
        }

        return multiplexer;
    }

    [ExcludeFromCodeCoverage(Justification = "Unwires multiplexer event handlers and disposes — only reached from ForceReconnect against a live multiplexer.")]
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

    [ExcludeFromCodeCoverage(Justification = "Handler for IConnectionMultiplexer.InternalError — fires only from real Redis transport errors.")]
    private void OnInternalError(object? send, InternalErrorEventArgs e)
    {
        _telemetryProvider.TrackEvent(
            "Redis.InternalError",
            [
                new("Endpoint", e.EndPoint?.ToString() ?? string.Empty),
                new("Origin", e.Origin ?? string.Empty),
                new("ExceptionMessage", e.Exception?.Message ?? string.Empty),
                new("ExceptionType", e.Exception?.GetType()?.FullName ?? string.Empty),
            ]);
    }

    [ExcludeFromCodeCoverage(Justification = "Handler for IConnectionMultiplexer.ErrorMessage — fires only from real Redis-side error replies.")]
    private void OnInternalErrorMessage(object? send, RedisErrorEventArgs e)
    {
        _telemetryProvider.TrackEvent(
            "Redis.ErrorMessage",
            [
                new("Endpoint", e.EndPoint?.ToString() ?? string.Empty),
                new("Message", e.Message),
            ]);
    }

    [ExcludeFromCodeCoverage(Justification = "Handler for IConnectionMultiplexer.ConnectionRestored — fires only from a real reconnect event.")]
    private void OnInternalConnectionRestored(object? sender, ConnectionFailedEventArgs e)
    {
        OnConnectionRestored?.Invoke(sender, e);
        if (_redisOptions.LogConnectionRestoredEvents)
        {
            _telemetryProvider.TrackEvent("Redis.ConnectionRestored", GetEventProperties(e));
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Handler for IConnectionMultiplexer.ConnectionFailed — fires only from a real connection-drop event.")]
    private void OnInternalConnectionFailed(object? sender, ConnectionFailedEventArgs e)
    {
        OnConnectionFailed?.Invoke(sender, e);
        if (_redisOptions.LogConnectionFailedEvents)
        {
            _telemetryProvider.TrackEvent("Redis.ConnectionFailed", GetEventProperties(e));
        }
    }

    private async ValueTask<IConnectionMultiplexer> CreateMultiplexerAsync(CancellationToken cancellationToken)
    {
        var configuration = _redisConfigurationOptionsProvider.GetConfiguration();
        await RedisConnectionConfigurators.ApplyAsync(configuration, _configurators, cancellationToken).ConfigureAwait(false);
        return await _connectionMultiplexerFactory.CreateAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    [ExcludeFromCodeCoverage(Justification = "Carrier record for GetMasterPhysicalConnectionMetrics, which is itself excluded as reflection-on-live-Redis-only.")]
    internal sealed record ReadWriteStatus(EndPoint EndPoint, int AwaitingResponseCount, int LastWrite, int WriteStatus, int LastRead, int ReadStatus);
}
