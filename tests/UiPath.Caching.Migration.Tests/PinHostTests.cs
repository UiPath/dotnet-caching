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
    public async Task A_pin_setting_with_an_upgrade_hold_shorter_than_the_advertisement_lifetime_fails_router_creation()
    {
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var hold = new Dictionary<string, string?> { ["Caching:Redis:FormatNegotiation:UpgradeHold"] = "00:02:29" };
        await using var provider = Build(source, "g", router => router.UsePinWhen(_ => false), hold);

        var create = () => provider.GetRequiredService<IRouter>();

        create.Should().Throw<InvalidOperationException>().WithMessage("*UpgradeHold*LivenessWindow + 2 x AdvertiseInterval*");
    }

    [Fact]
    public async Task A_pin_setting_with_an_upgrade_hold_of_exactly_the_advertisement_lifetime_is_accepted()
    {
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var hold = new Dictionary<string, string?> { ["Caching:Redis:FormatNegotiation:UpgradeHold"] = "00:02:30" };
        await using var provider = Build(source, "g", router => router.UsePinWhen(_ => false), hold);

        provider.GetRequiredService<IRouter>().Should().NotBeNull();
        await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), Ct);
    }

    [Fact]
    public async Task A_pin_setting_in_force_mode_fails_router_creation()
    {
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var force = new Dictionary<string, string?> { ["Caching:Redis:FormatNegotiation:Mode"] = "Force" };
        await using var provider = Build(source, "g", router => router.UsePinWhen(_ => false), force);

        var create = () => provider.GetRequiredService<IRouter>();

        create.Should().Throw<InvalidOperationException>().WithMessage("*Force*");
    }

    [Fact]
    public async Task A_pin_setting_without_a_pin_store_fails_router_creation_naming_the_advertisement()
    {
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var broken = new Dictionary<string, string?> { ["Caching:Redis:FormatNegotiation:AdvertiseInterval"] = "00:00:00" };
        await using var provider = Build(source, "g", router => router.UsePinWhen(_ => false), broken);

        var create = () => provider.GetRequiredService<IRouter>();

        create.Should().Throw<InvalidOperationException>().WithMessage("*pin store*advertisement*");
    }
    [Fact]
    public async Task A_write_format_setting_without_a_pin_setting_fails_router_registration()
    {
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        var build = () => Build(source, "g", router => router.UseValueSerializers(_ => { }).UseWriteFormatWhen(_ => "messagepack"));

        build.Should().Throw<InvalidOperationException>().WithMessage("*UseWriteFormatWhen*UsePinWhen*");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_write_format_setting_without_value_serializers_fails_router_registration()
    {
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        var build = () => Build(source, "g", router => router.UsePinWhen(_ => true).UseWriteFormatWhen(_ => "messagepack"));

        build.Should().Throw<InvalidOperationException>().WithMessage("*UseWriteFormatWhen*UseValueSerializers*");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task With_a_write_format_setting_and_no_reachable_redis_a_pinned_group_still_fails_closed_and_never_reaches_the_source()
    {
        var source = new FakeBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var provider = Build(source, "g", router => router.UsePinWhen(_ => true).UseValueSerializers(stack => stack.AddValueSerializer("messagepack", [TestFormatSerializer.Id], _ => new TestFormatSerializer())).UseWriteFormatWhen(_ => throw new InvalidOperationException("no setting")));
        try
        {
            var router = provider.GetRequiredService<IRouter>();

            var write = await router.Get<Item>("items").SetAsync("k", new Item("v"), default, Ct);
            write.Should().Be(WriteOutcome.NotSent);
            source.Writes.Should().Be(0);
        }
        finally
        {
            // One bounded dispose: a dispose that hangs fails the test instead of the run.
            await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
    }

    [Fact]
    public void The_write_format_and_the_value_serializers_must_not_be_null()
    {
        var builder = new RouterBuilder();

        builder.Invoking(b => b.UseWriteFormatWhen(null!)).Should().Throw<ArgumentNullException>();
        builder.Invoking(b => b.UseValueSerializers(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_banner_says_whether_a_write_format_setting_is_registered()
    {
        RouterBanner.Describe(new RouterBuilder(), "p1").Should().Contain("write format=stack default");
        RouterBanner.Describe(new RouterBuilder().UseWriteFormatWhen(_ => null), "p1").Should().Contain("write format=UseWriteFormatWhen");
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

    private static ServiceProvider Build(FakeBackend source, string group, Action<RouterBuilder> configure, Dictionary<string, string?>? extra = null)
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
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

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
