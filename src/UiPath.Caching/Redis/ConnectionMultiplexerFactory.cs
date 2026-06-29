using StackExchange.Redis.Profiling;

namespace UiPath.Caching.Redis;

[ExcludeFromCodeCoverage(Justification = "Wraps StackExchange.Redis.ConnectionMultiplexer.ConnectAsync — needs a real Redis endpoint to exercise.")]
public class ConnectionMultiplexerFactory(IOptions<RedisConnectionOptions> redisOptions, IRedisProfiler redisProfiler) : IConnectionMultiplexerFactory
{
    public async ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RegisterProfilerIfEnabled(redisOptions.Value.ConnectionFactory?.Invoke(configuration)
            ?? await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false));
    }

    private IConnectionMultiplexer RegisterProfilerIfEnabled(IConnectionMultiplexer connectionMultiplexer)
    {
        if (redisOptions.Value.ProfilerEnabled)
        {
            connectionMultiplexer.RegisterProfiler(ProfilingSessionFactory());
        }

        return connectionMultiplexer;
    }

    private Func<ProfilingSession?> ProfilingSessionFactory() =>
        redisOptions.Value.ProfilingSessionFactory ?? redisProfiler.GetSession;
}
