using System.Buffers;
using MessagePack;
using UiPath.Caching.MessagePack;

namespace UiPath.Caching.Tests.MessagePack;

public class MessagePackCompressionTests
{
    private const int Threshold = 1024;

    private readonly MessagePackValueSerializer _serializer = new(new MessagePackValueSerializerOptions { CompressionThresholdBytes = Threshold });

    private delegate void WriteAction(ref MessagePackWriter writer);

    [Fact]
    public void Below_threshold_writes_id_0x02()
    {
        var (id, bytes) = Write(new string('a', Threshold - 4));

        id.Should().Be(0x02);
        bytes.Length.Should().Be(Threshold - 1);
    }

    [Fact]
    public void At_threshold_writes_id_0x03_when_smaller()
    {
        // A str16 header is three bytes, so this value serializes to exactly the threshold.
        var (id, bytes) = Write(new string('a', Threshold - 3));

        id.Should().Be(0x03);
        bytes.Length.Should().BeLessThan(Threshold);
    }

    [Fact]
    public void Incompressible_value_stays_id_0x02()
    {
        var random = new byte[4 * Threshold];
        new Random(7).NextBytes(random);

        var (id, bytes) = Write(random);

        id.Should().Be(0x02);
        Read<byte[]>(id, bytes).Should().Equal(random);
    }

    [Fact]
    public void Compressed_value_round_trips()
    {
        var value = Enumerable.Range(0, 2000).Select(i => $"row-{i % 17}").ToList();

        var (id, bytes) = Write(value);

        id.Should().Be(0x03);
        Read<List<string>>(id, bytes).Should().Equal(value);
    }

    [Fact]
    public void A_negative_threshold_is_rejected()
    {
        var act = () => new MessagePackValueSerializer(new MessagePackValueSerializerOptions { CompressionThresholdBytes = -1 });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_truncated_compressed_value_throws()
    {
        var (id, bytes) = Write(Enumerable.Range(0, 2000).Select(i => $"row-{i % 17}").ToList());

        var act = () => Read<List<string>>(id, bytes[..(bytes.Length / 2)]);

        act.Should().Throw<MessagePackSerializationException>();
    }

    [Fact]
    public void A_compressed_value_that_claims_int_MaxValue_is_rejected_by_the_ratio()
    {
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions { MaxDecompressedBytes = int.MaxValue });

        var act = () => serializer.Deserialize<string>(0x03, new ReadOnlySequence<byte>(Hostile(int.MaxValue, 3)));

        act.Should().Throw<InvalidDataException>().WithMessage("*claims*");
    }

    [Fact]
    public void A_claim_just_over_255_times_the_compressed_size_is_rejected()
    {
        var size = Hostile(1_000_000, 1000).Length;

        var act = () => Read<string>(0x03, Hostile((size * 255L) + 1, 1000));

        act.Should().Throw<InvalidDataException>().WithMessage("*claims*");
    }

    [Fact]
    public void A_claim_of_exactly_255_times_the_compressed_size_passes_the_guard()
    {
        var size = Hostile(1_000_000, 1000).Length;

        var act = () => Read<string>(0x03, Hostile(size * 255L, 1000));

        act.Should().Throw<MessagePackSerializationException>();
    }

    [Fact]
    public void A_zero_filled_value_with_a_ratio_over_100x_round_trips()
    {
        var zeros = new byte[16 * 1024 * 1024];
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions());
        var target = new ArrayBufferWriter<byte>();

        var id = serializer.Serialize(zeros, target);

        id.Should().Be(0x03);
        (zeros.Length / target.WrittenCount).Should().BeGreaterThan(100);
        serializer.Deserialize<byte[]>(id, new ReadOnlySequence<byte>(target.WrittenMemory)).Should().Equal(zeros);
    }

    [Fact]
    public void A_claim_above_MaxDecompressedBytes_is_rejected_though_the_ratio_is_fine()
    {
        var target = new ArrayBufferWriter<byte>();
        var id = _serializer.Serialize(new string('a', 10_000), target);
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions { MaxDecompressedBytes = 1000 });

        var act = () => serializer.Deserialize<string>(id, new ReadOnlySequence<byte>(target.WrittenMemory));

        id.Should().Be(0x03);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_value_above_MaxDecompressedBytes_is_written_plain_so_it_can_be_read_back()
    {
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions { MaxDecompressedBytes = 4096 });
        var value = new string('a', 10_000);
        var target = new ArrayBufferWriter<byte>();

        var id = serializer.Serialize(value, target);

        id.Should().Be(0x02);
        serializer.Deserialize<string>(id, new ReadOnlySequence<byte>(target.WrittenMemory)).Should().Be(value);
    }

    [Fact]
    public void A_compressible_value_above_the_64_MiB_default_round_trips_when_the_limit_is_raised()
    {
        var zeros = new byte[70 * 1024 * 1024];
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions { MaxDecompressedBytes = 128 * 1024 * 1024 });
        var target = new ArrayBufferWriter<byte>();

        var id = serializer.Serialize(zeros, target);

        id.Should().Be(0x03);
        serializer.Deserialize<byte[]>(id, new ReadOnlySequence<byte>(target.WrittenMemory)).Should().Equal(zeros);
    }

    [Fact]
    public void MaxDecompressedBytes_defaults_to_64_MiB_and_must_be_positive()
    {
        new MessagePackValueSerializerOptions().MaxDecompressedBytes.Should().Be(64 * 1024 * 1024);
        var act = () => new MessagePackValueSerializer(new MessagePackValueSerializerOptions { MaxDecompressedBytes = 0 });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_threshold_of_zero_or_one_tries_every_value(int threshold)
    {
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions { CompressionThresholdBytes = threshold });
        var target = new ArrayBufferWriter<byte>();

        var id = serializer.Serialize(new string('a', 100), target);

        id.Should().Be(0x03);
        serializer.Deserialize<string>(id, new ReadOnlySequence<byte>(target.WrittenMemory)).Should().Be(new string('a', 100));
    }

    [Fact]
    public void A_threshold_of_zero_keeps_a_value_that_does_not_shrink_as_plain()
    {
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions { CompressionThresholdBytes = 0 });
        var target = new ArrayBufferWriter<byte>();

        var id = serializer.Serialize(7, target);

        id.Should().Be(0x02);
        target.WrittenCount.Should().Be(1);
    }

    [Fact]
    public void A_threshold_of_int_MaxValue_never_compresses_yet_reads_both_formats()
    {
        var never = new MessagePackValueSerializer(new MessagePackValueSerializerOptions { CompressionThresholdBytes = int.MaxValue });
        var value = new string('a', 10 * Threshold);
        var (compressedId, compressed) = Write(value);
        var target = new ArrayBufferWriter<byte>();

        var id = never.Serialize(value, target);

        compressedId.Should().Be(0x03);
        id.Should().Be(0x02);
        never.Deserialize<string>(compressedId, new ReadOnlySequence<byte>(compressed)).Should().Be(value);
    }

    [Fact]
    public void A_large_compressible_value_is_0x03_and_a_large_incompressible_one_is_0x02()
    {
        var random = new byte[10 * Threshold];
        new Random(3).NextBytes(random);

        Write(new string('a', 10 * Threshold)).Id.Should().Be(0x03);
        Write(random).Id.Should().Be(0x02);
    }

    [Fact]
    public void The_compressed_bytes_are_what_MessagePack_reads_as_an_Lz4BlockArray()
    {
        var value = new string('a', 10 * Threshold);
        var (id, bytes) = Write(value);

        var read = MessagePackSerializer.Deserialize<string>(bytes, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray), TestContext.Current.CancellationToken);

        id.Should().Be(0x03);
        read.Should().Be(value);
    }

    [Fact]
    public void A_compressed_value_reads_from_a_sequence_of_small_segments()
    {
        var value = Enumerable.Range(0, 2000).Select(i => $"row-{i % 17}").ToList();
        var (id, bytes) = Write(value);

        var read = _serializer.Deserialize<List<string>>(id, Segmented(bytes, 7));

        id.Should().Be(0x03);
        read.Should().Equal(value);
    }

    [Fact]
    public void A_claim_above_the_default_MaxDecompressedBytes_is_rejected_though_the_ratio_is_fine()
    {
        var claim = (64L * 1024 * 1024) + 1;
        var serializer = new MessagePackValueSerializer(new MessagePackValueSerializerOptions());

        var act = () => serializer.Deserialize<string>(0x03, new ReadOnlySequence<byte>(Hostile(claim, (int)(claim / 200))));

        act.Should().Throw<InvalidDataException>().WithMessage("*claims*");
    }

    [Fact]
    public void A_128_KB_compressible_value_compresses_as_one_LZ4_block()
    {
        var value = string.Concat(Enumerable.Range(0, 16_000).Select(i => $"row-{i % 17};"));
        var (id, bytes) = Write(value);

        var reader = new MessagePackReader(bytes);
        id.Should().Be(0x03);
        reader.ReadArrayHeader().Should().Be(2);
        reader.ReadExtensionFormatHeader().TypeCode.Should().Be(98);
    }

    [Fact]
    public void A_compressed_value_with_fewer_than_one_block_is_rejected()
    {
        var bytes = Build((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(1);
            w.WriteExtensionFormatHeader(new ExtensionHeader(98, 0));
            w.Write(new byte[3]);
        });

        var act = () => Read<string>(0x03, bytes);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_compressed_value_whose_first_element_is_not_an_extension_is_rejected()
    {
        var bytes = Build((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(2);
            w.Write(5);
            w.Write(new byte[3]);
        });

        var act = () => Read<string>(0x03, bytes);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_compressed_value_with_another_extension_type_code_is_rejected()
    {
        var bytes = Build((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(2);
            w.WriteExtensionFormatHeader(new ExtensionHeader(99, 1));
            w.WriteRaw([0x05]);
            w.Write(new byte[3]);
        });

        var act = () => Read<string>(0x03, bytes);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_compressed_value_with_a_negative_block_length_is_rejected()
    {
        var bytes = Build((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(2);
            w.WriteExtensionFormatHeader(new ExtensionHeader(98, 1));
            w.WriteRaw([0xFF]);
            w.Write(new byte[3]);
        });

        var act = () => Read<string>(0x03, bytes);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_compressed_value_whose_block_lengths_overflow_when_summed_is_rejected()
    {
        var lengths = Build((ref MessagePackWriter w) =>
        {
            w.Write(long.MaxValue);
            w.Write(long.MaxValue);
        });
        var bytes = Build((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.WriteExtensionFormatHeader(new ExtensionHeader(98, (uint)lengths.Length));
            w.WriteRaw(lengths);
            w.Write(new byte[3]);
            w.Write(new byte[3]);
        });

        var act = () => Read<string>(0x03, bytes);

        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void A_compressed_value_whose_extension_does_not_match_its_block_count_is_rejected(int lengthCount)
    {
        var lengths = Build((ref MessagePackWriter w) =>
        {
            for (var i = 0; i < lengthCount; i++)
            {
                w.Write(100);
            }
        });
        var bytes = Build((ref MessagePackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.WriteExtensionFormatHeader(new ExtensionHeader(98, (uint)lengths.Length));
            w.WriteRaw(lengths);
            w.Write(new byte[3]);
            w.Write(new byte[3]);
        });

        var act = () => Read<string>(0x03, bytes);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_compressed_value_whose_first_block_is_not_binary_is_rejected()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(2);
        writer.WriteExtensionFormatHeader(new ExtensionHeader(98, 1));
        writer.WriteRaw([0x05]);
        writer.Write("not a block");
        writer.Flush();

        var act = () => Read<string>(0x03, buffer.WrittenMemory.ToArray());

        act.Should().Throw<InvalidDataException>();
    }

    private static byte[] Build(WriteAction write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        write(ref writer);
        writer.Flush();
        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] Hostile(long claim, int blockLength)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        var lengths = new ArrayBufferWriter<byte>();
        var lengthWriter = new MessagePackWriter(lengths);
        lengthWriter.Write(claim);
        lengthWriter.Flush();
        writer.WriteArrayHeader(2);
        writer.WriteExtensionFormatHeader(new ExtensionHeader(98, (uint)lengths.WrittenCount));
        writer.WriteRaw(lengths.WrittenSpan);
        writer.Write(new byte[blockLength]);
        writer.Flush();
        return buffer.WrittenMemory.ToArray();
    }

    private static ReadOnlySequence<byte> Segmented(byte[] bytes, int size)
    {
        Segment? first = null;
        Segment? last = null;
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            var next = new Segment(bytes.AsMemory(offset, Math.Min(size, bytes.Length - offset)));
            if (last is null)
            {
                first = next;
            }
            else
            {
                last.Append(next);
            }

            last = next;
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private (int Id, byte[] Bytes) Write<T>(T value)
    {
        var target = new ArrayBufferWriter<byte>();
        var id = _serializer.Serialize(value, target);
        return (id, target.WrittenMemory.ToArray());
    }

    private T? Read<T>(int id, byte[] bytes) => _serializer.Deserialize<T>(id, new ReadOnlySequence<byte>(bytes));

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public void Append(Segment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}
