using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Distributed;

namespace UiPath.Caching.Tests.Distributed;

public class UiPathDistributedCacheTests
{
    private const string DataField = "data";
    private const string AbsoluteExpirationField = "absexp";
    private const string SlidingExpirationField = "sldexp";

    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Payload = [1, 2, 3];

    private readonly IHashCache _inner = Substitute.For<IHashCache>();
    private readonly ISystemClock _clock = Substitute.For<ISystemClock>();
    private readonly UiPathDistributedCache _cache;

    public UiPathDistributedCacheTests()
    {
        _clock.UtcNow.Returns(Now);
        _cache = Build();
    }

    private UiPathDistributedCache Build(
        UiPathDistributedCacheOptions? options = null,
        bool slideByRewrite = false,
        ICacheKeyStrategy? keyStrategy = null) =>
        new(_inner,
            options ?? new UiPathDistributedCacheOptions(),
            keyStrategy ?? new PrefixCacheKeyStrategy(UiPathDistributedCacheOptions.DefaultKeyPrefix),
            policy: null,
            NullLogger.Instance,
            _clock,
            slideByRewrite);

    private static byte[] Ticks(long? value) =>
        Encoding.UTF8.GetBytes((value ?? -1).ToString(CultureInfo.InvariantCulture));

    private static Dictionary<string, byte[]?> Entry(long? slidingTicks = null, DateTimeOffset? absolute = null) => new()
    {
        [DataField] = Payload,
        [AbsoluteExpirationField] = Ticks(absolute?.UtcTicks),
        [SlidingExpirationField] = Ticks(slidingTicks),
    };

    private void StoredEntry(Dictionary<string, byte[]?> fields) =>
        _inner.GetAsync<byte[]>(Arg.Any<CacheKey>(), Arg.Any<string[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(fields);


    /// <summary>
    /// The storage key the default strategy produces. Tests whose subject is not the key's shape go
    /// through this, so "prefix plus separator" is asserted in one place — as the default, not as the
    /// contract — rather than restated in every expectation.
    /// </summary>
    private static string Key(string callerKey) =>
        new PrefixCacheKeyStrategy(UiPathDistributedCacheOptions.DefaultKeyPrefix)
            .GetCacheKey<byte[]>(new CacheKey(callerKey, CacheKeyCasing.Sensitive)).Name;

    /// <summary>Pinned literally, because changing the default relocates every stored entry.</summary>
    [Fact]
    public async Task Default_key_composition_is_pinned()
    {
        StoredEntry(Entry());
        await _cache.GetAsync("AbC-9xQ", TestContext.Current.CancellationToken);
        await _inner.Received(1).GetAsync<byte[]>(
            Arg.Is<CacheKey>(k => k.Name == "d:AbC-9xQ"),
            Arg.Any<string[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Keys_are_case_sensitive_and_preserved()
    {
        StoredEntry(Entry());
        var expected = Key("AbC-9xQ");
        await _cache.GetAsync("AbC-9xQ", TestContext.Current.CancellationToken);
        await _inner.Received(1).GetAsync<byte[]>(
            Arg.Is<CacheKey>(k => k.Name == expected && k.Casing == CacheKeyCasing.Sensitive),
            Arg.Any<string[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The application's own <see cref="IHashCache"/> asking for the caller key must never land on these entries.</summary>
    [Fact]
    public async Task Composed_key_keeps_the_bare_caller_key_unreachable()
    {
        StoredEntry(Entry());
        var token = TestContext.Current.CancellationToken;
        var expected = Key("AbC");

        await _cache.GetAsync("AbC", token);
        await _cache.SetAsync("AbC", Payload, new DistributedCacheEntryOptions(), token);
        await _cache.RemoveAsync("AbC", token);

        expected.Should().NotBe("AbC");
        await _inner.Received(1).GetAsync<byte[]>(
            Arg.Is<CacheKey>(k => k.Name == expected),
            Arg.Any<string[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await _inner.Received(1).SetAsync(
            Arg.Is<CacheKey>(k => k.Name == expected),
            Arg.Any<IDictionary<string, byte[]?>>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await _inner.Received(1).RemoveAsync<byte[]>(
            Arg.Is<CacheKey>(k => k.Name == expected), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Custom_key_strategy_replaces_the_default()
    {
        StoredEntry(Entry());
        var custom = Build(keyStrategy: new PrefixCacheKeyStrategy("mine", '/'));
        await custom.GetAsync("AbC", TestContext.Current.CancellationToken);
        await _inner.Received(1).GetAsync<byte[]>(
            Arg.Is<CacheKey>(k => k.Name == "mine/AbC" && k.Casing == CacheKeyCasing.Sensitive),
            Arg.Any<string[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The seam only transforms the key, so nothing downstream assumes where the marker sits.</summary>
    [Fact]
    public async Task Key_strategy_need_not_be_a_prefix()
    {
        StoredEntry(Entry());
        var suffixed = Build(keyStrategy: new SuffixKeyStrategy(":d"));
        await suffixed.GetAsync("AbC", TestContext.Current.CancellationToken);
        await _inner.Received(1).GetAsync<byte[]>(
            Arg.Is<CacheKey>(k => k.Name == "AbC:d" && k.Casing == CacheKeyCasing.Sensitive),
            Arg.Any<string[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Default_cache_key_strategy_stores_the_bare_key()
    {
        StoredEntry(Entry());
        var bare = Build(keyStrategy: new DefaultCacheKeyStrategy());
        await bare.GetAsync("AbC", TestContext.Current.CancellationToken);
        await _inner.Received(1).GetAsync<byte[]>(
            Arg.Is<CacheKey>(k => k.Name == "AbC"),
            Arg.Any<string[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Key_strategy_returning_an_empty_key_fails_loudly()
    {
        var broken = Build(keyStrategy: new EmptyKeyStrategy());
        (await FluentActions.Awaiting(() => broken.GetAsync("k", TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidOperationException>()).WithMessage("*CacheKeyStrategy*empty key*");
    }

    /// <summary>
    /// A strategy rebuilding the key instead of using <see cref="CacheKey.WithName"/> picks up the ambient
    /// casing, which would lowercase a case-significant caller key with the original spelling already lost.
    /// </summary>
    [Fact]
    public async Task Key_strategy_dropping_case_sensitivity_fails_loudly()
    {
        var lossy = Build(keyStrategy: new AmbientCasingKeyStrategy());
        (await FluentActions.Awaiting(() => lossy.GetAsync("AbC", TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidOperationException>()).WithMessage("*case-significant*WithName*");
    }

    private sealed class SuffixKeyStrategy(string suffix) : ICacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => key.WithName(key.Name + suffix);
    }

    private sealed class AmbientCasingKeyStrategy : ICacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => new(key.Name, CacheKeyCasing.Insensitive);
    }

    private sealed class EmptyKeyStrategy : ICacheKeyStrategy
    {
        public CacheKey GetCacheKey<T>(CacheKey key) => default;
    }

    [Fact]
    public async Task Null_key_throws()
    {
        await FluentActions.Awaiting(() => _cache.GetAsync(null!, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Whitespace_only_key_throws_argument_exception()
    {
        (await FluentActions.Awaiting(() => _cache.GetAsync("   ", TestContext.Current.CancellationToken))
            .Should().ThrowAsync<ArgumentException>()).And.Should().NotBeOfType<ArgumentNullException>();
    }

    /// <summary>A failed write has to name the entry, or the message cannot be acted on.</summary>
    [Fact]
    public async Task Failed_write_logs_the_key()
    {
        const string key = "Session-AbC";
        var logger = new CapturingLogger();
        var cache = new UiPathDistributedCache(
            _inner, new UiPathDistributedCacheOptions(),
            new PrefixCacheKeyStrategy(UiPathDistributedCacheOptions.DefaultKeyPrefix),
            policy: null, logger, _clock);
        _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, byte[]?>>(),
            Arg.Any<TimeSpan>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(false);

        await cache.SetAsync(key, Payload, new DistributedCacheEntryOptions(), TestContext.Current.CancellationToken);

        logger.Messages.Should().ContainSingle().Which.Should().Contain(key);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    /// <summary>The prefix the strategy adds must not make an empty caller key look valid.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_caller_key_throws(string key)
    {
        await FluentActions.Awaiting(() => _cache.GetAsync(key, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<ArgumentException>();
    }


    [Fact]
    public async Task Get_miss_returns_null()
    {
        StoredEntry([]);
        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Get_returns_payload_and_slides_when_sliding_set()
    {
        var sliding = TimeSpan.FromMinutes(20);
        StoredEntry(Entry(sliding.Ticks));

        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().Equal(Payload);

        await _inner.Received(1).RefreshAsync<byte[]>(
            Arg.Any<CacheKey>(), Now.Add(sliding), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_requests_all_fields_including_data()
    {
        StoredEntry(Entry());
        await _cache.GetAsync("k", TestContext.Current.CancellationToken);
        await _inner.Received(1).GetAsync<byte[]>(
            Arg.Any<CacheKey>(), Arg.Is<string[]>(f => f != null && f.Contains(DataField)),
            Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_slide_is_capped_by_absolute_expiration()
    {
        var absolute = Now.AddMinutes(5);
        StoredEntry(Entry(TimeSpan.FromMinutes(20).Ticks, absolute));

        await _cache.GetAsync("k", TestContext.Current.CancellationToken);

        await _inner.Received(1).RefreshAsync<byte[]>(
            Arg.Any<CacheKey>(), absolute, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_without_sliding_does_not_refresh()
    {
        StoredEntry(Entry(absolute: Now.AddHours(1)));

        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().Equal(Payload);

        await _inner.DidNotReceiveWithAnyArgs().RefreshAsync<byte[]>(default, default(DateTimeOffset), null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Absurd_sliding_window_clamps_instead_of_overflowing()
    {
        StoredEntry(Entry(long.MaxValue));

        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().Equal(Payload);

        await _inner.Received(1).RefreshAsync<byte[]>(
            Arg.Any<CacheKey>(), DateTimeOffset.MaxValue, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unrecognized_entry_is_a_miss()
    {
        StoredEntry(new Dictionary<string, byte[]?> { ["something-else"] = [9] });

        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Expired_absolute_entry_is_a_miss_and_is_not_removed()
    {
        StoredEntry(Entry(TimeSpan.FromMinutes(20).Ticks, Now.AddMinutes(-1)));

        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().BeNull();

        await _inner.DidNotReceiveWithAnyArgs().RemoveAsync<byte[]>(default, TestContext.Current.CancellationToken);
        await _inner.DidNotReceiveWithAnyArgs().RefreshAsync<byte[]>(default, default(DateTimeOffset), null, TestContext.Current.CancellationToken);
    }


    [Fact]
    public async Task Set_writes_payload_and_metadata_fields()
    {
        var sliding = TimeSpan.FromMinutes(20);
        IDictionary<string, byte[]?>? written = null;
        TimeSpan? ttl = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Do<IDictionary<string, byte[]?>>(v => written = v),
            Arg.Do<TimeSpan>(t => ttl = t), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());

        await _cache.SetAsync("k", Payload, new DistributedCacheEntryOptions { SlidingExpiration = sliding }, TestContext.Current.CancellationToken);

        ttl.Should().Be(sliding);
        written.Should().NotBeNull();
        written![DataField].Should().Equal(Payload);
        Encoding.UTF8.GetString(written[SlidingExpirationField]!).Should().Be(sliding.Ticks.ToString(CultureInfo.InvariantCulture));
        Encoding.UTF8.GetString(written[AbsoluteExpirationField]!).Should().Be("-1");
    }

    [Fact]
    public async Task Set_with_both_uses_min_of_sliding_and_remaining_absolute()
    {
        TimeSpan? ttl = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, byte[]?>>(),
            Arg.Do<TimeSpan>(t => ttl = t), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());

        await _cache.SetAsync("k", Payload, new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(20),
            AbsoluteExpiration = Now.AddMinutes(5),
        }, TestContext.Current.CancellationToken);

        ttl.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Set_with_relative_absolute_records_the_deadline()
    {
        IDictionary<string, byte[]?>? written = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Do<IDictionary<string, byte[]?>>(v => written = v),
            Arg.Any<TimeSpan>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());

        await _cache.SetAsync("k", Payload, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
        }, TestContext.Current.CancellationToken);

        Encoding.UTF8.GetString(written![AbsoluteExpirationField]!)
            .Should().Be(Now.AddMinutes(30).UtcTicks.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Set_with_no_expiration_uses_the_configured_default()
    {
        TimeSpan? ttl = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, byte[]?>>(),
            Arg.Do<TimeSpan>(t => ttl = t), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        var bounded = Build(new UiPathDistributedCacheOptions { DefaultEntryExpiration = TimeSpan.FromHours(2) });

        await bounded.SetAsync("k", Payload, new DistributedCacheEntryOptions(), TestContext.Current.CancellationToken);

        ttl.Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task Set_with_no_expiration_and_no_default_defers_to_the_tier()
    {
        await _cache.SetAsync("k", Payload, new DistributedCacheEntryOptions(), TestContext.Current.CancellationToken);

        // The write carries no expiration argument at all, which is the only way left to ask the
        // tier for its own default.
        await _inner.Received(1).SetAsync(
            Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, byte[]?>>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await _inner.DidNotReceive().SetAsync(
            Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, byte[]?>>(), Arg.Any<TimeSpan>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_with_past_absolute_expiration_throws()
    {
        await FluentActions.Awaiting(() => _cache.SetAsync("k", Payload,
                new DistributedCacheEntryOptions { AbsoluteExpiration = Now.AddMinutes(-1) }, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }


    [Fact]
    public async Task Refresh_reads_metadata_only()
    {
        var sliding = TimeSpan.FromMinutes(20);
        StoredEntry(Entry(sliding.Ticks));

        await _cache.RefreshAsync("k", TestContext.Current.CancellationToken);

        await _inner.Received(1).GetAsync<byte[]>(
            Arg.Any<CacheKey>(), Arg.Is<string[]>(f => f != null && !f.Contains(DataField)),
            Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await _inner.Received(1).RefreshAsync<byte[]>(
            Arg.Any<CacheKey>(), Now.Add(sliding), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_forwards()
    {
        var expected = Key("AbC");
        await _cache.RemoveAsync("AbC", TestContext.Current.CancellationToken);
        await _inner.Received(1).RemoveAsync<byte[]>(
            Arg.Is<CacheKey>(k => k.Name == expected), Arg.Any<CancellationToken>());
    }


    [Fact]
    public async Task SlideByRewrite_extends_by_writing_the_entry_back()
    {
        var sliding = TimeSpan.FromMinutes(20);
        StoredEntry(Entry(sliding.Ticks));
        IDictionary<string, byte[]?>? written = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Do<IDictionary<string, byte[]?>>(v => written = v),
            Arg.Any<HashCacheEntryOptions>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        var rewriting = Build(slideByRewrite: true);

        (await rewriting.GetAsync("k", TestContext.Current.CancellationToken)).Should().Equal(Payload);

        written.Should().NotBeNull();
        written!.Should().ContainKey(SlidingExpirationField);
        written[DataField].Should().Equal(Payload);
        await _inner.DidNotReceiveWithAnyArgs().RefreshAsync<byte[]>(default, default(DateTimeOffset), null, TestContext.Current.CancellationToken);
    }


    [Fact]
    public void Sync_methods_block_on_async()
    {
        StoredEntry(Entry());
        _cache.Get("k").Should().Equal(Payload);
    }

    [Fact]
    public async Task Unbounded_entries_persist_instead_of_taking_a_default()
    {
        DateTimeOffset? expiration = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, byte[]?>>(),
            Arg.Do<DateTimeOffset>(e => expiration = e), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        var unbounded = Build(new UiPathDistributedCacheOptions { AllowUnboundedEntries = true });

        await unbounded.SetAsync("k", Payload, new DistributedCacheEntryOptions(), TestContext.Current.CancellationToken);

        expiration.Should().Be(DateTimeOffset.MaxValue);
    }

    [Fact]
    public async Task Absurd_sliding_write_clamps_the_ttl()
    {
        TimeSpan? ttl = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, byte[]?>>(),
            Arg.Do<TimeSpan>(t => ttl = t), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());

        await _cache.SetAsync("k", Payload,
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.MaxValue }, TestContext.Current.CancellationToken);

        ttl.Should().NotBeNull();
        // Against a later clock reading than the adapter's, which is what the backing cache actually adds to.
        FluentActions.Invoking(() => Now.AddSeconds(30).Add(ttl!.Value)).Should().NotThrow();
    }

    /// <summary>
    /// The hash layer reports a zero-length field as absent, so metadata presence is what distinguishes
    /// "our entry with an empty payload" from "not our entry".
    /// </summary>
    [Fact]
    public async Task Empty_payload_reads_back_as_empty_not_a_miss()
    {
        StoredEntry(new Dictionary<string, byte[]?>
        {
            [DataField] = null,
            [AbsoluteExpirationField] = Ticks(null),
            [SlidingExpirationField] = Ticks(null),
        });

        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Absurd_default_entry_expiration_is_clamped()
    {
        TimeSpan? ttl = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Any<IDictionary<string, byte[]?>>(),
            Arg.Do<TimeSpan>(t => ttl = t), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        var cache = Build(new UiPathDistributedCacheOptions { DefaultEntryExpiration = TimeSpan.MaxValue });

        await cache.SetAsync("k", Payload, new DistributedCacheEntryOptions(), TestContext.Current.CancellationToken);

        ttl.Should().NotBeNull();
        // Against a later clock reading than the adapter's, which is what the backing cache actually adds to.
        FluentActions.Invoking(() => Now.AddSeconds(30).Add(ttl!.Value)).Should().NotThrow();
    }

    private const string FieldOmitted = "\0omitted";

    /// <summary>
    /// Values no write can produce, per field. Each is a miss: serving one would hand back the payload with its
    /// expiration silently dropped — as an entry that never expires — which is the failure this decode exists to
    /// prevent. Whitespace padding is included because the writer never emits it.
    /// </summary>
    public static TheoryData<string?> RejectedMetadataValues() =>
    [
        (string?)null,            // field present, null value — the shape a Redis miss returns
        FieldOmitted,             // field absent entirely
        "",
        " ",
        "garbage",
        "1.5",
        "1e3",
        " 1 ",
        "0",                      // parses, but is skipped downstream rather than applied
        "-2",                     // not the sentinel
        "9223372036854775808",    // one past long.MaxValue: does not parse
    ];

    [Theory]
    [MemberData(nameof(RejectedMetadataValues))]
    public async Task Rejected_absolute_expiration_is_a_miss(string? value) =>
        await AssertMetadataIsRejected(AbsoluteExpirationField, value);

    [Theory]
    [MemberData(nameof(RejectedMetadataValues))]
    public async Task Rejected_sliding_expiration_is_a_miss(string? value) =>
        await AssertMetadataIsRejected(SlidingExpirationField, value);

    private async Task AssertMetadataIsRejected(string field, string? value)
    {
        var entry = new Dictionary<string, byte[]?>(StringComparer.Ordinal)
        {
            [DataField] = Payload,
            [AbsoluteExpirationField] = Ticks(null),
            [SlidingExpirationField] = Ticks(null),
        };

        if (value == FieldOmitted)
        {
            entry.Remove(field);
        }
        else
        {
            entry[field] = value is null ? null : Encoding.UTF8.GetBytes(value);
        }

        StoredEntry(entry);

        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    /// <summary>
    /// The two fields carry different quantities, so their ranges differ: an absolute deadline is a
    /// <see cref="DateTime"/> tick count, a sliding window a <see cref="TimeSpan"/> one, which reaches further.
    /// </summary>
    [Fact]
    public async Task Field_ranges_follow_the_quantity_each_field_carries()
    {
        var token = TestContext.Current.CancellationToken;
        var beyondDateTime = Encoding.UTF8.GetBytes((DateTime.MaxValue.Ticks + 1).ToString(CultureInfo.InvariantCulture));

        StoredEntry(new Dictionary<string, byte[]?>(StringComparer.Ordinal)
        {
            [DataField] = Payload,
            [AbsoluteExpirationField] = beyondDateTime,
            [SlidingExpirationField] = Ticks(null),
        });
        (await _cache.GetAsync("k", token)).Should().BeNull("an absolute deadline past DateTime.MaxValue cannot be decoded");

        StoredEntry(new Dictionary<string, byte[]?>(StringComparer.Ordinal)
        {
            [DataField] = Payload,
            [AbsoluteExpirationField] = Ticks(null),
            [SlidingExpirationField] = beyondDateTime,
        });
        (await _cache.GetAsync("k", token)).Should().Equal(Payload, "the same number is a valid TimeSpan");
    }

    /// <summary>
    /// The expiration shapes a caller can write, as serializable parts rather than a
    /// <see cref="DistributedCacheEntryOptions"/> — the options type is not serializable, so passing it
    /// directly leaves the runner unable to enumerate the rows individually.
    /// </summary>
    public static TheoryData<TimeSpan?, TimeSpan?, DateTimeOffset?> WriteShapes() => new()
    {
        { null, null, null },
        { TimeSpan.FromMinutes(20), null, null },
        { TimeSpan.FromTicks(1), null, null },
        { TimeSpan.MaxValue, null, null },
        { null, TimeSpan.FromHours(2), null },
        { null, TimeSpan.MaxValue, null },
        { null, null, Now.AddDays(1) },
        { TimeSpan.FromMinutes(20), TimeSpan.FromHours(2), null },
    };

    /// <summary>
    /// Whatever a write produces must decode as a hit. This is the property that keeps the accepted set and the
    /// producible set in step: validating and decoding separately let the two rule sets drift, which is what
    /// turned malformed metadata into a never-expiring entry.
    /// </summary>
    [Theory]
    [MemberData(nameof(WriteShapes))]
    public async Task Everything_a_write_produces_decodes_as_a_hit(
        TimeSpan? slidingExpiration, TimeSpan? absoluteExpirationRelativeToNow, DateTimeOffset? absoluteExpiration)
    {
        var options = new DistributedCacheEntryOptions
        {
            SlidingExpiration = slidingExpiration,
            AbsoluteExpirationRelativeToNow = absoluteExpirationRelativeToNow,
            AbsoluteExpiration = absoluteExpiration,
        };
        var token = TestContext.Current.CancellationToken;
        IDictionary<string, byte[]?>? written = null;
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Do<IDictionary<string, byte[]?>>(v => written = v),
            Arg.Any<TimeSpan>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await _inner.SetAsync(Arg.Any<CacheKey>(), Arg.Do<IDictionary<string, byte[]?>>(v => written = v),
            Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());

        await _cache.SetAsync("k", Payload, options, token);

        written.Should().NotBeNull();
        StoredEntry(new Dictionary<string, byte[]?>(written!, StringComparer.Ordinal));

        (await _cache.GetAsync("k", token)).Should().Equal(Payload);
    }

    /// <summary>
    /// RedisHashCache returns a dictionary containing every requested field with null values when HMGET
    /// finds nothing, so field presence alone cannot mean "entry exists".
    /// </summary>
    [Fact]
    public async Task Redis_shaped_miss_is_a_miss_not_an_empty_payload()
    {
        StoredEntry(new Dictionary<string, byte[]?>
        {
            [DataField] = null,
            [AbsoluteExpirationField] = null,
            [SlidingExpirationField] = null,
        });

        (await _cache.GetAsync("k", TestContext.Current.CancellationToken)).Should().BeNull();
    }
}
