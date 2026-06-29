namespace UiPath.Caching.Redis;

public interface IConnectionMultiplexerFactory
{
    ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default);
}
