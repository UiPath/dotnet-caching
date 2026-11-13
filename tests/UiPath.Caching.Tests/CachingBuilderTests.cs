using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Policies;
using UiPath.Caching.Polly;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;

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
    public void A_leftover_RedisValue_serializer_registration_fails_the_build_even_when_caching_is_disabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ISerializerProxy<RedisValue>>());

        var act = () => new CachingBuilder(services) { Enabled = false }.Complete();

        act.Should().Throw<InvalidOperationException>("a disabled profile is usually the test copy of one that is on elsewhere")
            .WithMessage("*ISerializerProxy<RedisValue>*");
    }

    [Fact]
    public void A_RedisValue_serializer_registered_by_a_completion_callback_fails_the_build()
    {
        var builder = new CachingBuilder(new ServiceCollection());
        builder.RegisterOnCompleteCallback(typeof(CachingBuilderTests),
            b => b.Services.AddSingleton(Substitute.For<ISerializerProxy<RedisValue>>()));

        var act = () => builder.Complete();

        act.Should().Throw<InvalidOperationException>("callbacks run before the check, so what they register is seen")
            .WithMessage("*ISerializerProxy<RedisValue>*");
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

    [Fact]
    public void Registered_serializer_resolves_by_name()
    {
        var serializer = new FakeValueSerializer(0x10);
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);
        builder.AddValueSerializer("fast", _ => serializer);
        builder.Complete();

        var registry = services.BuildServiceProvider().GetRequiredService<ValueSerializerRegistry>();

        registry.Resolve("fast").Should().BeSameAs(serializer);
        registry.TryGetByFormat(0x10, out var byFormat).Should().BeTrue();
        byFormat.Should().BeSameAs(serializer);
    }

    [Fact]
    public void The_factory_receives_the_container()
    {
        var services = new ServiceCollection();
        var marker = new object();
        services.AddSingleton(marker);
        object? seen = null;
        var builder = new CachingBuilder(services);
        builder.AddValueSerializer("fast", sp =>
        {
            seen = sp.GetRequiredService<object>();
            return new FakeValueSerializer(0x10);
        });
        builder.Complete();

        services.BuildServiceProvider().GetRequiredService<ValueSerializerRegistry>();

        seen.Should().BeSameAs(marker);
    }

    [Fact]
    public void Registry_is_empty_without_registrations()
    {
        var services = new ServiceCollection();
        new CachingBuilder(services).Complete();

        services.BuildServiceProvider().GetRequiredService<ValueSerializerRegistry>().IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_consumer_cannot_register_a_library_id()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);
        builder.AddValueSerializer("fast", _ => new FakeValueSerializer(CacheValueFormats.MessagePack));
        builder.Complete();

        var act = () => services.BuildServiceProvider().GetRequiredService<ValueSerializerRegistry>();

        act.Should().Throw<OptionsValidationException>().WithMessage("*0x02*");
    }

    [Fact]
    public void A_library_serializer_may_use_a_library_id()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);
        builder.AddLibraryValueSerializer("messagepack", _ => new FakeValueSerializer(CacheValueFormats.MessagePack));
        builder.Complete();

        services.BuildServiceProvider().GetRequiredService<ValueSerializerRegistry>()
            .TryGetByFormat(CacheValueFormats.MessagePack, out _).Should().BeTrue();
    }

    [Fact]
    public void The_same_name_registered_twice_fails_at_registration()
    {
        var builder = new CachingBuilder(new ServiceCollection());
        builder.AddValueSerializer("fast", _ => new FakeValueSerializer(0x10));

        var act = () => builder.AddValueSerializer("FAST", _ => new FakeValueSerializer(0x11));

        act.Should().Throw<ArgumentException>().WithParameterName("name").WithMessage("*FAST*");
    }

    [Fact]
    public void The_json_name_is_refused_at_registration()
    {
        var builder = new CachingBuilder(new ServiceCollection());

        var act = () => builder.AddValueSerializer("JSON", _ => new FakeValueSerializer(0x10));

        act.Should().Throw<ArgumentException>().WithParameterName("name").WithMessage("*json*");
    }

    [Fact]
    public void A_library_and_a_consumer_serializer_cannot_share_a_name()
    {
        var builder = new CachingBuilder(new ServiceCollection());
        builder.AddLibraryValueSerializer("messagepack", _ => new FakeValueSerializer(CacheValueFormats.MessagePack));

        var act = () => builder.AddValueSerializer("messagepack", _ => new FakeValueSerializer(0x10));

        act.Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Fact]
    public void A_factory_that_returns_null_fails_naming_the_serializer()
    {
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);
        builder.AddValueSerializer("fast", _ => null!);
        builder.Complete();

        var act = () => services.BuildServiceProvider().GetRequiredService<ValueSerializerRegistry>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*fast*").WithMessage("*null*");
    }

    [Fact]
    public void The_container_disposes_the_serializers_it_created()
    {
        var serializer = new DisposableFakeValueSerializer(0x10);
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);
        builder.AddValueSerializer("fast", _ => serializer);
        builder.Complete();
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<ValueSerializerRegistry>();

        provider.Dispose();

        serializer.Disposed.Should().Be(1);
    }

    [Fact]
    public void A_serializer_that_fails_validation_is_still_disposed()
    {
        var serializer = new DisposableFakeValueSerializer(0x02);
        var services = new ServiceCollection();
        var builder = new CachingBuilder(services);
        builder.AddValueSerializer("fast", _ => serializer);
        builder.Complete();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ValueSerializerRegistry>();

        act.Should().Throw<OptionsValidationException>();
        serializer.Disposed.Should().Be(1);
    }

    [Fact]
    public void A_registration_needs_a_name_and_a_factory()
    {
        var builder = new CachingBuilder(new ServiceCollection());

        builder.Invoking(b => b.AddValueSerializer("", _ => new FakeValueSerializer(0x10))).Should().Throw<ArgumentException>();
        builder.Invoking(b => b.AddValueSerializer("fast", null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_redis_provider_built_from_the_container_carries_the_registry()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRedisConnector>());
        services.AddCaching(
            builder => builder.AddRedis(_ => { }).AddValueSerializer("fast", _ => new FakeValueSerializer(0x10)),
            options => options.AppShortName = "app");
        using var provider = services.BuildServiceProvider();

        var redis = provider.GetServices<ICacheProvider>().OfType<RedisCacheProvider>().Should().ContainSingle().Subject;

        var registry = provider.GetRequiredService<ValueSerializerRegistry>();
        registry.IsEmpty.Should().BeFalse();
        redis.CreateCache().Should().BeOfType<RedisCache>().Which.ValueSerializers.Should().BeSameAs(registry);
        redis.CreateHashCache().Should().BeOfType<RedisHashCache>().Which.ValueSerializers.Should().BeSameAs(registry);
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
