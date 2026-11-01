using System.Runtime.CompilerServices;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>A peer-refreshed entry keeps its payload serialized until a read asks for the value.</summary>
public class DeferredEntryTests
{
    private static readonly DateTimeOffset Expiration = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Expiration_and_read_marks_do_not_deserialize()
    {
        var serializer = new CountingSerializer();
        var entry = Entry<List<string>>(serializer, ["a", "b"]);

        entry.Expiration.Should().Be(Expiration);
        ((IReadTracked)entry).MarkRead(5);
        ((IReadTracked)entry).LastRead.Should().Be(5);

        serializer.Deserialized.Should().Be(0);
        entry.Materialized.Should().BeFalse();
    }

    [Fact]
    public void The_first_read_deserializes_once_and_later_reads_get_the_same_instance()
    {
        var serializer = new CountingSerializer();
        var entry = Entry<List<string>>(serializer, ["a", "b"]);

        ((ICacheEntry)entry).Found.Should().BeTrue();
        var first = entry.Value;
        var second = ((ICacheEntry)entry).Value;

        first.Should().Equal("a", "b");
        second.Should().BeSameAs(first);
        serializer.Deserialized.Should().Be(1);
        entry.Materialized.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_first_reads_deserialize_once()
    {
        var serializer = new CountingSerializer { Delay = TimeSpan.FromMilliseconds(50) };
        var entry = Entry<List<string>>(serializer, ["a"]);
        using var start = new Barrier(16);

        var token = TestContext.Current.CancellationToken;
        List<string>? Read()
        {
            start.SignalAndWait(token);
            return entry.Value;
        }

        var reads = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(Read, token)));

        serializer.Deserialized.Should().Be(1);
        reads.Should().OnlyContain(r => ReferenceEquals(r, reads[0]));
        reads[0].Should().Equal("a");
    }

    [Fact]
    public void A_renewed_entry_shares_the_payload_and_its_one_deserialization()
    {
        var serializer = new CountingSerializer();
        var entry = Entry<List<string>>(serializer, ["a"]);
        var later = Expiration.AddHours(1);
        var metadata = new Dictionary<string, string?> { ["m"] = "1" };

        var renewed = (ICacheEntry<List<string>?>)entry.NewEntry(later, metadata);

        serializer.Deserialized.Should().Be(0);
        renewed.Expiration.Should().Be(later);
        renewed.Metadata.Should().BeSameAs(metadata);
        renewed.Value.Should().BeSameAs(entry.Value);
        serializer.Deserialized.Should().Be(1);
    }

    [Fact]
    public void A_payload_that_fails_to_deserialize_is_not_found_and_is_reported()
    {
        var failures = new List<Exception>();
        var entry = new DeferredEntry<List<string>>("not json"u8.ToArray(), new SystemJsonByteSerializerProxy(), keepsDefault: false, failures.Add, Expiration);

        ((ICacheEntry)entry).Found.Should().BeFalse();
        entry.Value.Should().BeNull();
        ((ICacheEntry)entry).Found.Should().BeFalse();

        failures.Should().ContainSingle();
        entry.Expiration.Should().Be(Expiration, "a TTL query reads the entry's expiration as the inner tier reported it");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void A_payload_that_reads_as_null_is_found_only_when_nulls_are_cached(bool keepsDefault, bool found)
    {
        var entry = new DeferredEntry<List<string>>("null"u8.ToArray(), new SystemJsonByteSerializerProxy(), keepsDefault, failed: null, Expiration);

        ((ICacheEntry)entry).Found.Should().Be(found);
        entry.Value.Should().BeNull();
    }

    [Fact]
    public void The_payload_is_let_go_once_deserialized()
    {
        var (entry, payload) = EntryWithWeakPayload();

        _ = entry.Value;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        payload.TryGetTarget(out _).Should().BeFalse();
        entry.Value.Should().Equal("a");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DeferredEntry<List<string>> Entry, WeakReference<byte[]> Payload) EntryWithWeakPayload()
    {
        var serializer = new SystemJsonByteSerializerProxy();
        var bytes = serializer.Serialize(new List<string> { "a" })!;
        return (new DeferredEntry<List<string>>(bytes, serializer, keepsDefault: false, failed: null, Expiration), new WeakReference<byte[]>(bytes));
    }

    private static DeferredEntry<T> Entry<T>(CountingSerializer serializer, T value) =>
        new(serializer.Serialize(value)!, serializer, keepsDefault: false, failed: null, Expiration);
}
