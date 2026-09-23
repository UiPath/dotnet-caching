namespace UiPath.Caching.Redis;

internal static class RedisConnectionConfigurators
{
    public static async ValueTask ApplyAsync(ConfigurationOptions configuration, IEnumerable<IRedisConnectionConfigurator>? configurators, IRedisConfigurationOptionsProvider optionsProvider, CancellationToken cancellationToken)
    {
        if (configurators is not null)
        {
            foreach (var configurator in configurators)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await configurator.ConfigureAsync(configuration, cancellationToken).ConfigureAwait(false);
            }
        }

        // Last, so derived bounds follow the final async timeout.
        optionsProvider.ReapplyDerivedBounds(configuration);
    }
}
