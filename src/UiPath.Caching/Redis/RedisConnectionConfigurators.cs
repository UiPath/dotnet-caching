namespace UiPath.Caching.Redis;

internal static class RedisConnectionConfigurators
{
    public static async ValueTask ApplyAsync(ConfigurationOptions configuration, IEnumerable<IRedisConnectionConfigurator>? configurators, CancellationToken cancellationToken)
    {
        if (configurators is null)
        {
            return;
        }

        foreach (var configurator in configurators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await configurator.ConfigureAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
    }
}
