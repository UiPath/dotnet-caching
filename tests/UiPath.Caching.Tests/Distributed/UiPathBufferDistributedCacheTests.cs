#if NET9_0_OR_GREATER
using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Distributed;

namespace UiPath.Caching.Tests.Distributed;

/// <summary>
/// The <see cref="IBufferDistributedCache"/> half of the adapter. The array half is covered by
/// <see cref="UiPathDistributedCacheTests"/>; what is asserted here is that the buffer half reaches the
/// same read and write paths — same key, same fields, same TTL, same sliding — and the two things only it
/// can express: a hit that carries no bytes, and a caller buffer that is copied rather than kept.
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

    private UiPathDistributedCache Build(UiPathDistributedCacheOptions? options = null) =>
        new(_inner,
            options ?? new UiPathDistributedCacheOptions(),
            new PrefixCacheKeyStrategy(UiPathDistributedCacheOptions.DefaultKeyPrefix),
            policy: null,
            NullLogger.Instance,
            new SystemClockTimeProvider(_clock));

    private static byte[] Ticks(long? value) =>
        Encoding.UTF8.GetBytes((value ?? -1).ToString(CultureInfo.InvariantCulture));

    private static Dictionary<string, byte[]?> Entry(
        byte[]? payload = null, long? slidingTicks = null, DateTimeOffset? absolute = null) => new()
        {
            [DataField] = payload ?? Payload,
            [AbsoluteExpirationField] = Ticks(absolute?.UtcTicks),
            [SlidingExpirationField] = Ticks(slidingTicks),
        };

    private void StoredEntry(Dictionary<string, byte[]?> fields) =>
        _inner.GetAsync<byte[]>(Arg.Any<CacheKey>(), Arg.Any<string[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(fields);

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

        await _inner.Received(1).GetAsync<byte[]>(
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

        await _inner.Received(1).RefreshAsync<byte[]>(
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

    [Fact]
    public async Task Set_flattens_every_segment_and_writes_the_same_fields()
    {
        var sliding = TimeSpan.FromMinutes(20);
        IDictionary<string, byte[]?>? written = null;
        TimeSpan? ttl = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Do<IDictionary<string, byte[]?>>(v => written = v),
            Arg.Do<TimeSpan>(t => ttl = t), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());

        await _cache.SetAsync("k", Segmented([1, 2, 3, 4, 5], split: 2),
            new DistributedCacheEntryOptions { SlidingExpiration = sliding }, TestContext.Current.CancellationToken);

        ttl.Should().Be(sliding);
        written.Should().NotBeNull();
        written![DataField].Should().Equal(1, 2, 3, 4, 5);
        Encoding.UTF8.GetString(written[SlidingExpirationField]!).Should().Be(sliding.Ticks.ToString(CultureInfo.InvariantCulture));
        Encoding.UTF8.GetString(written[AbsoluteExpirationField]!).Should().Be("-1");
    }

    /// <summary>A pooled caller reuses its buffer the moment the call returns, so the adapter must not keep a window onto it.</summary>
    [Fact]
    public async Task Set_copies_the_callers_buffer_rather_than_aliasing_it()
    {
        IDictionary<string, byte[]?>? written = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Do<IDictionary<string, byte[]?>>(v => written = v),
            Arg.Any<TimeSpan>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        var buffer = new byte[] { 1, 2, 3 };

        await _cache.SetAsync("k", new ReadOnlySequence<byte>(buffer),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) }, TestContext.Current.CancellationToken);
        buffer[0] = 99;

        written![DataField].Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Set_with_no_expiration_defers_to_the_tier_like_the_array_overload()
    {
        await _cache.SetAsync("k", new ReadOnlySequence<byte>(Payload), new DistributedCacheEntryOptions(), TestContext.Current.CancellationToken);

        await _inner.Received(1).SetAsync(
            Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, byte[]?>>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await _inner.DidNotReceive().SetAsync(
            Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, byte[]?>>(), Arg.Any<TimeSpan>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
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
        IDictionary<string, byte[]?>? written = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Do<IDictionary<string, byte[]?>>(v => written = v),
            Arg.Any<TimeSpan>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());

        _cache.Set("k", new ReadOnlySequence<byte>(Payload),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) });

        written![DataField].Should().Equal(Payload);
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
