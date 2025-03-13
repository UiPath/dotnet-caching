namespace UiPath.Platform.Caching.Redis;

public interface IConnectionMultiplexerFactory
{
    IConnectionMultiplexer Create(ConfigurationOptions configuration);
}
