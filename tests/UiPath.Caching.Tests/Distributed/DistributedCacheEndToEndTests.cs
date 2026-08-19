using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Distributed;

[Collection("CacheKeyDefaultCasing")]
public class DistributedCacheEndToEndTests
{
    private static ServiceProvider Build(string? instanceName = "sess:")
    {
        var services = new ServiceCollection();
        services.AddCaching(b =>
        {
            b.AddMemory(_ => { });
            b.AddDistributedCache(KnownCacheProviderNames.InMemory, o => o.InstanceName = instanceName);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Session_scenario_idle_keeps_alive_and_absolute_cap_wins()
    {
        using var provider = Build();
        var cache = provider.GetRequiredService<IDistributedCache>();
        var token = TestContext.Current.CancellationToken;

        await cache.SetAsync("Session-AbC", [1], new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMilliseconds(400),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(2),
        }, token);

        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(200, token);
            (await cache.GetAsync("Session-AbC", token)).Should().NotBeNull("touch {0} slides the window", i);
        }

        await Task.Delay(2100, token);
        (await cache.GetAsync("Session-AbC", token)).Should().BeNull();
    }

    [Fact]
    public async Task Refresh_extends_without_reading_data()
    {
        using var provider = Build();
        var cache = provider.GetRequiredService<IDistributedCache>();
        var token = TestContext.Current.CancellationToken;

        await cache.SetAsync("k", [1], new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMilliseconds(500) }, token);
        await Task.Delay(300, token);
        await cache.RefreshAsync("k", token);
        await Task.Delay(300, token);

        (await cache.GetAsync("k", token)).Should().NotBeNull();
    }

    [Fact]
    public async Task Global_key_casing_does_not_move_distributed_keys()
    {
        try
        {
            using var provider = Build();
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

    [Fact]
    public async Task Whitespace_wrapped_keys_collide_by_design_without_a_prefix()
    {
        using var provider = Build(instanceName: null);
        var cache = provider.GetRequiredService<IDistributedCache>();
        var token = TestContext.Current.CancellationToken;

        await cache.SetAsync(" k ", [1], new DistributedCacheEntryOptions(), token);
        (await cache.GetAsync("k", token)).Should().Equal(1);
    }

    [Fact]
    public async Task With_a_prefix_leading_whitespace_becomes_interior_and_survives()
    {
        using var provider = Build();
        var cache = provider.GetRequiredService<IDistributedCache>();
        var token = TestContext.Current.CancellationToken;

        await cache.SetAsync(" k ", [1], new DistributedCacheEntryOptions(), token);
        (await cache.GetAsync("k", token)).Should().BeNull();
        (await cache.GetAsync(" k ", token)).Should().Equal(1);
    }
}
