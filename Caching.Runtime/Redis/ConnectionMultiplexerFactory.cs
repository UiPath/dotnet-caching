namespace UiPath.Platform.Caching.Redis;

public class ConnectionMultiplexerFactory(IOptions<RedisConnectionOptions> redisOptions) : IConnectionMultiplexerFactory
{
    public IConnectionMultiplexer Create(ConfigurationOptions configuration) =>
        redisOptions.Value.ConnectionFactory?.Invoke(configuration) ?? ConnectionMultiplexer.Connect(configuration);
}
