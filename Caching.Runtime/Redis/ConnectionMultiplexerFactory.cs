using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Redis;

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
