using System.Globalization;
using StackExchange.Redis.Maintenance;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Redis;

public sealed class RedisPlannedMaintenance : IRedisPlannedMaintenance
{
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly IRedisConnector _redisConnector;
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly TimeSpan _probingTime = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _hangingTime = TimeSpan.FromSeconds(10);
    private long _maintenanceInProgress;

    public RedisPlannedMaintenance(
        ICachingTelemetryProvider telemetryProvider,
        IRedisConnector redisConnector,
        IRedisConfigurationOptionsProvider redisConfigurationOptionsProvider,
        IConnectionMultiplexerFactory connectionMultiplexerFactory)
    {
        _telemetryProvider = telemetryProvider;
        _redisConnector = redisConnector;

        var configuration = redisConfigurationOptionsProvider.GetConfiguration();
        _multiplexer = connectionMultiplexerFactory.Create(configuration);
        _multiplexer.ServerMaintenanceEvent += OnServerMaintenance;
    }


    public bool InProgress
    {
        get => Interlocked.Read(ref _maintenanceInProgress) == 1;
        set => Interlocked.Exchange(ref _maintenanceInProgress, value ? 1 : 0);
    }

    public void Dispose()
    {
        _multiplexer.ServerMaintenanceEvent -= OnServerMaintenance;
        _multiplexer.Dispose();
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
            new Dictionary<string, string>(7)
            {
                ["IPAddress"] = azureEvent.IPAddress?.ToString() ?? string.Empty,
                ["NotificationTypeString"] = azureEvent.NotificationTypeString,
                ["SslPort"] = azureEvent.SslPort.ToString(CultureInfo.InvariantCulture),
                ["ReceivedTimeUtc"] = azureEvent.ReceivedTimeUtc.ToString(CultureInfo.InvariantCulture),
                ["StartTimeUtc"] = azureEvent.StartTimeUtc?.ToString(CultureInfo.InvariantCulture)??string.Empty,
                ["IsReplica"] = azureEvent.IsReplica.ToString(CultureInfo.InvariantCulture),
                ["RawMessage"] = azureEvent.RawMessage??string.Empty,
            },
            null);
    }

    private void StartConnectionProbing()
    {
        if (Interlocked.CompareExchange(ref _maintenanceInProgress, 1, 0) != 0)
        {
            return;
        }

        var tokenSource = new CancellationTokenSource(_probingTime);
        var token = tokenSource.Token;

        // start connection probing on a background thread
        _ = Task.Run(
            async () =>
            {
                try
                {
                    _telemetryProvider.TrackEvent("Redis.MaintenanceStarted", null, null);

                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            // probing connection by writing a value (representing the probing time per machine)
                            var probeTask = _redisConnector.Database.StringSetAsync("probeRedis_" + Environment.MachineName, DateTime.UtcNow.ToString(CultureInfo.InvariantCulture), expiry: TimeSpan.FromDays(1));

                            // handle "Hanging" issues (see OnHangScan from RedisConnector)
                            await probeTask.WaitAsync(_hangingTime, token);
                        }
                        catch (Exception ex) when (
                            ex is RedisTimeoutException or RedisConnectionException or TimeoutException)
                        {
                            _redisConnector.ForceReconnect();

                            // give connection multiplexer a chance to connect (we don't want to be too aggressive)
                            await Task.Delay(TimeSpan.FromSeconds(1), token);
                        }
                    }
                }
                finally
                {
                    tokenSource?.Cancel();
                    tokenSource?.Dispose();
                    InProgress = false;
                    _telemetryProvider.TrackEvent("Redis.MaintenanceEnded", null, null);
                }
            }, token);
    }
}
