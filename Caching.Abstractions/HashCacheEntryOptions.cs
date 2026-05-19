namespace UiPath.Platform.Caching;

public record struct HashCacheEntryOptions(
    DateTimeOffset? ExpireTime = default,
    TimeSpan? TimeToLive = default,
    IDictionary<string, string?>? Metadata = null,
    HashCacheSetOption SetOption = HashCacheSetOption.KeyReplace);
