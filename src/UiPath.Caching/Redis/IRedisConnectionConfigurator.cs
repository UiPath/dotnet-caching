namespace UiPath.Caching.Redis;

/// <summary>Configures the Redis <see cref="ConfigurationOptions"/> before connecting (authentication extension point).</summary>
public interface IRedisConnectionConfigurator
{
    ValueTask ConfigureAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default);
}
