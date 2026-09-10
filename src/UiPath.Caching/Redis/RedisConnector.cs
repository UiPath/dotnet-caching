using System.Globalization;
using System.Net;
using System.Reflection;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

public sealed class RedisConnector : IRedisConnector
{
    /// <summary>Consecutive refreshes a node may report no configuration before the scan concludes it never will.</summary>
    internal const int NullTopologyRefreshLimit = 3;

    /// <summary>Most scan intervals skipped after a failing refresh: an hour at the default interval.</summary>
    internal const int MaxScanBackoff = 120;

    private readonly RedisConnectionOptions _redisOptions;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly IRedisConfigurationOptionsProvider _redisConfigurationOptionsProvider;
    private readonly IConnectionMultiplexerFactory _connectionMultiplexerFactory;
    private readonly IEnumerable<IRedisConnectionConfigurator>? _configurators;
    private readonly Timer? _hangDetectionTimer;
    private readonly ITimer? _staleEndpointTimer;
    private readonly TimeSpan _staleEndpointThreshold;
    private readonly TimeProvider _clock;
    private readonly Dictionary<EndPoint, long> _disconnectedSince = [];
    private readonly Dictionary<EndPoint, long> _memberConfirmedAt = [];
    private readonly IClusterTopologyReader _topologyReader;
    private readonly Lazy<Version> _version;
    private readonly object _swapLock = new();

    private volatile Lazy<Task<IConnectionMultiplexer>> _lazyCacheConnectionMultiplexer;
    private volatile bool _disposed;
    private volatile bool _staleScanDisabled;
    private int _reconnecting;
    private int _staleScanRunning;
    private int _nullTopologyRefreshes;
    private IConnectionMultiplexer? _nullTopologyMultiplexer;
    private int _scanFailures;
    private int _scansToSkip;
    private ReadWriteStatus? _lastMasterMetrics;

    public RedisConnector(ICachingTelemetryProvider telemetryProvider,
        IRedisConfigurationOptionsProvider redisConfigurationOptionsProvider,
        IConnectionMultiplexerFactory connectionMultiplexerFactory,
        IOptions<RedisConnectionOptions> redisOptions,
        IEnumerable<IRedisConnectionConfigurator>? configurators = null)
        : this(telemetryProvider, redisConfigurationOptionsProvider, connectionMultiplexerFactory, redisOptions, configurators, null)
    {
    }

    public RedisConnector(ICachingTelemetryProvider telemetryProvider,
        IRedisConfigurationOptionsProvider redisConfigurationOptionsProvider,
        IConnectionMultiplexerFactory connectionMultiplexerFactory,
        IOptions<RedisConnectionOptions> redisOptions,
        IEnumerable<IRedisConnectionConfigurator>? configurators,
        TimeProvider? clock)
        : this(telemetryProvider, redisConfigurationOptionsProvider, connectionMultiplexerFactory, redisOptions, configurators, clock, null)
    {
    }

    internal RedisConnector(ICachingTelemetryProvider telemetryProvider,
        IRedisConfigurationOptionsProvider redisConfigurationOptionsProvider,
        IConnectionMultiplexerFactory connectionMultiplexerFactory,
        IOptions<RedisConnectionOptions> redisOptions,
        IEnumerable<IRedisConnectionConfigurator>? configurators,
        TimeProvider? clock,
        IClusterTopologyReader? topologyReader)
    {
        _redisOptions = redisOptions.Value;

        _lazyCacheConnectionMultiplexer = CreateLazyConnection();

        _telemetryProvider = telemetryProvider;
        _redisConfigurationOptionsProvider = redisConfigurationOptionsProvider;
        _connectionMultiplexerFactory = connectionMultiplexerFactory;
        _configurators = configurators;
        _clock = clock ?? TimeProvider.System;
        _topologyReader = topologyReader ?? ClusterConfigurationReader.Instance;
        if (_redisOptions.EnableHangDetection)
        {
            var hangDetectionDueTime = _redisOptions.HangDetectionDueTime ?? TimeSpan.FromSeconds(30);
            var hangDetectionPeriod = _redisOptions.HangDetectionPeriod ?? TimeSpan.FromSeconds(5);
            _hangDetectionTimer = new Timer(_ => OnHangScan(), null, hangDetectionDueTime, hangDetectionPeriod);
        }
        _staleEndpointThreshold = _redisOptions.StaleEndpointThreshold > TimeSpan.Zero ? _redisOptions.StaleEndpointThreshold : TimeSpan.FromMinutes(5);
        if (_redisOptions.EnableStaleEndpointDetection)
        {
            var scanInterval = _redisOptions.StaleEndpointScanInterval > TimeSpan.Zero ? _redisOptions.StaleEndpointScanInterval : TimeSpan.FromSeconds(30);
            _staleEndpointTimer = _clock.CreateTimer(state => _ = ScanStaleEndpointsAsync(), null, scanInterval, scanInterval);
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

    private IConnectionMultiplexer ConnectionMultiplexer => GetConnectionTask().GetAwaiter().GetResult();

    public EndPoint[] GetEndPoints(bool configuredOnly = false)
    {
        var lazy = _lazyCacheConnectionMultiplexer;
        return lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully
            ? lazy.Value.Result.GetEndPoints(configuredOnly)
            : [];
    }

    public void ForceReconnect() => ForceReconnect(_lazyCacheConnectionMultiplexer);

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        var task = GetConnectionTask();
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    internal static string FormatEndPoint(EndPoint endPoint) => endPoint switch
    {
        IPEndPoint ip => $"{ip.Address}:{ip.Port}",
        DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
        _ => endPoint.ToString() ?? string.Empty,
    };

    internal static bool IsHangDetected(int awaitingResponseCount, int now, int lastWrite, int writeStatus, int lastRead, int lastWriteThresholdMs, int lastReadThresholdMs) =>
        awaitingResponseCount > 100
        && now - lastWrite > lastWriteThresholdMs
        && writeStatus == 3
        && now - lastRead > lastReadThresholdMs;

    /// <summary>Only topology-discovered endpoints can go stale; configured ones are left to StackExchange.Redis.</summary>
    internal async Task ScanStaleEndpointsAsync()
    {
        if (_disposed || _staleScanDisabled || Volatile.Read(ref _reconnecting) > 0)
        {
            return;
        }

        var lazy = _lazyCacheConnectionMultiplexer;
        if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _staleScanRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (_scansToSkip > 0)
            {
                _scansToSkip--;
                return;
            }

            var stale = await FindStaleEndpointsAsync(lazy.Value.Result).ConfigureAwait(false);
            _scanFailures = 0;
            if (!ReferenceEquals(_lazyCacheConnectionMultiplexer, lazy))
            {
                return;
            }

            if (stale is null)
            {
                DisableStaleEndpointScan(lazy);
                return;
            }

            if (stale.Count == 0)
            {
                return;
            }

            _telemetryProvider.TrackEvent(
                "Redis.StaleEndpointDetected",
                [
                    new("EndPoints", string.Join(";", stale.Select(FormatEndPoint))),
                    new("Threshold", _staleEndpointThreshold.ToString()),
                ]);
            ForceReconnect(lazy); // the timestamps stay until a scan of the new multiplexer prunes them, so a failed rebuild is retried next scan
        }
        catch (Exception ex)
        {
            _scansToSkip = Math.Min(1 << Math.Min(++_scanFailures, 7), MaxScanBackoff); // a handshake that never completes fails every refresh; back off instead of tracking it every interval
            _telemetryProvider.TrackException(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _staleScanRunning, 0);
        }
    }

    internal async Task<ClusterMembership> RefreshClusterMembershipAsync(IConnectionMultiplexer multiplexer)
    {
        // A non-initial reconfigure re-handshakes the configured endpoints only, so only their configuration can become current.
        var configured = multiplexer.GetEndPoints(configuredOnly: true);
        var candidates = multiplexer.GetServers()
            .Where(server => server.IsConnected && Array.IndexOf(configured, server.EndPoint) >= 0)
            .Select(server => (Server: server, Before: _topologyReader.GetConfiguration(server)))
            .ToList();

        // The client's own handshake reads CLUSTER NODES as an internal call, so AllowAdmin does not apply; false means another reconfiguration held the guard.
        if (candidates.Count == 0 || !await multiplexer.ConfigureAsync().ConfigureAwait(false))
        {
            return ClusterMembership.Inconclusive;
        }

        var unchanged = false;
        var neverConfigured = false;
        foreach (var (server, before) in candidates)
        {
            var after = _topologyReader.GetConfiguration(server);
            if (after is null)
            {
                neverConfigured |= before is null && server.IsConnected;
            }
            else if (ReferenceEquals(after, before))
            {
                unchanged = true; // a landed re-read installs a new instance, so the same one means this node's refresh failed
            }
            else if (server.IsConnected)
            {
                _nullTopologyRefreshes = 0;
                return new(Conclusive: true, _topologyReader.GetMembers(after));
            }
        }

        if (unchanged || !neverConfigured)
        {
            _nullTopologyRefreshes = 0;
            return ClusterMembership.Inconclusive;
        }

        // A node with no configuration may have lost only the topology reply; a persistent absence means it cannot answer it.
        // The streak belongs to the multiplexer it was observed on, so a rebuilt one starts over.
        if (!ReferenceEquals(_nullTopologyMultiplexer, multiplexer))
        {
            _nullTopologyMultiplexer = multiplexer;
            _nullTopologyRefreshes = 0;
        }

        return ++_nullTopologyRefreshes >= NullTopologyRefreshLimit ? new(Conclusive: true, Members: null) : ClusterMembership.Inconclusive;
    }

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

    [ExcludeFromCodeCoverage(Justification = "Only called from the excluded OnInternalConnection* event handlers.")]
    private static KeyValuePair<string, string>[] GetEventProperties(ConnectionFailedEventArgs e) =>
    [
        new(nameof(e.EndPoint), e.EndPoint?.ToString() ?? string.Empty),
        new(nameof(e.FailureType), e.FailureType.ToString()),
        new("ExceptionMessage", e.Exception?.Message ?? string.Empty),
        new("ExceptionType", e.Exception?.GetType()?.FullName ?? string.Empty),
    ];

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

    private void ForceReconnect(Lazy<Task<IConnectionMultiplexer>> current)
    {
        if (_disposed || !ReferenceEquals(_lazyCacheConnectionMultiplexer, current))
        {
            return;
        }

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
            _staleEndpointTimer?.Dispose();
        }
    }

    /// <returns>Null when no connected node carries a cluster configuration, so membership can never be judged.</returns>
    private async Task<List<EndPoint>?> FindStaleEndpointsAsync(IConnectionMultiplexer multiplexer)
    {
        var now = _clock.GetTimestamp();
        var configured = multiplexer.GetEndPoints(configuredOnly: true);
        IServer? connected = null;
        var disconnected = new List<IServer>();
        foreach (var server in multiplexer.GetServers())
        {
            if (server.IsConnected)
            {
                connected ??= server;
            }
            else if (Array.IndexOf(configured, server.EndPoint) < 0)
            {
                disconnected.Add(server);
            }
        }

        foreach (var endpoint in _disconnectedSince.Keys.Where(e => !disconnected.Exists(s => s.EndPoint.Equals(e))).ToArray())
        {
            _disconnectedSince.Remove(endpoint);
            _memberConfirmedAt.Remove(endpoint);
        }

        var overdue = new List<EndPoint>();
        foreach (var endpoint in disconnected.Select(s => s.EndPoint))
        {
            if (!_disconnectedSince.TryGetValue(endpoint, out var since))
            {
                _disconnectedSince[endpoint] = now;
            }
            else if (_clock.GetElapsedTime(since, now) >= _staleEndpointThreshold && !IsRecentlyConfirmedMember(endpoint, now))
            {
                overdue.Add(endpoint);
            }
        }

        if (overdue.Count == 0 || connected is null)
        {
            return [];
        }

        var membership = await RefreshClusterMembershipAsync(multiplexer).ConfigureAwait(false);
        if (!membership.Conclusive)
        {
            return [];
        }

        if (membership.Members is null)
        {
            return null;
        }

        var confirmed = overdue.Where(membership.Members.Contains).ToList();
        RecordConfirmedMembers(confirmed, now);
        return overdue.Except(confirmed).ToList();
    }

    /// <summary>A member the cluster still lists is asked about again only once per threshold, and reported the first time.</summary>
    private void RecordConfirmedMembers(List<EndPoint> confirmed, long now)
    {
        var firstTime = confirmed.Where(endpoint => !_memberConfirmedAt.ContainsKey(endpoint)).ToList();
        foreach (var endpoint in confirmed)
        {
            _memberConfirmedAt[endpoint] = now;
        }

        if (firstTime.Count > 0)
        {
            _telemetryProvider.TrackEvent(
                "Redis.StaleEndpointStillAMember",
                [
                    new("EndPoints", string.Join(";", firstTime.Select(FormatEndPoint))),
                    new("Threshold", _staleEndpointThreshold.ToString()),
                ]);
        }
    }

    private bool IsRecentlyConfirmedMember(EndPoint endpoint, long now) =>
        _memberConfirmedAt.TryGetValue(endpoint, out var confirmedAt) && _clock.GetElapsedTime(confirmedAt, now) < _staleEndpointThreshold;

    private void DisableStaleEndpointScan(Lazy<Task<IConnectionMultiplexer>> judged)
    {
        lock (_swapLock)
        {
            if (_disposed || !ReferenceEquals(_lazyCacheConnectionMultiplexer, judged))
            {
                return;
            }

            _staleScanDisabled = true;
        }

        _staleEndpointTimer?.Dispose();
        _telemetryProvider.TrackEvent("Redis.StaleEndpointScanDisabled", [new("Reason", "NoClusterConfiguration")]);
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
