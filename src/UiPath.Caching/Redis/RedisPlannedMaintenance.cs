using System.Globalization;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis.Maintenance;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

[ExcludeFromCodeCoverage(Justification = "Wires up StackExchange.Redis ServerMaintenanceEvent — exercised only by real Azure Cache for Redis planned-maintenance notifications.")]
public sealed class RedisPlannedMaintenance : IRedisPlannedMaintenance, IHostedService
{
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly IRedisConnector _redisConnector;
    private readonly IRedisConfigurationOptionsProvider _redisConfigurationOptionsProvider;
    private readonly IConnectionMultiplexerFactory _connectionMultiplexerFactory;
    private readonly IEnumerable<IRedisConnectionConfigurator>? _configurators;
    private readonly ILogger<RedisPlannedMaintenance> _logger;
    private readonly int _connectionRetryCount;
    private readonly TimeSpan _connectionRetryDelay;
    private readonly TimeSpan _probingTime = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _probeInterval = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _hangingTime = TimeSpan.FromSeconds(10);
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private IConnectionMultiplexer? _multiplexer;
    private volatile bool _disposed;
    private int _stopped;
    private long _maintenanceInProgress;

    public RedisPlannedMaintenance(
        ICachingTelemetryProvider telemetryProvider,
        IRedisConnector redisConnector,
        IRedisConfigurationOptionsProvider redisConfigurationOptionsProvider,
        IConnectionMultiplexerFactory connectionMultiplexerFactory,
        ILogger<RedisPlannedMaintenance> logger,
        IOptions<RedisConnectionOptions> options,
        IEnumerable<IRedisConnectionConfigurator>? configurators = null)
    {
        _telemetryProvider = telemetryProvider;
        _redisConnector = redisConnector;
        _redisConfigurationOptionsProvider = redisConfigurationOptionsProvider;
        _connectionMultiplexerFactory = connectionMultiplexerFactory;
        _configurators = configurators;
        _logger = logger;
        _connectionRetryCount = Math.Max(1, options.Value.PlannedMaintenanceConnectionRetryCount);
        var retryDelay = options.Value.PlannedMaintenanceConnectionRetryDelay;
        _connectionRetryDelay = retryDelay <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : retryDelay;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => InitializeAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Cancel();
        return Task.CompletedTask;
    }

    private void Cancel()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _cancellationTokenSource.Cancel();
        }
    }

    public bool InProgress
    {
        get => Interlocked.Read(ref _maintenanceInProgress) == 1;
        set => Interlocked.Exchange(ref _maintenanceInProgress, value ? 1 : 0);
    }

    public void Dispose()
    {
        IConnectionMultiplexer? multiplexer;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            multiplexer = _multiplexer;
            _multiplexer = null;
        }

        Cancel();
        _cancellationTokenSource.Dispose();

        if (multiplexer is not null)
        {
            TryDisposeMultiplexer(multiplexer);
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!_disposed && !cancellationToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await TryConnectAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (attempt < _connectionRetryCount)
                {
                    _logger.LogWarning(ex, "Redis planned-maintenance subscription attempt {Attempt} failed; retrying in {Delay}.", attempt, _connectionRetryDelay);
                }
                else
                {
                    _logger.LogWarning(ex, "Redis planned-maintenance subscription failed after {Attempts} attempts; giving up.", _connectionRetryCount);
                    return;
                }
            }

            try
            {
                await Task.Delay(_connectionRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task TryConnectAsync(CancellationToken cancellationToken)
    {
        var configuration = _redisConfigurationOptionsProvider.GetConfiguration();
        await RedisConnectionConfigurators.ApplyAsync(configuration, _configurators, cancellationToken).ConfigureAwait(false);
        var multiplexer = await _connectionMultiplexerFactory.CreateAsync(configuration, cancellationToken).ConfigureAwait(false);
        multiplexer.ServerMaintenanceEvent += OnServerMaintenance;

        bool disposed;
        lock (_lock)
        {
            disposed = _disposed;
            if (!disposed)
            {
                _multiplexer = multiplexer;
            }
        }

        if (disposed)
        {
            TryDisposeMultiplexer(multiplexer);
        }
    }

    private void TryDisposeMultiplexer(IConnectionMultiplexer multiplexer)
    {
        try
        {
            multiplexer.ServerMaintenanceEvent -= OnServerMaintenance;
            multiplexer.Dispose();
        }
        catch (Exception ex)
        {
            _telemetryProvider.TrackException(ex);
        }
    }

    private void OnServerMaintenance(object? sender, ServerMaintenanceEvent e)
    {
        if (e is not AzureMaintenanceEvent azureEvent)
        {
            return;
        }

        if (azureEvent.NotificationType == AzureNotificationType.NodeMaintenanceStarting)
        {
            StartConnectionProbing();
        }

        _telemetryProvider.TrackEvent(
            "Redis.Maintenance",
            [
                new("IPAddress", azureEvent.IPAddress?.ToString() ?? string.Empty),
                new("NotificationTypeString", azureEvent.NotificationTypeString),
                new("SslPort", azureEvent.SslPort.ToString(CultureInfo.InvariantCulture)),
                new("ReceivedTimeUtc", azureEvent.ReceivedTimeUtc.ToString(CultureInfo.InvariantCulture)),
                new("StartTimeUtc", azureEvent.StartTimeUtc?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                new("IsReplica", azureEvent.IsReplica.ToString(CultureInfo.InvariantCulture)),
                new("RawMessage", azureEvent.RawMessage ?? string.Empty),
            ]);
    }

    private void StartConnectionProbing()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _maintenanceInProgress, 1, 0) != 0)
        {
            return;
        }

        CancellationTokenSource tokenSource;
        try
        {
            tokenSource = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
        }
        catch (ObjectDisposedException)
        {
            InProgress = false;
            return;
        }

        tokenSource.CancelAfter(_probingTime);
        var token = tokenSource.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    _telemetryProvider.TrackEvent("Redis.MaintenanceStarted");

                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            var probeTask = _redisConnector.Database.StringSetAsync("probeRedis_" + Environment.MachineName, DateTime.UtcNow.ToString(CultureInfo.InvariantCulture), expiry: TimeSpan.FromDays(1));

                            await probeTask.WaitAsync(_hangingTime, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _telemetryProvider.TrackException(ex);
                            _redisConnector.ForceReconnect();
                        }

                        try
                        {
                            await Task.Delay(_probeInterval, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    tokenSource.Dispose();
                    InProgress = false;
                    _telemetryProvider.TrackEvent("Redis.MaintenanceEnded");
                }
            });
    }
}
