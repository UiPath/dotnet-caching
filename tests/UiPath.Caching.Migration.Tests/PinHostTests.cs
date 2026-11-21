using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class PinHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_a_pin_setting_and_no_reachable_redis_a_group_that_is_not_pinned_fails_closed_and_the_host_still_starts()
    {
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await using var provider = Build(source, "g", router => router.UsePinWhen(_ => false));

        var router = provider.GetRequiredService<IRouter>();

        var read = async () => await router.Get<Item>("items").TryGetAsync("k", Ct);
        await read.Should().ThrowAsync<CacheUnavailableException>();
        source.Reads.Should().Be(0);
        await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), Ct);
    }

    [Fact]
    public async Task With_a_pinned_group_and_no_library_the_call_fails_unavailable_and_never_reaches_the_source()
    {
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await using var provider = Build(source, "g", router => router.UsePinWhen(_ => true));

        var router = provider.GetRequiredService<IRouter>();

        var read = async () => await router.Get<Item>("items").GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made")), default, Ct);
        await read.Should().ThrowAsync<CacheUnavailableException>();
        source.Reads.Should().Be(0);
        source.Writes.Should().Be(0);
        await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), Ct);
    }

    [Fact]
    public async Task Without_a_pin_setting_the_router_is_unchanged_and_serves_from_the_source_when_the_library_is_down()
    {
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await using var provider = Build(source, "g", _ => { });

        var value = await provider.GetRequiredService<IRouter>().Get<Item>("items").GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("made")), default, Ct);

        value.Should().Be(new Item("made"));
        source.Writes.Should().Be(1);
        await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), Ct);
    }

    [Fact]
    public async Task A_group_name_that_cannot_be_a_token_fails_router_creation_when_a_pin_setting_is_registered()
    {
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await using var provider = Build(source, "a+b", router => router.UsePinWhen(_ => false));

        var create = () => provider.GetRequiredService<IRouter>();

        create.Should().Throw<InvalidOperationException>().WithMessage("*a+b*");
    }

    [Fact]
    public void The_pin_setting_must_not_be_null()
    {
        var set = () => new RouterBuilder().UsePinWhen(null!);

        set.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_banner_says_a_pin_setting_is_registered_and_that_the_pin_comes_first()
    {
        var banner = RouterBanner.Describe(new RouterBuilder().UsePinWhen(_ => true), "p1");

        banner.Should().Contain("pin=UsePinWhen").And.Contain("precedence, first wins: pin");
    }

    private static ServiceProvider Build(FakeBackend source, string group, Action<RouterBuilder> configure)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = $"pin{Guid.NewGuid():N}"[..10],
            ["Caching:SourceUri"] = "urn:pin-test-host",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = "127.0.0.1:1",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(configuration, builder => builder.AddRedisConnection());
        services.AddCachingRouter(
            configuration,
            router =>
            {
                router
                    .Add(new RouterCacheRegistration<Item> { Name = "items", Group = group, Source = source, RedisKey = key => key })
                    .UseLibraryWhen(_ => true)
                    .UseEnvelope(new LegacyEnvelopeProxy());
                configure(router);
            });
        return services.BuildServiceProvider();
    }
}
