using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Serialization;

[Collection(WriterPoolCollection.Name)]
public class ValueSerializerPayloadTests
{
    [Fact]
    public void Payload_starts_with_the_header()
    {
        using var payload = Write(new Fake([0x10], 0x10, "abc"u8.ToArray()), "ignored");

        payload.Memory.ToArray().Should().Equal(0x10, (byte)'a', (byte)'b', (byte)'c');
    }

    [Fact]
    public void Header_is_the_id_the_serializer_returned()
    {
        using var payload = Write(new Fake([0x10, 0x11], 0x11, "abc"u8.ToArray()), 1);

        payload.Memory.Span[0].Should().Be(0x11);
        payload.Memory.Length.Should().Be(4);
    }

    [Fact]
    public void Extended_payload_starts_with_two_header_bytes()
    {
        using var payload = Write(new Fake([0x1A5], 0x1A5, "abc"u8.ToArray()), 1);

        payload.Memory.ToArray().Should().Equal(0x1F, 0xA5, (byte)'a', (byte)'b', (byte)'c');
        ValueHeader.TryRead(payload.Memory.Span, out var id, out var length).Should().BeTrue();
        id.Should().Be(0x1A5);
        length.Should().Be(2);
    }

    [Fact]
    public void Zero_byte_serializer_output_throws()
    {
        var act = () => Write(new Fake([0x10], 0x10, []), 1);

        act.Should().Throw<InvalidOperationException>().WithMessage("*must not write zero bytes*");
    }

    [Fact]
    public void An_id_the_serializer_does_not_own_throws()
    {
        var act = () => Write(new Fake([0x10], 0x11, "abc"u8.ToArray()), 1);

        act.Should().Throw<InvalidOperationException>().WithMessage("*0x11*");
    }

    [Fact]
    public void A_serializer_without_ids_throws()
    {
        var act = () => Write(new Fake([], 0x10, "abc"u8.ToArray()), 1);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Large_payload_grows_the_rented_buffer()
    {
        var body = new byte[200 * 1024];
        new Random(7).NextBytes(body);

        using var payload = Write(new Fake([0x10], 0x10, body), 1);

        payload.Memory.Length.Should().Be(body.Length + 1);
        payload.Memory.Span[0].Should().Be(0x10);
        payload.Memory.Span[1..].SequenceEqual(body).Should().BeTrue();
    }

    [Fact]
    public void Growing_keeps_the_header_when_the_serializer_writes_in_pieces()
    {
        var serializer = new Fake([0x1A5], 0x1A5, Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray(), chunk: 7);

        using var payload = Write(serializer, 1);

        payload.Memory.Span[..2].ToArray().Should().Equal(0x1F, 0xA5);
        payload.Memory.Span[2..].ToArray().Should().Equal(serializer.Body);
    }

    [Fact]
    public void A_large_payload_returns_its_rented_array_on_dispose()
    {
        using var pool = new RecordingPool();
        var payload = Write(new Fake([0x10], 0x10, new byte[100 * 1024]), 1);
        MemoryMarshal.TryGetArray(payload.Memory, out var segment).Should().BeTrue();

        pool.Returned.Should().NotContain(segment.Array!);
        payload.Dispose();

        pool.Returned.Should().Contain(segment.Array!);
    }

    [Fact]
    public void A_failed_serialize_leaves_nothing_in_the_next_payload()
    {
        var failing = new Fake([0x10], 0x10, new byte[1000], failAfterChunks: 3, chunk: 100);
        var act = () => Write(failing, 1);
        act.Should().Throw<IOException>();

        using var payload = Write(new Fake([0x10], 0x10, "abc"u8.ToArray()), 1);

        payload.Memory.ToArray().Should().Equal(0x10, (byte)'a', (byte)'b', (byte)'c');
    }

    [Fact]
    public void An_id_added_to_the_live_list_after_registration_is_not_accepted()
    {
        var live = new List<int> { 0x10 };
        var serializer = new Fake(live, 0x11, "abc"u8.ToArray());
        int[] snapshot = [.. live];
        live.Add(0x11);

        var act = () => SerializedPayload.Serialize(serializer, snapshot, 1);

        act.Should().Throw<InvalidOperationException>().WithMessage("*0x11*");
    }

    [Fact]
    public void An_id_with_another_header_length_than_the_reserved_one_throws()
    {
        var serializer = new Fake([0x10, 0x1A5], 0x1A5, "abc"u8.ToArray());

        var act = () => Write(serializer, 1);

        act.Should().Throw<InvalidOperationException>().WithMessage("*0x1A5*");
    }

    [Fact]
    public void A_failed_serialize_returns_the_buffer_it_rented()
    {
        using var pool = new RecordingPool();
        var failing = new Fake([0x10], 0x10, new byte[300 * 1024], failAfterChunks: 2, chunk: 100 * 1024);

        var act = () => Write(failing, 1);

        act.Should().Throw<IOException>();
        pool.RentedByThisThread.Should().NotBeEmpty();
        pool.Returned.Should().Contain(pool.RentedByThisThread);
    }

    [Fact]
    public void The_cached_writer_is_reused_after_a_successful_call()
    {
        using var pool = new RecordingPool();
        using var warm = Write(new Fake([0x10], 0x10, "abc"u8.ToArray()), 1);
        var rented = pool.RentedByThisThread.Count;

        using var again = Write(new Fake([0x10], 0x10, "def"u8.ToArray()), 1);

        pool.RentedByThisThread.Count.Should().Be(rented);
    }

    [Fact]
    public void A_nested_serialize_keeps_both_payloads()
    {
        var inner = new Fake([0x11], 0x11, "inner"u8.ToArray());
        ReadOnlyMemory<byte> innerBytes = default;
        var outer = new Fake([0x10], 0x10, "outer"u8.ToArray(), onSerialize: () =>
        {
            using var nested = Write(inner, 1);
            innerBytes = nested.Memory.ToArray();
        });

        using var payload = Write(outer, 1);

        Encoding.UTF8.GetString(payload.Memory.Span[1..]).Should().Be("outer");
        Encoding.UTF8.GetString(innerBytes.Span[1..]).Should().Be("inner");
    }

    [Fact]
    public void The_json_overload_is_unchanged()
    {
        using var payload = SerializedPayload.Serialize(new SystemJsonByteSerializerProxy(), "s");

        Encoding.UTF8.GetString(payload.Memory.Span).Should().Be("\"s\"");
    }

    private static SerializedPayload Write<T>(Fake serializer, T value) =>
        SerializedPayload.Serialize(serializer, [.. serializer.FormatIds], value);

    private sealed class Fake(IReadOnlyList<int> ids, int returns, byte[] body, int chunk = 0, int failAfterChunks = -1, Action? onSerialize = null) : ICacheValueSerializer
    {
        public byte[] Body { get; } = body;

        public IReadOnlyList<int> FormatIds { get; } = ids;

        public int Serialize<T>(T value, IBufferWriter<byte> target)
        {
            onSerialize?.Invoke();
            var step = chunk == 0 ? Body.Length : chunk;
            var chunks = 0;
            for (var offset = 0; offset < Body.Length; offset += step)
            {
                if (chunks++ == failAfterChunks)
                {
                    throw new IOException("boom");
                }

                var count = Math.Min(step, Body.Length - offset);
                Body.AsSpan(offset, count).CopyTo(target.GetSpan(count));
                target.Advance(count);
            }

            return returns;
        }

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) => throw new NotSupportedException();
    }
}
