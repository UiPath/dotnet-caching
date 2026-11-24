using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class RouterWithoutEnvelopeTests
{
    [Fact]
    public async Task A_router_without_an_envelope_builds_and_its_stack_registers_no_header_free_envelope()
    {
        await using var provider = Build(router => { });

        _ = provider.GetRequiredService<IRouter>();

        var json = provider.GetRequiredKeyedService<ISerializerProxy<byte[]>>(RouterServiceCollectionExtensions.StackName);
        json.Should().NotBeAssignableTo<IHeaderFreeSerializerProxy>();
        json.Should().BeOfType<SystemJsonByteSerializerProxy>();
    }

    [Fact]
    public async Task A_router_with_an_envelope_keeps_it_header_free_in_its_stack()
    {
        await using var provider = Build(router => router.UseEnvelope(new LegacyEnvelopeProxy()));

        provider.GetRequiredKeyedService<ISerializerProxy<byte[]>>(RouterServiceCollectionExtensions.StackName)
            .Should().BeAssignableTo<IHeaderFreeSerializerProxy>();
    }

    [Fact]
    public void The_in_place_keys_keep_the_system_fields_only_without_an_envelope()
    {
        new RouterBuilder().InPlaceKeepsSystemFields.Should().BeTrue();
        new RouterBuilder().UseEnvelope(new LegacyEnvelopeProxy()).InPlaceKeepsSystemFields.Should().BeFalse();
    }

    [Fact]
    public void A_channel_bridge_without_an_envelope_fails_the_registration()
    {
        var act = () => Build(router => router.UseChannelBridge(new FakeChannelBridge()));

        act.Should().Throw<InvalidOperationException>().WithMessage("UseChannelBridge needs UseEnvelope*");
    }

    [Fact]
    public async Task A_channel_bridge_with_an_envelope_is_accepted()
    {
        await using var provider = Build(router => router.UseEnvelope(new LegacyEnvelopeProxy()).UseChannelBridge(new FakeChannelBridge()));

        provider.GetService<IRouter>().Should().NotBeNull();
    }

    private static ServiceProvider Build(Action<RouterBuilder> configure)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = "noenv",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = "127.0.0.1:1,abortConnect=false,connectTimeout=100",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(configuration, builder => builder.AddRedisConnection());
        services.AddCachingRouter(configuration, router =>
        {
            router.UseLibraryWhen(_ => false);
            router.Add(new RouterCacheRegistration<string> { Name = "items", Group = "g", Source = new Source(), RedisKey = key => key });
            configure(router);
        });
        return services.BuildServiceProvider();
    }

    private sealed class Source : ISourceCache<string>
    {
        public ValueTask<SourceRead<string>> GetAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<WriteResult> SetAsync(string key, string value, CacheExpiry expiry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<AddResult> TryAddAsync(string key, string value, CacheExpiry expiry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
