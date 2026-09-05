#if NET9_0_OR_GREATER
using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Distributed;

namespace UiPath.Caching.Tests.Distributed;

/// <summary>
/// The <see cref="IBufferDistributedCache"/> half of the adapter. The array half is covered by
/// <see cref="UiPathDistributedCacheTests"/>; what is asserted here is that the buffer half reaches the
/// same read and write paths — same key, same fields, same TTL, same sliding — and the things only it
/// can express: a hit that carries no bytes, and what happens to a caller's buffer on the way in, which
/// depends on whether the tier keeps what it is handed.
/// </summary>
public class UiPathBufferDistributedCacheTests
{
    private const string DataField = "data";
    private const string AbsoluteExpirationField = "absexp";
    private const string SlidingExpirationField = "sldexp";

    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Payload = [1, 2, 3];

    private readonly IHashCache _inner = Substitute.For<IHashCache>();
    private readonly ISystemClock _clock = Substitute.For<ISystemClock>();
    private readonly UiPathDistributedCache _cache;

    public UiPathBufferDistributedCacheTests()
    {
        _clock.UtcNow.Returns(Now);
        _cache = Build();
    }

    /// <summary>Defaults to the Redis-tier shape: nothing downstream keeps the values, so a buffer may pass straight through.</summary>
    private UiPathDistributedCache Build(UiPathDistributedCacheOptions? options = null, bool tierRetainsValues = false) =>
        new(_inner,
            options ?? new UiPathDistributedCacheOptions(),
            new PrefixCacheKeyStrategy(UiPathDistributedCacheOptions.DefaultKeyPrefix),
            policy: null,
            NullLogger.Instance,
            new SystemClockTimeProvider(_clock),
            tierRetainsValues: tierRetainsValues);

    private static byte[] Ticks(long? value) =>
        Encoding.UTF8.GetBytes((value ?? -1).ToString(CultureInfo.InvariantCulture));

    private static Dictionary<string, ReadOnlyMemory<byte>> Entry(
        byte[]? payload = null, long? slidingTicks = null, DateTimeOffset? absolute = null) => new()
        {
            [DataField] = payload ?? Payload,
            [AbsoluteExpirationField] = Ticks(absolute?.UtcTicks),
            [SlidingExpirationField] = Ticks(slidingTicks),
        };

    private void StoredEntry(Dictionary<string, ReadOnlyMemory<byte>> fields) =>
        _inner.GetAsync<ReadOnlyMemory<byte>>(Arg.Any<CacheKey>(), Arg.Any<string[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(fields);

    /// <summary>
    /// Captures what the write hands the tier. Copied at capture time: on the Redis-tier shape the payload
    /// memory is only valid during the call, exactly as it would be for the real tier.
    /// </summary>
    private async Task<Dictionary<string, byte[]>> CaptureWriteAsync(Func<Task> write)
    {
        Dictionary<string, byte[]>? written = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(),
            Arg.Do<IDictionary<string, ReadOnlyMemory<byte>>>(v => written = v.ToDictionary(p => p.Key, p => p.Value.ToArray())),
            Arg.Any<TimeSpan>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await write();
        written.Should().NotBeNull();
        return written!;
    }

    private static byte[] BackingArray(ReadOnlyMemory<byte> memory)
    {
        MemoryMarshal.TryGetArray(memory, out var segment).Should().BeTrue();
        return segment.Array!;
    }

    /// <summary>Splits <paramref name="payload"/> across two segments, which is the shape a pooled writer produces and the one a naive <c>First.Span</c> read would truncate.</summary>
    private static ReadOnlySequence<byte> Segmented(byte[] payload, int split)
    {
        var first = new Segment(payload.AsMemory(0, split), runningIndex: 0);
        var second = first.Append(payload.AsMemory(split));
        return new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }

    /// <summary>The type check every consumer uses to find the buffer half; without it they silently stay on the array path.</summary>
    [Fact]
    public void Adapter_is_discoverable_as_a_buffer_cache()
    {
        ((IDistributedCache)_cache).Should().BeAssignableTo<IBufferDistributedCache>();
        NullDistributedCache.Instance.Should().BeAssignableTo<IBufferDistributedCache>(
            "switching caching off must not change which half of the contract a consumer finds");
    }

    [Fact]
    public async Task TryGet_writes_the_payload_and_reports_a_hit()
    {
        StoredEntry(Entry());
        var destination = new ArrayBufferWriter<byte>();

        (await _cache.TryGetAsync("k", destination, TestContext.Current.CancellationToken)).Should().BeTrue();

        destination.WrittenSpan.ToArray().Should().Equal(Payload);
    }

    [Fact]
    public async Task TryGet_miss_reports_false_and_writes_nothing()
    {
        StoredEntry([]);
        var destination = new ArrayBufferWriter<byte>();

        (await _cache.TryGetAsync("k", destination, TestContext.Current.CancellationToken)).Should().BeFalse();

        destination.WrittenCount.Should().Be(0);
    }

    /// <summary>
    /// The reason the contract returns a <see cref="bool"/> separately from the bytes: an entry stored empty
    /// is a hit that writes nothing, where <c>GetAsync</c> has only "no bytes" to say for both cases.
    /// </summary>
    [Fact]
    public async Task Stored_empty_payload_is_a_hit_that_writes_nothing()
    {
        StoredEntry(Entry(payload: []));
        var destination = new ArrayBufferWriter<byte>();

        (await _cache.TryGetAsync("k", destination, TestContext.Current.CancellationToken)).Should().BeTrue();

        destination.WrittenCount.Should().Be(0);
        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().BeEmpty(
            "the array overload reports the same entry as an empty payload");
    }

    [Fact]
    public async Task TryGet_reads_the_composed_key_and_asks_for_the_data_field()
    {
        StoredEntry(Entry());

        await _cache.TryGetAsync("AbC-9xQ", new ArrayBufferWriter<byte>(), TestContext.Current.CancellationToken);

        await _inner.Received(1).GetAsync<ReadOnlyMemory<byte>>(
            Arg.Is<CacheKey>(k => k.Name == "d:AbC-9xQ" && k.Casing == CacheKeyCasing.Sensitive),
            Arg.Is<string[]>(f => f != null && f.Contains(DataField)),
            Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGet_slides_like_a_read()
    {
        var sliding = TimeSpan.FromMinutes(20);
        StoredEntry(Entry(slidingTicks: sliding.Ticks));

        await _cache.TryGetAsync("k", new ArrayBufferWriter<byte>(), TestContext.Current.CancellationToken);

        await _inner.Received(1).RefreshAsync<ReadOnlyMemory<byte>>(
            Arg.Any<CacheKey>(), Now.Add(sliding), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGet_of_an_expired_entry_reports_a_miss()
    {
        StoredEntry(Entry(absolute: Now.AddMinutes(-1)));
        var destination = new ArrayBufferWriter<byte>();

        (await _cache.TryGetAsync("k", destination, TestContext.Current.CancellationToken)).Should().BeFalse();

        destination.WrittenCount.Should().Be(0);
    }

    [Fact]
    public async Task TryGet_validates_the_key_and_the_destination()
    {
        await FluentActions.Awaiting(() => _cache.TryGetAsync(null!, new ArrayBufferWriter<byte>(), TestContext.Current.CancellationToken).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _cache.TryGetAsync("k", null!, TestContext.Current.CancellationToken).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _cache.TryGetAsync("   ", new ArrayBufferWriter<byte>(), TestContext.Current.CancellationToken).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Synchronous_TryGet_returns_the_same_answer()
    {
        StoredEntry(Entry());
        var destination = new ArrayBufferWriter<byte>();

        _cache.TryGet("k", destination).Should().BeTrue();

        destination.WrittenSpan.ToArray().Should().Equal(Payload);
    }

    /// <summary>
    /// The array half hands back the tier's array itself rather than a copy of it, as it did before values
    /// travelled as memory — every tier holds whole arrays, so the memory always covers one.
    /// </summary>
    [Fact]
    public async Task Get_returns_the_stored_array_without_copying()
    {
        var stored = new byte[] { 4, 5, 6 };
        StoredEntry(Entry(payload: stored));

        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().BeSameAs(stored);
    }

    [Fact]
    public async Task Set_flattens_every_segment_and_writes_the_same_fields()
    {
        var sliding = TimeSpan.FromMinutes(20);
        TimeSpan? ttl = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, ReadOnlyMemory<byte>>>(),
            Arg.Do<TimeSpan>(t => ttl = t), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());

        var written = await CaptureWriteAsync(() => _cache.SetAsync("k", Segmented([1, 2, 3, 4, 5], split: 2),
            new DistributedCacheEntryOptions { SlidingExpiration = sliding }, TestContext.Current.CancellationToken).AsTask());

        ttl.Should().Be(sliding);
        written[DataField].Should().Equal(1, 2, 3, 4, 5);
        Encoding.UTF8.GetString(written[SlidingExpirationField]).Should().Be(sliding.Ticks.ToString(CultureInfo.InvariantCulture));
        Encoding.UTF8.GetString(written[AbsoluteExpirationField]).Should().Be("-1");
    }

    /// <summary>
    /// On a tier that keeps what it is handed, the caller's buffer must not be what it keeps: a pooled caller
    /// reuses that buffer the moment the call returns.
    /// </summary>
    [Fact]
    public async Task Set_on_a_retaining_tier_copies_the_callers_buffer()
    {
        var retaining = Build(tierRetainsValues: true);
        ReadOnlyMemory<byte> handed = default;
        await _inner.SetAsync(Arg.Any<CacheKey>(),
            Arg.Do<IDictionary<string, ReadOnlyMemory<byte>>>(v => handed = v[DataField]),
            Arg.Any<TimeSpan>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        var buffer = new byte[] { 1, 2, 3 };

        await retaining.SetAsync("k", new ReadOnlySequence<byte>(buffer),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) }, TestContext.Current.CancellationToken);
        buffer[0] = 99;

        BackingArray(handed).Should().NotBeSameAs(buffer);
        handed.ToArray().Should().Equal(1, 2, 3);
    }

    /// <summary>
    /// On the Redis-tier shape the buffer goes through untouched: the connection copies it as it writes the
    /// command, and the write is awaited to completion, so no array is needed in between.
    /// </summary>
    [Fact]
    public async Task Set_on_a_pass_through_tier_hands_over_the_callers_memory_itself()
    {
        ReadOnlyMemory<byte> handed = default;
        await _inner.SetAsync(Arg.Any<CacheKey>(),
            Arg.Do<IDictionary<string, ReadOnlyMemory<byte>>>(v => handed = v[DataField]),
            Arg.Any<TimeSpan>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        var buffer = new byte[] { 1, 2, 3 };

        await _cache.SetAsync("k", new ReadOnlySequence<byte>(buffer),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) }, TestContext.Current.CancellationToken);

        BackingArray(handed).Should().BeSameAs(buffer);
    }

    /// <summary>A segmented sequence has no single memory to pass, so it is flattened — but on a pass-through tier into a rented buffer, not a fresh array, and what the tier saw is correct.</summary>
    [Fact]
    public async Task Segmented_set_on_a_pass_through_tier_writes_the_flattened_bytes()
    {
        var written = await CaptureWriteAsync(() => _cache.SetAsync("k", Segmented([1, 2, 3, 4, 5, 6, 7], split: 3),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) }, TestContext.Current.CancellationToken).AsTask());

        written[DataField].Should().Equal(1, 2, 3, 4, 5, 6, 7);
    }

    [Fact]
    public async Task Empty_sequence_writes_an_empty_payload()
    {
        var written = await CaptureWriteAsync(() => _cache.SetAsync("k", ReadOnlySequence<byte>.Empty,
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) }, TestContext.Current.CancellationToken).AsTask());

        written[DataField].Should().BeEmpty();
        Encoding.UTF8.GetString(written[SlidingExpirationField]).Should().NotBe("-1", "the metadata is what marks the entry as ours");
    }

    [Fact]
    public async Task Set_with_no_expiration_defers_to_the_tier_like_the_array_overload()
    {
        await _cache.SetAsync("k", new ReadOnlySequence<byte>(Payload), new DistributedCacheEntryOptions(), TestContext.Current.CancellationToken);

        await _inner.Received(1).SetAsync(
            Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, ReadOnlyMemory<byte>>>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await _inner.DidNotReceive().SetAsync(
            Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, ReadOnlyMemory<byte>>>(), Arg.Any<TimeSpan>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_with_a_past_absolute_expiration_throws()
    {
        await FluentActions.Awaiting(() => _cache.SetAsync("k", new ReadOnlySequence<byte>(Payload),
                new DistributedCacheEntryOptions { AbsoluteExpiration = Now.AddMinutes(-1) }, TestContext.Current.CancellationToken).AsTask())
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Synchronous_Set_writes_the_entry()
    {
        var written = await CaptureWriteAsync(() =>
        {
            _cache.Set("k", new ReadOnlySequence<byte>(Payload), new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) });
            return Task.CompletedTask;
        });

        written[DataField].Should().Equal(Payload);
    }

    [Fact]
    public void Null_cache_reports_a_miss_and_swallows_the_write()
    {
        IBufferDistributedCache cache = NullDistributedCache.Instance;
        var destination = new ArrayBufferWriter<byte>();

        cache.TryGet("k", destination).Should().BeFalse();
        cache.Set("k", new ReadOnlySequence<byte>(Payload), new DistributedCacheEntryOptions());

        destination.WrittenCount.Should().Be(0);
    }
}
#endif
