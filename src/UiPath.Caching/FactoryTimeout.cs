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

    public static Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? factoryTimeout,
        CacheKey cacheKey,
        string cacheName,
        ICachingTelemetryProvider telemetry,
        CancellationToken token,
        string source = SourceForeground)
    {
        if (factoryTimeout is not null && factoryTimeout.Value > TimeSpan.Zero)
        {
            return RunWithTimeoutAsync(factory, factoryTimeout.Value, cacheKey, cacheName, telemetry, source, token);
        }

        // Nothing to time, so the factory's own task is handed back: an async wrapper would allocate a second one per run. A throw before it yields stays on the task, as the wrapper reported it.
        try
        {
            return factory(token);
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    private static async Task<T> RunWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> factory,
        TimeSpan factoryTimeout,
        CacheKey cacheKey,
        string cacheName,
        ICachingTelemetryProvider telemetry,
        string source,
        CancellationToken token)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        // CancelAfter throws when the delay exceeds Int32.MaxValue ms; clamp so misconfigured values degrade gracefully.
        var clampedTimeout = factoryTimeout.TotalMilliseconds > int.MaxValue
            ? TimeSpan.FromMilliseconds(int.MaxValue)
            : factoryTimeout;
        linkedCts.CancelAfter(clampedTimeout);
        try
        {
            return await factory(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            telemetry.TryTrackEvent(EventName,
            [
                new(TagCacheName, cacheName),
                new(TagCacheKey, cacheKey.Name),
                new(TagSource, source),
            ]);
            throw new TimeoutException($"Cache factory for key '{cacheKey.Name}' exceeded {clampedTimeout}.");
        }
    }
}
