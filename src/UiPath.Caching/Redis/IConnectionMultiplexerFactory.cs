namespace UiPath.Caching.Redis;

public interface IConnectionMultiplexerFactory
{
    IConnectionMultiplexer Create(ConfigurationOptions configuration);
}
