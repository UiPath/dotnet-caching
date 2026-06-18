using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using StackExchange.Redis;
using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Sample.AspNetCore;

public class OpenTelemetryConnectionMultiplexerFactory(IOptions<RedisConnectionOptions> redisOptions, IServiceProvider serviceProvider) : IConnectionMultiplexerFactory
{
    public IConnectionMultiplexer Create(ConfigurationOptions configuration)
    {
        var cnn = redisOptions.Value.ConnectionFactory?.Invoke(configuration) ?? ConnectionMultiplexer.Connect(configuration);
        var instrumentation = serviceProvider.GetService<StackExchangeRedisInstrumentation>();
        instrumentation?.AddConnection(cnn);
        return cnn;
    }
}
