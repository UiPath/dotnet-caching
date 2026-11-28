using System.Buffers;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.MessagePack;

[Collection(WriterPoolCollection.Name)]
public class MessagePackOutOfMemoryTests(ITestContextAccessor testContextAccessor)
{
    private static readonly ManualTimeProvider Clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly byte[] EmptyArray = [0x02, 0x90];

    private readonly MessagePackValueSerializer _serializer = new(new MessagePackValueSerializerOptions
    {
        SerializerOptions = MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create([new ExhaustedFormatter()], [ContractlessStandardResolver.Instance])),
    });

    private CancellationToken Token => testContextAccessor.Current.CancellationToken;

    [Fact]
    public void Deserializing_rethrows_an_out_of_memory_failure_instead_of_a_serialization_error()
    {
        var act = () => _serializer.Deserialize<Exhausted>(0x02, new ReadOnlySequence<byte>(EmptyArray));

        act.Should().Throw<OutOfMemoryException>();
    }

    [Fact]
    public void Serializing_rethrows_an_out_of_memory_failure_instead_of_a_serialization_error()
    {
        var act = () => _serializer.Serialize(new Exhausted(), new ArrayBufferWriter<byte>());

        act.Should().Throw<OutOfMemoryException>();
    }

    [Fact]
    public void Serializing_without_compression_rethrows_an_out_of_memory_failure()
    {
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions
        {
            CompressionThresholdBytes = int.MaxValue,
            SerializerOptions = _serializer.Options,
        });

        var act = () => serializer.Serialize(new Exhausted(), new ArrayBufferWriter<byte>());

        act.Should().Throw<OutOfMemoryException>();
    }

    [Fact]
    public void A_corrupt_value_is_still_a_serialization_error()
    {
        var act = () => _serializer.Deserialize<string>(0x02, new ReadOnlySequence<byte>([0x02, 0xC1]));

        act.Should().Throw<MessagePackSerializationException>();
    }

    [Fact]
    public async Task Get_and_GetOrAdd_propagate_the_failure_and_do_not_run_the_generator()
    {
        var database = Substitute.For<IDatabase>();
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)EmptyArray);
        var sut = Create(database);
        var generated = 0;
        Task<Exhausted?> Generate(CancellationToken token)
        {
            generated++;
            return Task.FromResult<Exhausted?>(new Exhausted());
        }

        await FluentActions.Awaiting(async () => await sut.GetAsync<Exhausted>("k", policy: null, Token)).Should().ThrowAsync<OutOfMemoryException>();
        await FluentActions.Awaiting(() => sut.GetOrAddAsync<Exhausted>("k", Generate, TimeSpan.FromMinutes(1), policy: null, Token).AsTask()).Should().ThrowAsync<OutOfMemoryException>();

        generated.Should().Be(0);
    }

    private RedisCache Create(IDatabase database)
    {
        var connector = Substitute.For<IRedisConnector>();
        connector.Database.Returns(database);
        connector.Version.Returns(new Version(6, 0));
        connector.IsConnected.Returns(true);
        database.Multiplexer.GetHashSlot(Arg.Any<RedisKey>()).Returns(0);
        var pipelines = Substitute.For<IResiliencePipelineProvider>();
        pipelines.Get(Arg.Any<string>()).Returns(EmptyResiliencePipeline.Instance);
        var strategy = Substitute.For<IRedisKeyStrategy>();
        strategy.GetRedisKey(Arg.Any<CacheKey>()).Returns(call => (RedisKey)$"test:str:{call.Arg<CacheKey>()}");
        var strategies = Substitute.For<IRedisKeyStrategyFactory>();
        strategies.Create(Arg.Any<CacheOptions>(), Arg.Any<Type>()).Returns(strategy);
        var registry = new ValueSerializerRegistry([RegistryEntry.Of("messagepack", _serializer)]);

        return new RedisCache(
            connector,
            new SystemJsonByteSerializerProxy(),
            pipelines,
            NullTelemetryProvider.Instance,
            new RedisCacheOptions { RedisKeyStrategyFactory = strategies, ReadScriptsEnabled = false },
            new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true },
            new DefaultCachePolicyFactory(null, null, valueSerializers: registry),
            Clock,
            NullLogger<RedisCache>.Instance)
        {
            ValueSerializers = registry,
        };
    }

#pragma warning disable S2094 // a marker type for the generic argument
    public sealed class Exhausted
    {
    }
#pragma warning restore S2094

    public sealed class ExhaustedFormatter : IMessagePackFormatter<Exhausted?>
    {
        public void Serialize(ref MessagePackWriter writer, Exhausted? value, MessagePackSerializerOptions options) => throw new OutOfMemoryException();

        public Exhausted? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) => throw new OutOfMemoryException();
    }
}
