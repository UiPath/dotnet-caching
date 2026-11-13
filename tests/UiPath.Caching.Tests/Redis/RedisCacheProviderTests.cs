using Microsoft.Extensions.Logging;
using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Redis;

public class RedisCacheProviderTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private RedisCacheOptions _options = default!;

    private RedisCacheProvider? _sut = null;
    private RedisCacheProvider Sut => _sut ??= _fixture.Create<RedisCacheProvider>();

    [Fact]
    public void Works_as_expected()
    {
        Sut.CreateCache().Should().BeOfType<RedisCache>();
        Sut.CreateHashCache().Should().BeOfType<RedisHashCache>();
        Sut.Name.Should().Be("Redis");
        Sut.Enabled.Should().Be(_options.Enabled);
    }


    [Fact]
    public void Dispose_can_be_called()
    {
        var x = Sut.CreateCache();
        var y = Sut.CreateHashCache();
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Provider_passes_the_registry_to_both_caches()
    {
        var registry = new ValueSerializerRegistry([("fast", new FakeValueSerializer(0x10), false)]);
        using var sut = new RedisCacheProvider(
            _fixture.Create<IOptions<RedisCacheOptions>>(),
            _fixture.Create<IOptions<CacheOptions>>(),
            _fixture.Create<IRedisConnector>(),
            _fixture.Create<ISerializerProxy<byte[]>>(),
            _fixture.Create<IResiliencePipelineProvider>(),
            _fixture.Create<ICachingTelemetryProvider>(),
            _fixture.Create<ILoggerFactory>(),
            _fixture.Create<ICachePolicyFactory>(),
            _fixture.Create<TimeProvider>(),
            keyMaskingPolicy: null,
            registry);

        sut.CreateCache().Should().BeOfType<RedisCache>().Which.ValueSerializers.Should().BeSameAs(registry);
        sut.CreateHashCache().Should().BeOfType<RedisHashCache>().Which.ValueSerializers.Should().BeSameAs(registry);
    }

    [Fact]
    public void The_public_constructor_passes_an_empty_registry()
    {
        Sut.CreateCache().Should().BeOfType<RedisCache>().Which.ValueSerializers.IsEmpty.Should().BeTrue();
        Sut.CreateHashCache().Should().BeOfType<RedisHashCache>().Which.ValueSerializers.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_custom_factory_naming_an_unknown_serializer_fails_at_first_use()
    {
        var factory = Substitute.For<ICachePolicyFactory>();
        factory.Keys.Returns(["orders"]);
        factory.Resolve("orders").Returns(new CachePolicy { Serializer = new SerializerPolicy { Write = "typo" } });
        _fixture.Inject(factory);

        Sut.Invoking(s => s.CreateCache()).Should().Throw<OptionsValidationException>().WithMessage("*orders*typo*");
        Sut.Invoking(s => s.CreateHashCache()).Should().Throw<OptionsValidationException>().WithMessage("*orders*typo*");
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _options = _fixture.Create<RedisCacheOptions>();
        _fixture.Inject(Options.Create(_options));
        return ValueTask.CompletedTask;
    }
}
