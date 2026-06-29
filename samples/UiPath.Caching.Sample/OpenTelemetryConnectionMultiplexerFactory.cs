using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Sample;

public class OpenTelemetryConnectionMultiplexerFactory(IOptions<RedisConnectionOptions> redisOptions, IServiceProvider serviceProvider) : IConnectionMultiplexerFactory
{
    public async ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cnn = redisOptions.Value.ConnectionFactory?.Invoke(configuration) ?? await ConnectionMultiplexer.ConnectAsync(configuration);
        var instrumentation = serviceProvider.GetService<StackExchangeRedisInstrumentation>();
        instrumentation?.AddConnection(cnn);
        return cnn;
    }
}
