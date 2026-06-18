using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal static class FactoryTimeout
{
    public const string SourceForeground = "foreground";
    public const string SourceRehydrateLock = "rehydrate-lock";

    private const string EventName = "cache.factory.timed_out";
    private const string TagCacheName = "cache.name";
    private const string TagCacheKey = "cache.key";
    private const string TagSource = "source";

    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? factoryTimeout,
        CacheKey cacheKey,
        string cacheName,
        ICachingTelemetryProvider telemetry,
        CancellationToken token,
        string source = SourceForeground)
    {
        if (factoryTimeout is null || factoryTimeout.Value <= TimeSpan.Zero)
        {
            return await factory(token).ConfigureAwait(false);
        }
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        // CancelAfter throws when the delay exceeds Int32.MaxValue ms; clamp so misconfigured values degrade gracefully.
        var clampedTimeout = factoryTimeout.Value.TotalMilliseconds > int.MaxValue
            ? TimeSpan.FromMilliseconds(int.MaxValue)
            : factoryTimeout.Value;
        linkedCts.CancelAfter(clampedTimeout);
        try
        {
            return await factory(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            telemetry.TrackEvent(EventName,
            [
                new(TagCacheName, cacheName),
                new(TagCacheKey, cacheKey.Name),
                new(TagSource, source),
            ]);
            throw new TimeoutException($"Cache factory for key '{cacheKey.Name}' exceeded {clampedTimeout}.");
        }
    }
}
