namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {CacheKey}")]
    private partial void LogCacheMissed(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Batch cache missed. generating {Count} keys for {CacheKey}")]
    private partial void LogBatchCacheMissed(LoggedKeys cacheKey, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {CacheKey}: a null value cannot be represented unless CacheNullValues is on.")]
    private partial void LogTryAddSkippedUnrepresentableValue(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache TryAdd for cacheKey {CacheKey}")]
    private partial void LogInnerCacheTryAddError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TryAdd won cacheKey {CacheKey} in the inner cache but could not broadcast or populate the local tier. The add still stands.")]
    private partial void LogTryAddLocalPropagationFailed(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TryAdd for {CacheKey} reported not-added: a generator for the key was still running after Lock.LocalSingleFlightTimeout, and without waiting for it two in-process callers could both be told they added the key, or the generated value could replace the added one. Raise Lock.LocalSingleFlightTimeout if this key is contended.")]
    private partial void LogTryAddKeyBusy(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {CacheKey}: the requested expiration {Expiration} is not in the future, so the entry would retain nothing.")]
    private partial void LogTryAddSkippedExpiredEntry(LoggedKey cacheKey, DateTimeOffset expiration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryAdd skipped for {CacheKey}: the effective local retention {LocalMaxExpiration} is not positive, and on this provider it is the only retention.")]
    private partial void LogTryAddSkippedNonPositiveLocalRetention(LoggedKey cacheKey, TimeSpan localMaxExpiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TryAdd won cacheKey {CacheKey} but the invalidation broadcast reported not-published. The add still stands; peers may serve a stale copy until it expires.")]
    private partial void LogTryAddBroadcastNotPublished(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replacing cached key {CacheKey}")]
    private partial void LogReplacingCachedKey(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Setting local only for cacheKeys {CacheKeys}")]
    private partial void LogSettingLocalOnlyForCacheKeys(LoggedKeys cacheKeys);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing cached. Key {CacheKey}")]
    private partial void LogClearingCached(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing inner cache key {CacheKey} at expiration {Expiration}")]
    private partial void LogRefreshingInnerCacheKey(LoggedKey cacheKey, DateTimeOffset? expiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache refresh value for cacheKey {CacheKey}")]
    private partial void LogInnerCacheRefreshError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache contains for cacheKey {CacheKey}")]
    private partial void LogInnerCacheContainsError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing local cached. cacheKey {CacheKey}")]
    private partial void LogClearingLocalCached(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache remove cacheKey {CacheKey}")]
    private partial void LogInnerCacheRemoveError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache remove cacheKeys {CacheKeys}")]
    private partial void LogInnerCacheRemoveKeysError(Exception ex, LoggedKeys cacheKeys);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found local. {CacheKey}")]
    private partial void LogFoundLocal(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Using primary only when disconnected. Returning local for cacheKey {CacheKey}")]
    private partial void LogUsingPrimaryOnlyWhenDisconnected(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Returning default for cacheKey {CacheKey}")]
    private partial void LogReturningDefaultDisconnected(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found inner cache copy at cacheKey {CacheKey}")]
    private partial void LogFoundInnerCacheCopy(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set value for {CacheKeys}")]
    private partial void LogInnerCacheSetKeysError(Exception ex, LoggedKeys cacheKeys);
}
