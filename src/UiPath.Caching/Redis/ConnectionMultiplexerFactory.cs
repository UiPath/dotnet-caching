using StackExchange.Redis.Profiling;

namespace UiPath.Caching.Redis;

[ExcludeFromCodeCoverage(Justification = "Wraps StackExchange.Redis.ConnectionMultiplexer.ConnectAsync — needs a real Redis endpoint to exercise.")]
public class ConnectionMultiplexerFactory(IOptions<RedisConnectionOptions> redisOptions, IRedisProfiler redisProfiler) : IConnectionMultiplexerFactory
{
    public async ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var multiplexer = redisOptions.Value.ConnectionFactory is { } factory
            ? await factory(configuration, cancellationToken).ConfigureAwait(false)
            : await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false);
        return RegisterProfilerIfEnabled(multiplexer);
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
