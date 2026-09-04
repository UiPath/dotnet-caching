using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Policies;
using UiPath.Caching.Polly;

namespace UiPath.Caching.Tests;

public class CachingBuilderTests
{
    /// <summary>Nothing resolves that service now, so it would be ignored rather than fail.</summary>
    [Fact]
    public void A_leftover_RedisValue_serializer_registration_fails_the_build()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ISerializerProxy<RedisValue>>());

        var act = () => new CachingBuilder(services).Complete();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ISerializerProxy<RedisValue>*")
            .WithMessage("*ISerializerProxy<byte[]>*");
    }

    [Fact]
    public void A_byte_serializer_registration_is_honored_over_the_default()
    {
        var services = new ServiceCollection();
        var custom = Substitute.For<ISerializerProxy<byte[]>>();
        services.AddSingleton(custom);

        new CachingBuilder(services).Complete();

        services.BuildServiceProvider().GetRequiredService<ISerializerProxy<byte[]>>().Should().BeSameAs(custom);
    }

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
            .Should().BeOfType<ChangeTokenFactory<byte[]>>();
        providerA.GetRequiredService<IResiliencePipelineProvider>()
            .Get(ResiliencePipelineNames.Read).Should().BeOfType<ResiliencePipelineWrapper>();

        providerB.GetRequiredService<IChangeTokenFactory>()
            .Should().BeOfType<ChangeTokenFactory<byte[]>>();
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
