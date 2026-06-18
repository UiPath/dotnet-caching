using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace UiPath.Caching.Redis;
[ExcludeFromCodeCoverage(Justification = "Pings a live IConnectionMultiplexer and reads its Status/IsConnected/OperationCount — depends on a real Redis endpoint.")]
public class RedisHealthCheck : IHealthCheck
{
    private readonly IRedisConnector _redisConnector;
    private readonly IRedisPlannedMaintenance? _redisPlannedMaintenance;

    public RedisHealthCheck(IRedisConnector redisConnector, IRedisPlannedMaintenance? redisPlannedMaintenance = null)
    {
        _redisConnector = redisConnector;
        _redisPlannedMaintenance = redisPlannedMaintenance;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_redisPlannedMaintenance?.InProgress ?? false)
            {
                return new HealthCheckResult(HealthStatus.Healthy, "Azure Cache for Redis maintenance in progress");
            }

            var latency = await _redisConnector.Database.PingAsync();

            return HealthCheckResult.Healthy(data: new Dictionary<string, object>
                {
                    { "Latency",  latency.TotalMilliseconds },
                    { "ConnectionMultiplexer.IsConnected",  _redisConnector.Database.Multiplexer?.IsConnected ?? false },
                    { "ConnectionMultiplexer.IsConnecting",  _redisConnector.Database.Multiplexer ?.IsConnecting ?? false },
                    { "ConnectionMultiplexer.OperationCount",  _redisConnector.Database.Multiplexer?.OperationCount ?? -1 },
                    { "ConnectionMultiplexer.Status",  _redisConnector.Database.Multiplexer?.GetStatus() ?? "N/A" },
                });
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
