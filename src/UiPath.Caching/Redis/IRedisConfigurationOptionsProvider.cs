namespace UiPath.Caching.Redis;

public interface IRedisConfigurationOptionsProvider
{
    ConfigurationOptions GetConfiguration();
}
