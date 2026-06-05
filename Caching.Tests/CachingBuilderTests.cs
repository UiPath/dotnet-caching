using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Platform.Caching.Config;
using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Polly;

namespace UiPath.Platform.Caching.Tests;

public class CachingBuilderTests
{
    [Fact]
    public void Two_builders_with_same_key_each_fire_their_own_callback()
    {
        var key = new object();
        var firedA = 0;
        var firedB = 0;

        var builderA = new CachingBuilder(new ServiceCollection());
        var builderB = new CachingBuilder(new ServiceCollection());

        builderA.RegisterOnCompleteCallback(key, _ => firedA++);
        builderB.RegisterOnCompleteCallback(key, _ => firedB++);

        builderA.Complete();
        builderB.Complete();

        firedA.Should().Be(1);
        firedB.Should().Be(1);
    }

    [Fact]
    public void Same_key_registered_twice_on_one_builder_fires_callback_once()
    {
        var key = new object();
        var fired = 0;

        var builder = new CachingBuilder(new ServiceCollection());
        builder.RegisterOnCompleteCallback(key, _ => fired++);
        builder.RegisterOnCompleteCallback(key, _ => fired++);

        builder.Complete();

        fired.Should().Be(1);
    }

    [Fact]
    public void Different_keys_on_one_builder_each_fire()
    {
        var fired = 0;

        var builder = new CachingBuilder(new ServiceCollection());
        builder.RegisterOnCompleteCallback("a", _ => fired++);
        builder.RegisterOnCompleteCallback("b", _ => fired++);

        builder.Complete();

        fired.Should().Be(2);
    }

    [Fact]
    public void Two_builders_with_full_pipeline_each_resolve_real_services()
    {
        using var providerA = BuildContainer();
        using var providerB = BuildContainer();

        providerA.GetRequiredService<IChangeTokenFactory>()
            .Should().BeOfType<ChangeTokenFactory<RedisValue>>();
        providerA.GetRequiredService<IResiliencePipelineProvider>()
            .Get(ResiliencePipelineNames.Read).Should().BeOfType<ResiliencePipelineWrapper>();

        providerB.GetRequiredService<IChangeTokenFactory>()
            .Should().BeOfType<ChangeTokenFactory<RedisValue>>();
        providerB.GetRequiredService<IResiliencePipelineProvider>()
            .Get(ResiliencePipelineNames.Read).Should().BeOfType<ResiliencePipelineWrapper>();
    }

    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddCaching(builder =>
            builder
                .AddInMemoryRedis()
                .AddResilienceStrategies(_ => { }));
        return services.BuildServiceProvider();
    }
}
