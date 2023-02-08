namespace UiPath.Platform.Caching;

public record struct RegionCacheEntryOptions(
    DateTimeOffset? ExpireTime = default,
    TimeSpan? TimeToLive = default,
    RegionCacheSetOption SetOption = RegionCacheSetOption.KeyReplace,
    IDictionary<string, string?>? ExtendedProperties = null);
