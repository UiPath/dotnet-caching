using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Distributed;

[Collection("CacheKeyDefaultCasing")]   // mutates CacheKey.DefaultCasing — serialized collection
public class DistributedCacheEndToEndTests
{
    /// <summary>
    /// Drives both the adapter and the backing memory cache, so expiration can be advanced
    /// deterministically instead of racing the wall clock.
    /// </summary>
    private sealed class FakeClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
    }

    private static readonly DateTimeOffset Start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static ServiceProvider Build(FakeClock clock)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISystemClock>(clock);
        services.AddCaching(b =>
        {
            b.AddMemory(o => o.Clock = clock);
            b.AddDistributedCache(KnownCacheProviderNames.InMemory);
        });
        return services.BuildServiceProvider();
    }

    /// <summary>Each touch falls inside the sliding window, so the entry survives well past one window — until the absolute cap passes.</summary>
    [Fact]
    public async Task Session_scenario_idle_keeps_alive_and_absolute_cap_wins()
    {
        var clock = new FakeClock(Start);
        using var provider = Build(clock);
        var cache = provider.GetRequiredService<IDistributedCache>();
        var token = TestContext.Current.CancellationToken;

        await cache.SetAsync("Session-AbC", [1], new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(20),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2),
        }, token);

        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(15));
            (await cache.GetAsync("Session-AbC", token)).Should().NotBeNull("touch {0} slides the window", i);
        }

        clock.Advance(TimeSpan.FromHours(2));
        (await cache.GetAsync("Session-AbC", token)).Should().BeNull();
    }

    [Fact]
    public async Task Refresh_extends_without_reading_data()
    {
        var clock = new FakeClock(Start);
        using var provider = Build(clock);
        var cache = provider.GetRequiredService<IDistributedCache>();
        var token = TestContext.Current.CancellationToken;

        await cache.SetAsync("k", [1], new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(10) }, token);

        clock.Advance(TimeSpan.FromMinutes(8));
        await cache.RefreshAsync("k", token);
        clock.Advance(TimeSpan.FromMinutes(8));   // 16 min total — gone without the refresh

        (await cache.GetAsync("k", token)).Should().NotBeNull();
    }

    /// <summary>Flipping the ambient default must not change how the adapter keys anything.</summary>
    [Fact]
    public async Task Global_key_casing_does_not_move_distributed_keys()
    {
        try
        {
            var clock = new FakeClock(Start);
            using var provider = Build(clock);
            var cache = provider.GetRequiredService<IDistributedCache>();
            var token = TestContext.Current.CancellationToken;

            CacheKey.DefaultCasing = CacheKeyCasing.Insensitive;
            await cache.SetAsync("AbC", [1], new DistributedCacheEntryOptions(), token);

            CacheKey.DefaultCasing = CacheKeyCasing.Sensitive;
            (await cache.GetAsync("AbC", token)).Should().Equal(1);
            (await cache.GetAsync("abc", token)).Should().BeNull();
        }
        finally
        {
            CacheKey.DefaultCasing = CacheKeyCasing.Insensitive;
        }
    }

    /// <summary>Documented deviation: <see cref="CacheKey"/> trims, so " k " and "k" are one key.</summary>
    [Fact]
    public async Task Whitespace_wrapped_keys_collide_by_design()
    {
        var clock = new FakeClock(Start);
        using var provider = Build(clock);
        var cache = provider.GetRequiredService<IDistributedCache>();
        var token = TestContext.Current.CancellationToken;

        await cache.SetAsync(" k ", [1], new DistributedCacheEntryOptions(), token);
        (await cache.GetAsync("k", token)).Should().Equal(1);
    }
}
