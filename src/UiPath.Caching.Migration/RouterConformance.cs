using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration;

/// <summary>
/// The parity check an application runs in its CI, against a test Redis, for each registered cache: what the source writes the library reads, what the
/// library writes the source reads, both write the same bytes and the same deadline. Give samples for every cached shape, including default and empty
/// values. The sample keys are written, read and deleted.
/// </summary>
public static class RouterConformance
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    // Less than the two minutes between the source's and the library's deadlines, so the check tells them apart.
    private static readonly TimeSpan TtlTolerance = TimeSpan.FromSeconds(30);

    public static async Task<ConformanceReport> CheckAsync<T>(
        IServiceProvider services,
        string cacheName,
        IEnumerable<KeyValuePair<string, T>> samples,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(samples);
        var router = services.GetRequiredService<IRouter>() as Router ?? throw new InvalidOperationException("The conformance check needs the router that AddCachingRouter registers.");
        var cache = router.Find<T>(cacheName) ?? throw new ArgumentException($"No cache named '{cacheName}' of {typeof(T).Name} is registered with the router.", nameof(cacheName));
        var envelope = router.Envelope ?? throw new InvalidOperationException("The router has no envelope.");
        var connector = services.GetRequiredService<IRedisConnector>();
        await connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var store = services.GetRequiredKeyedService<ICacheFactory>(RouterServiceCollectionExtensions.StackName).CreateCache(KnownCacheProviderNames.Redis).AsMigrationCache();
        var library = new LibraryPathClient<T>(store, connector, cache.Registration.RedisKey, Lifetime);
        var source = new SourcePathClient<T>(cache.Registration.Source);
        var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var failures = new List<string>();
        var index = 0;
        foreach (var (key, value) in samples)
        {
            var label = $"sample {index++}";
            var redisKey = (RedisKey)cache.Registration.RedisKey(key);
            var deadline = clock.GetUtcNow() + Lifetime;
            try
            {
                await ExpectCommittedAsync(source.SetAsync(key, value, deadline, cancellationToken), label, "the source write", failures).ConfigureAwait(false);
                var sourceBytes = (byte[]?)await connector.Database.StringGetAsync(redisKey).ConfigureAwait(false);
                await ExpectTtlAsync(connector.Database, redisKey, deadline, clock, label, "the source write", failures).ConfigureAwait(false);
                var libraryRead = await library.ReadAsync(key, cancellationToken).ConfigureAwait(false);
                if (libraryRead.Kind != ReadOutcome.Found || !Same(envelope, libraryRead.Value, value))
                {
                    failures.Add($"{label}: the library read {libraryRead.Kind} for what the source wrote, not the value.");
                }

                // On an empty key and with another deadline, so a library write that stores nothing cannot pass on what the source left.
                await connector.Database.KeyDeleteAsync(redisKey).ConfigureAwait(false);
                var libraryDeadline = deadline + TimeSpan.FromMinutes(2);
                await ExpectCommittedAsync(library.SetAsync(key, value, libraryDeadline, cancellationToken), label, "the library write", failures).ConfigureAwait(false);
                var libraryBytes = (byte[]?)await connector.Database.StringGetAsync(redisKey).ConfigureAwait(false);
                if (libraryBytes is null)
                {
                    failures.Add($"{label}: the library write stored nothing.");
                }

                await ExpectTtlAsync(connector.Database, redisKey, libraryDeadline, clock, label, "the library write", failures).ConfigureAwait(false);
                var sourceRead = await source.ReadAsync(key, cancellationToken).ConfigureAwait(false);
                if (sourceRead.Kind != ReadOutcome.Found || !Same(envelope, sourceRead.Value, value))
                {
                    failures.Add($"{label}: the source read {sourceRead.Kind} for what the library wrote, not the value.");
                }

                if (!(sourceBytes ?? []).AsSpan().SequenceEqual(libraryBytes ?? []))
                {
                    failures.Add($"{label}: the two clients write different bytes ({sourceBytes?.Length ?? 0} from the source, {libraryBytes?.Length ?? 0} from the library, first difference at {FirstDifference(sourceBytes, libraryBytes)}).");
                }
            }
            finally
            {
                await connector.Database.KeyDeleteAsync(redisKey).ConfigureAwait(false);
            }
        }

        return new ConformanceReport(index, failures);
    }

    private static async Task ExpectCommittedAsync(ValueTask<WriteResult> write, string label, string what, List<string> failures)
    {
        var result = await write.ConfigureAwait(false);
        if (result.Outcome != WriteOutcome.Committed)
        {
            failures.Add($"{label}: {what} reported {result.Outcome}.");
        }
    }

    private static async Task ExpectTtlAsync(IDatabase database, RedisKey key, DateTimeOffset deadline, TimeProvider clock, string label, string what, List<string> failures)
    {
        var ttl = await database.KeyTimeToLiveAsync(key).ConfigureAwait(false);
        var expected = deadline - clock.GetUtcNow();
        if (ttl is not { } left || (left - expected).Duration() > TtlTolerance)
        {
            failures.Add($"{label}: {what} stored a lifetime of {(ttl is { } t ? t.ToString() : "none")}, not about {expected}.");
        }
    }

    private static bool Same<T>(ISerializerProxy<byte[]> envelope, T? read, T expected)
        where T : class =>
        read is not null && (envelope.Serialize(read) ?? []).AsSpan().SequenceEqual(envelope.Serialize(expected) ?? []);

    private static int FirstDifference(byte[]? first, byte[]? second)
    {
        var length = Math.Min(first?.Length ?? 0, second?.Length ?? 0);
        for (var i = 0; i < length; i++)
        {
            if (first![i] != second![i])
            {
                return i;
            }
        }

        return length;
    }
}
