using System.Buffers;
using System.Text.Json;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using UiPath.Caching.MessagePack;

namespace UiPath.Caching.Tests.MessagePack;

public class MessagePackValueSerializerTests
{
    private readonly MessagePackValueSerializer _serializer = new(new MessagePackValueSerializerOptions());

    [Fact]
    public void Round_trips_a_contractless_type()
    {
        var value = Plain.Sample();

        var (id, bytes) = Write(value);

        id.Should().Be(CacheValueFormats.MessagePack);
        Read<Plain>(id, bytes).Should().BeEquivalentTo(value);
    }

    [Fact]
    public void Round_trips_an_int_key_type()
    {
        var value = new Keyed { Name = "n", Count = 7 };

        var (id, bytes) = Write(value);

        Read<Keyed>(id, bytes).Should().BeEquivalentTo(value);
    }

    [Fact]
    public void Null_and_empty_collections_round_trip_with_non_empty_payload()
    {
        var (nullId, nullBytes) = Write<Plain?>(null);
        var (listId, listBytes) = Write(new List<string>());
        var (mapId, mapBytes) = Write(new Dictionary<string, int>());

        nullBytes.Should().NotBeEmpty();
        listBytes.Should().NotBeEmpty();
        mapBytes.Should().NotBeEmpty();
        Read<Plain>(nullId, nullBytes).Should().BeNull();
        Read<List<string>>(listId, listBytes).Should().BeEmpty();
        Read<Dictionary<string, int>>(mapId, mapBytes).Should().BeEmpty();
    }

    [Fact]
    public void Byte_array_round_trips()
    {
        byte[] value = [0x02, 0, 1, 255];

        var (id, bytes) = Write(value);

        Read<byte[]>(id, bytes).Should().Equal(value);
    }

    [Fact]
    public void Value_is_smaller_than_json()
    {
        var value = Plain.Sample();

        var (_, bytes) = Write(value);

        bytes.Length.Should().BeLessThan((int)(JsonSerializer.SerializeToUtf8Bytes(value).Length * 0.7));
    }

    [Fact]
    public void Deeply_nested_data_is_rejected()
    {
        var nested = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(nested);
        for (var i = 0; i < 1000; i++)
        {
            writer.WriteArrayHeader(1);
        }

        writer.Write(1);
        writer.Flush();

        var act = () => Read<object>(CacheValueFormats.MessagePack, nested.WrittenMemory.ToArray());

        act.Should().Throw<MessagePackSerializationException>();
    }

    [Fact]
    public void Default_options_treat_the_data_as_untrusted()
    {
        _serializer.Options.Security.Should().BeSameAs(ResilientMessagePackSecurity.Instance);
        _serializer.Options.Security.HashCollisionResistant.Should().BeTrue();
    }

    [Fact]
    public void Supplied_options_that_carry_trusted_data_are_made_untrusted()
    {
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions { SerializerOptions = MessagePackSerializerOptions.Standard });

        serializer.Options.Security.Should().BeSameAs(ResilientMessagePackSecurity.Instance);
    }

    [Fact]
    public void A_custom_security_instance_is_kept()
    {
        var custom = MessagePackSecurity.TrustedData.WithMaximumObjectGraphDepth(10);
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions
        {
            SerializerOptions = MessagePackSerializerOptions.Standard.WithSecurity(custom),
        });

        serializer.Options.Security.Should().BeSameAs(custom);
        serializer.Options.Security.MaximumObjectGraphDepth.Should().Be(10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collections_keyed_by_types_without_a_resistant_comparer_round_trip(bool trustedOptions)
    {
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions
        {
            SerializerOptions = trustedOptions ? MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance) : null,
        });
        var spans = new Dictionary<TimeSpan, int> { [TimeSpan.FromSeconds(3)] = 1 };
        var decimals = new Dictionary<decimal, int> { [1.5m] = 2 };
        var tuples = new HashSet<(int, int)> { (1, 2) };

        Round(serializer, spans).Should().Equal(spans);
        Round(serializer, decimals).Should().Equal(decimals);
        Round(serializer, tuples).Should().BeEquivalentTo(tuples);
    }

    [Fact]
    public void String_keys_still_use_the_resistant_comparer()
    {
        _serializer.Options.Security.GetEqualityComparer<string>().Should().NotBeSameAs(EqualityComparer<string>.Default);
        _serializer.Options.Security.HashCollisionResistant.Should().BeTrue();
    }

    [Fact]
    public void Compression_set_on_the_supplied_options_is_overridden()
    {
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions
        {
            CompressionThresholdBytes = int.MaxValue,
            SerializerOptions = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray).WithCompressionMinLength(1),
        });
        var target = new ArrayBufferWriter<byte>();

        var id = serializer.Serialize(new string('a', 10_000), target);

        id.Should().Be(CacheValueFormats.MessagePack);
        serializer.Options.Compression.Should().Be(MessagePackCompression.None);
        target.WrittenCount.Should().BeGreaterThan(10_000);
    }

    [Fact]
    public void Wrong_id_payloads_are_rejected()
    {
        var big = new string('a', 10_000);
        var compressed = new ArrayBufferWriter<byte>();
        var compressedId = _serializer.Serialize(big, compressed);
        var (plainId, plainBytes) = Write("small");

        compressedId.Should().Be(CacheValueFormats.MessagePackLz4);
        plainId.Should().Be(CacheValueFormats.MessagePack);
        var asPlain = () => Read<string>(CacheValueFormats.MessagePack, compressed.WrittenMemory.ToArray());
        var asCompressed = () => Read<string>(CacheValueFormats.MessagePackLz4, plainBytes);
        asPlain.Should().Throw<MessagePackSerializationException>();
        asCompressed.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_plain_value_that_starts_with_the_compression_extension_is_stored_as_plain()
    {
        var options = MessagePackSerializerOptions.Standard.WithResolver(CompositeResolver.Create([new LookalikeFormatter()], [StandardResolver.Instance]));
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions { SerializerOptions = options });
        var target = new ArrayBufferWriter<byte>();

        var id = serializer.Serialize(new Lookalike(), target);

        id.Should().Be(CacheValueFormats.MessagePack);
        target.WrittenSpan[..2].ToArray().Should().Equal(0x92, 0xD4);
        serializer.Deserialize<Lookalike>(id, new ReadOnlySequence<byte>(target.WrittenMemory.ToArray())).Should().NotBeNull();
    }

    [Fact]
    public void Large_value_round_trips()
    {
        var value = new Plain { Name = new string('x', 200 * 1024), Numbers = [1, 2, 3] };

        var (id, bytes) = Write(value);

        Read<Plain>(id, bytes).Should().BeEquivalentTo(value);
    }

    [Fact]
    public void An_id_it_does_not_own_throws()
    {
        var (_, bytes) = Write(Plain.Sample());

        var act = () => Read<Plain>(0x10, bytes);

        act.Should().Throw<InvalidOperationException>();
    }

    private static T? Round<T>(MessagePackValueSerializer serializer, T value)
    {
        var target = new ArrayBufferWriter<byte>();
        var id = serializer.Serialize(value, target);
        return serializer.Deserialize<T>(id, new ReadOnlySequence<byte>(target.WrittenMemory));
    }

    private (int Id, byte[] Bytes) Write<T>(T value)
    {
        var target = new ArrayBufferWriter<byte>();
        var id = _serializer.Serialize(value, target);
        return (id, target.WrittenMemory.ToArray());
    }

    private T? Read<T>(int id, byte[] bytes) => _serializer.Deserialize<T>(id, new ReadOnlySequence<byte>(bytes));

    public sealed class Plain
    {
        public string? Name { get; set; }

        public int[]? Numbers { get; set; }

        public double[]? Measures { get; set; }

        public DateTime When { get; set; }

        public Guid Id { get; set; }

        public static Plain Sample() => new()
        {
            Name = "a medium sized object",
            Numbers = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
            Measures = [.. Enumerable.Range(0, 10).Select(i => Math.PI * (i + 1) / 7)],
            When = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Id = Guid.NewGuid(),
        };
    }

    public sealed class Lookalike
    {
    }

    public sealed class LookalikeFormatter : IMessagePackFormatter<Lookalike?>
    {
        public void Serialize(ref MessagePackWriter writer, Lookalike? value, MessagePackSerializerOptions options)
        {
            writer.WriteArrayHeader(2);
            writer.WriteExtensionFormatHeader(new ExtensionHeader(98, 1));
            writer.WriteRaw([0x05]);
            writer.Write("not a block");
        }

        public Lookalike? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            reader.Skip();
            return new Lookalike();
        }
    }

    [MessagePackObject]
    public sealed class Keyed
    {
        [Key(0)]
        public string? Name { get; set; }

        [Key(1)]
        public int Count { get; set; }
    }
}
