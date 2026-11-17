using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.PluginContract.Tests.Acme;
using UiPath.Caching.PluginContract.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PluginContract.Tests;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class AcmeNamedStackTests(RedisFixture fixture) : AcmeNodeTests(fixture)
{
    private const string Stack = "Named";

    [Fact]
    public async Task A_named_stack_writes_and_reads_the_plugin_it_inherits_and_the_application_disposes_it_once()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var shared = new AcmeValueSerializer();
        var root = Root(shared, stackSerializer: null, other: null);
        var cache = root.GetRequiredKeyedService<ICacheFactory>(Stack).CreateCache(KnownCacheProviderNames.Redis);
        var policy = root.GetRequiredKeyedService<ICachePolicyFactory>(Stack).Resolve(Policy)!;
        var key = Unique();

        await cache.SetAsync(key, new Profile("x", 1), policy, Ct);

        (await Raw(key))!.Take(2).Should().Equal(0x1F, 0x00);
        (await cache.GetAsync<Profile>(key, policy, Ct)).Should().Be(new Profile("x", 1));
        shared.Disposals.Should().Be(0);
        await root.DisposeAsync();
        shared.Disposals.Should().Be(1, "the stack borrows the instance; the application owns it");
    }

    [Fact]
    public async Task A_named_stacks_own_serializer_replaces_an_inherited_one_of_the_same_name_and_the_stack_owns_it()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var inherited = new AcmeValueSerializer();
        var own = new AcmeValueSerializer();
        var root = Root(inherited, stackSerializer: own, other: null);
        var cache = root.GetRequiredKeyedService<ICacheFactory>(Stack).CreateCache(KnownCacheProviderNames.Redis);
        var policy = root.GetRequiredKeyedService<ICachePolicyFactory>(Stack).Resolve(Policy)!;
        var key = Unique();

        await cache.SetAsync(key, new Profile("x", 1), policy, Ct);

        (await Raw(key))!.Take(2).Should().Equal(0x1F, 0x00);
        own.Serialized.Should().Be(1, "the stack's own serializer handled the write");
        inherited.Serialized.Should().Be(0, "the inherited one was replaced, not used");
        (own.Disposals, inherited.Disposals).Should().Be((0, 0));
        await root.DisposeAsync();
        (own.Disposals, inherited.Disposals).Should().Be((1, 1), "each is disposed once, by its own container");
    }

    [Fact]
    public void A_named_stack_cannot_claim_an_id_an_inherited_plugin_holds_under_another_name()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var root = Root(new AcmeValueSerializer(), stackSerializer: null, other: new AcmeValueSerializer(ids: [0x100]));

        var act = () => root.GetRequiredKeyedService<ICacheFactory>(Stack);

        act.Should().Throw<ArgumentException>().WithMessage("*0x100*acme*");
    }

    [Fact]
    public void Event_formats_are_not_forwarded_to_a_named_stack()
    {
        Assert.SkipUnless(Fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var root = Root(new AcmeValueSerializer(), stackSerializer: null, other: null);
        var application = root.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();
        var stack = root.GetKeyedService<IEventFormatterProxy<ICacheEvent>>(Stack);
        var sent = new CacheEvent { Id = "1", Type = KnownEventTypes.CacheSet, Source = new Uri("urn:a"), Data = new CacheEventData("k") };

        application.Encode(sent).Span[0].Should().Be((byte)AcmeFormats.Event);
        stack.Should().BeNull("the stack has no event formatter of its own and the application's is not lent to it");
    }

    private ServiceProvider Root(AcmeValueSerializer shared, AcmeValueSerializer? stackSerializer, AcmeValueSerializer? other)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = App,
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = Fixture.ConnectionString,
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
            ["Caching:Connections:Named:ConnectionString"] = $"{Fixture.ConnectionString},name=named",
            [$"Caching:Policies:{Policy}:Serializer:Write"] = AcmeFormats.Name,
            [$"Caching:Policies:{Policy}:DistributedExpiration"] = "00:05:00",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(
            configuration,
            b =>
            {
                b.AddRedisConnection();
                b.AddRedis(o => o.FormatNegotiation.Mode = FormatNegotiationMode.Force);
                b.AddBroadcast();
                b.AddValueSerializer(AcmeFormats.Name, AcmeFormats.ValueIds, _ => shared);
                b.AddEventFormat(AcmeFormats.Name, AcmeFormats.Event, _ => new AcmeEventFormat(), write: true);
            });
        services.AddNamedCaching(
            Stack,
            configuration,
            b =>
            {
                b.AddRedis(o => o.FormatNegotiation.Mode = FormatNegotiationMode.Force);
                if (stackSerializer is not null)
                {
                    b.AddValueSerializer(AcmeFormats.Name, AcmeFormats.ValueIds, _ => stackSerializer);
                }

                if (other is not null)
                {
                    b.AddValueSerializer("other", other.FormatIds, _ => other);
                }
            }).Expose<IEventFormatterProxy<ICacheEvent>>();
        return Track(services.BuildServiceProvider());
    }

    public sealed record Profile(string Name, int Version);
}
