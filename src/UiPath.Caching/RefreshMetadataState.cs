namespace UiPath.Caching;

internal record struct RefreshMetadataState(CacheKey CacheKey, TopicKey TopicKey, ICacheEntry CacheEntity, ICacheChangeToken Token, Type EntryType, TimeSpan? MaxExpiration);
