using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Config;

/// <summary>Drives real caches through the container with a Trace-level logger and inspects every line they wrote.</summary>
public class KeyLoggingTests
{
    private const string SecretKey = "session:Secret-Session-Key";
    private const string PlainKey = "order:Plain-Order-Key";
    private const string NumericKey = "1234567";

    [Fact]
    public void Options_do_not_mask_by_default()
    {
        new InMemoryCacheOptions().MaskKeys.Should().BeFalse();
        new InMemoryRedisCacheOptions().MaskKeys.Should().BeFalse();
        new RedisCacheOptions().MaskKeys.Should().BeFalse();
        new InMemoryCacheOptions().MaskedKeyPrefixes.Should().BeEmpty();
    }

    [Fact]
    public async Task Cache_logs_keys_in_full_when_masking_is_off()
    {
        var (provider, logs) = Build(maskKeys: false);
        using var _ = provider;
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemory);

        await cache.SetAsync(SecretKey, "value", token: TestContext.Current.CancellationToken);
        await cache.GetAsync<string>(SecretKey, token: TestContext.Current.CancellationToken);

        logs.Messages.Should().Contain(m => m.Contains(SecretKey, StringComparison.OrdinalIgnoreCase));
        logs.Messages.Should().NotContain(m => m.Contains("****"));
    }

    [Fact]
    public async Task Cache_masks_only_keys_under_a_listed_prefix()
    {
        var (provider, logs) = Build(maskKeys: true, "session:");
        using var _ = provider;
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemory);

        await cache.SetAsync(SecretKey, "value", token: TestContext.Current.CancellationToken);
        await cache.SetAsync(PlainKey, "value", token: TestContext.Current.CancellationToken);
        await cache.SetAsync(NumericKey, "value", token: TestContext.Current.CancellationToken);

        logs.Messages.Should().NotContain(m => m.Contains(SecretKey, StringComparison.OrdinalIgnoreCase));
        logs.Messages.Should().Contain(m => m.Contains("session:sec****", StringComparison.OrdinalIgnoreCase));
        logs.Messages.Should().Contain(m => m.Contains(PlainKey, StringComparison.OrdinalIgnoreCase));
        logs.Messages.Should().Contain(m => m.Contains(NumericKey));
    }

    [Fact]
    public async Task Hash_cache_masks_only_keys_under_a_listed_prefix()
    {
        var (provider, logs) = Build(maskKeys: true, "session:");
        using var _ = provider;
        var cache = provider.GetRequiredService<ICacheFactory>().CreateHashCache(KnownCacheProviderNames.InMemory);

        await cache.SetAsync(SecretKey, new Dictionary<string, string?> { ["f"] = "v" }, token: TestContext.Current.CancellationToken);
        await cache.SetAsync(PlainKey, new Dictionary<string, string?> { ["f"] = "v" }, token: TestContext.Current.CancellationToken);
        await cache.GetAsync<string>(SecretKey, ["f"], token: TestContext.Current.CancellationToken);

        logs.Messages.Should().NotContain(m => m.Contains(SecretKey, StringComparison.OrdinalIgnoreCase));
        logs.Messages.Should().Contain(m => m.Contains("session:sec****", StringComparison.OrdinalIgnoreCase));
        logs.Messages.Should().Contain(m => m.Contains(PlainKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The adapter's keys are the consumer's, so its tier masks them all under the adapter's own prefix, whatever the tier's settings.</summary>
    [Fact]
    public void Distributed_cache_masks_its_keys_even_a_guid()
    {
        var (provider, logs) = Build(maskKeys: false);
        using var _ = provider;
        var cache = provider.GetRequiredService<IDistributedCache>();
        var sessionId = Guid.NewGuid().ToString();

        cache.Set(sessionId, [1], new DistributedCacheEntryOptions());
        cache.Get(sessionId);
        cache.Get("missing-" + sessionId);

        var masked = "d:" + sessionId[..3] + "****";
        logs.Messages.Should().NotBeEmpty();
        logs.Messages.Should().NotContain(m => m.Contains(sessionId, StringComparison.OrdinalIgnoreCase));
        logs.Messages.Should().Contain(m => m.Contains(masked, StringComparison.OrdinalIgnoreCase), "the adapter's prefix stays, the consumer's key is masked");
    }

    private static (ServiceProvider Provider, CapturingLoggerProvider Logs) Build(bool maskKeys, params string[] prefixes)
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddCaching(
            b =>
            {
                b.AddMemory(o =>
                {
                    o.MaskKeys = maskKeys;
                    o.MaskedKeyPrefixes = [.. prefixes];
                });
                b.AddDistributedCache(KnownCacheProviderNames.InMemory);
            },
            o => o.DefaultCache = KnownCacheProviderNames.InMemory);
        return (services.BuildServiceProvider(), logs);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
