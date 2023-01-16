namespace UiPath.Platform.Caching;

public record struct RegionCacheEntryOptions(DateTimeOffset? ExpireTime = default, TimeSpan? TimeToLive = default, IDictionary<string, string?>? ExtendedProperties = null);
