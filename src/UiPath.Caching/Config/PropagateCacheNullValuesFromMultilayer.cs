namespace UiPath.Caching.Config;

/// <summary>
/// Forces <see cref="RedisCacheOptions.CacheNullValues"/> on whenever the wrapping
/// <see cref="InMemoryRedisCacheOptions"/> has it on, so cached nulls survive L1 eviction and stay
/// visible across nodes.
/// </summary>
internal sealed class PropagateCacheNullValuesFromMultilayer : IPostConfigureOptions<RedisCacheOptions>
{
    private readonly IOptions<InMemoryRedisCacheOptions> _source;
    private readonly ILogger<PropagateCacheNullValuesFromMultilayer> _logger;

    public PropagateCacheNullValuesFromMultilayer(
        IOptions<InMemoryRedisCacheOptions> source,
        ILoggerFactory loggerFactory)
    {
        _source = source;
        _logger = loggerFactory.CreateLogger<PropagateCacheNullValuesFromMultilayer>();
    }

    public void PostConfigure(string? name, RedisCacheOptions options)
    {
        if (name != Options.DefaultName || !_source.Value.CacheNullValues || options.CacheNullValues)
        {
            return;
        }

        _logger.LogDebug(
            "Forcing RedisCacheOptions.CacheNullValues=true to match InMemoryRedisCacheOptions.CacheNullValues=true. " +
            "The two must agree so cached nulls are visible across nodes; the inner cache's explicit false (if any) is overridden.");
        options.CacheNullValues = true;
    }
}
