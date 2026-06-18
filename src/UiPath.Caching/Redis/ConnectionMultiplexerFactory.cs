using StackExchange.Redis.Profiling;

namespace UiPath.Caching.Redis;

[ExcludeFromCodeCoverage(Justification = "Wraps StackExchange.Redis.ConnectionMultiplexer.Connect — needs a real Redis endpoint to exercise.")]
public class ConnectionMultiplexerFactory(IOptions<RedisConnectionOptions> redisOptions, IRedisProfiler redisProfiler) : IConnectionMultiplexerFactory
{
    public IConnectionMultiplexer Create(ConfigurationOptions configuration)
    {
        IConnectionMultiplexer connectionMultiplexer = redisOptions.Value.ConnectionFactory?.Invoke(configuration) ?? ConnectionMultiplexer.Connect(configuration);

        if (redisOptions.Value.ProfilerEnabled)
        {
            var factory = ProfilingSessionFactory();
            connectionMultiplexer.RegisterProfiler(factory);
        }

        return connectionMultiplexer;
    }

    private Func<ProfilingSession?> ProfilingSessionFactory() =>
        redisOptions.Value.ProfilingSessionFactory ?? redisProfiler.GetSession;

}
