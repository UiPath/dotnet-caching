namespace UiPath.Caching.Distributed;

public class UiPathDistributedCacheOptions
{
    /// <summary>Optional key prefix, prepended to every caller key.</summary>
    public string? InstanceName { get; set; }

    /// <summary>Optional <see cref="CachePolicy"/> name, resolved at construction; absent, the provider's default policy applies.</summary>
    public string? PolicyName { get; set; }
}
