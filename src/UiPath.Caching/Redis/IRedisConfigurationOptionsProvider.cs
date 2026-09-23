namespace UiPath.Caching.Redis;

public interface IRedisConfigurationOptionsProvider
{
    ConfigurationOptions GetConfiguration();

    /// <summary>Derives bounds from other settings once configurators have run.</summary>
    void ReapplyDerivedBounds(ConfigurationOptions configuration) => _ = configuration;
}
