using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.CloudEvents;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Redis;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Broadcast;

public class EventFormatSeamTests
{
    private static readonly Uri Source = new("urn:node-a");

    public static TheoryData<string, int, bool, Type> Rejected() => new()
    {
        { "json", 0x12, false, typeof(ArgumentException) },
        { "JSON", 0x12, false, typeof(ArgumentException) },
        { " ", 0x12, false, typeof(ArgumentException) },
        { "ACME", 0x12, false, typeof(ArgumentException) },
        { "other", 0x11, false, typeof(ArgumentException) },
        { "other", 0x00, false, typeof(ArgumentOutOfRangeException) },
        { "other", 0x09, false, typeof(ArgumentOutOfRangeException) },
        { "other", 0x0A, false, typeof(ArgumentOutOfRangeException) },
        { "other", 0x0D, false, typeof(ArgumentOutOfRangeException) },
        { "other", 0x1F, false, typeof(ArgumentOutOfRangeException) },
        { "other", 0x20, false, typeof(ArgumentOutOfRangeException) },
        { "other", 0x100, false, typeof(ArgumentOutOfRangeException) },
        { "other", -1, false, typeof(ArgumentOutOfRangeException) },
        { "other", 0x12, true, typeof(InvalidOperationException) },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void A_rejected_registration_throws_at_the_call_and_changes_nothing(string name, int id, bool write, Type expected)
    {
        var builder = new CachingBuilder(new ServiceCollection());
        builder.AddEventFormat("acme", 0x11, _ => new IdFormatter(0x11), write: true);
        var before = builder.Services.Select(d => (d.ServiceType, d.ServiceKey)).ToList();

        var act = () => builder.AddEventFormat(name, id, _ => new IdFormatter((byte)id), write);

        act.Should().Throw<Exception>().Which.Should().BeOfType(expected);
        builder.Services.Select(d => (d.ServiceType, d.ServiceKey)).Should().Equal(before);
        Registry(builder).Formats.Select(f => f.Name).Should().Equal("acme");
    }

    [Theory]
    [InlineData("", 0x12)]
    [InlineData("other", 0x100)]
    [InlineData("other", 0x1F)]
    public void A_rejected_first_registration_leaves_the_builder_empty(string name, int id)
    {
        var builder = new CachingBuilder(new ServiceCollection());

        var act = () => builder.AddEventFormat(name, id, _ => new IdFormatter(1), write: true);

        act.Should().Throw<ArgumentException>();
        builder.Services.Should().BeEmpty();
    }

    [Fact]
    public void Missing_arguments_throw()
    {
        var builder = new CachingBuilder(new ServiceCollection());

        ((Action)(() => builder.AddEventFormat("a", 0x11, null!, true))).Should().Throw<ArgumentNullException>();
        ((Action)(() => ((ICachingBuilder)null!).AddEventFormat("a", 0x11, _ => new IdFormatter(0x11), true))).Should().Throw<ArgumentNullException>();
        ((Action)(() => builder.AddEventFormat(null!, 0x11, _ => new IdFormatter(0x11), true))).Should().Throw<ArgumentException>();
        builder.Services.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x05)]
    [InlineData(0x0F)]
    [InlineData(0x10)]
    [InlineData(0x1E)]
    public void An_id_in_the_allocated_or_consumer_range_is_accepted(int id)
    {
        var builder = new CachingBuilder(new ServiceCollection());

        builder.AddEventFormat("acme", id, _ => new IdFormatter((byte)id), write: true);

        Registry(builder).WriteToken.Should().Be(id.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_read_only_format_beside_the_writer_is_accepted_whichever_comes_first()
    {
        var first = new CachingBuilder(new ServiceCollection());
        first.AddEventFormat("a", 0x11, _ => new IdFormatter(0x11), write: false);
        first.AddEventFormat("b", 0x12, _ => new IdFormatter(0x12), write: true);
        var second = new CachingBuilder(new ServiceCollection());
        second.AddEventFormat("a", 0x11, _ => new IdFormatter(0x11), write: true);
        second.AddEventFormat("b", 0x12, _ => new IdFormatter(0x12), write: false);

        Registry(first).WriteToken.Should().Be("12");
        Registry(second).WriteToken.Should().Be("11");
    }

    [Fact]
    public void Two_formats_decode_by_the_first_byte_and_json_decodes_the_rest()
    {
        var json = new RecordingJson();
        var formatA = new IdFormatter(0x11);
        var formatB = new IdFormatter(0x12);
        using var provider = Provider(
            b =>
            {
                b.Services.AddSingleton<IEventFormatterProxy<ICacheEvent>>(json);
                b.AddEventFormat("a", 0x11, _ => formatA, write: true);
                b.AddEventFormat("b", 0x12, _ => formatB, write: false);
            },
            force: true);
        var composite = provider.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();
        var original = Event();

        var fromA = composite.Decode(new IdFormatter(0x11).Encode(original));
        var fromB = composite.Decode(new IdFormatter(0x12).Encode(original));
        var unregistered = composite.Decode(new byte[] { 0x13, (byte)'{' });
        var text = composite.Decode(Encoding.UTF8.GetBytes("{}"));

        fromA!.Id.Should().Be(original.Id);
        fromB!.Id.Should().Be(original.Id);
        json.Bodies.Select(b => b[0]).Should().Equal(new byte[] { 0x13, (byte)'{' }, "only a body no format owns reaches JSON");
        formatA.Offered.Should().Equal(new byte[] { 0x11 }, "a body is offered only to the format that owns its first byte");
        formatB.Offered.Should().Equal((byte)0x12);
        unregistered.Should().BeNull();
        text.Should().BeNull();
    }

    [Fact]
    public void Only_the_writer_writes_and_it_writes_its_own_id()
    {
        using var provider = Provider(
            b =>
            {
                b.AddEventFormat("a", 0x11, _ => new IdFormatter(0x11), write: false);
                b.AddEventFormat("b", 0x12, _ => new IdFormatter(0x12), write: true);
            },
            force: true);

        provider.GetRequiredService<IEventFormatterProxy<ICacheEvent>>().Encode(Event()).Span[0].Should().Be(0x12);
    }

    [Fact]
    public void A_read_only_registration_writes_json()
    {
        using var provider = Provider(b => b.AddEventFormat("a", 0x11, _ => new IdFormatter(0x11), write: false), force: true);

        provider.GetRequiredService<IEventFormatterProxy<ICacheEvent>>().Encode(Event()).Span[0].Should().Be((byte)'{');
    }

    [Fact]
    public void The_tokens_come_from_the_declared_ids()
    {
        using var provider = Provider(
            b =>
            {
                b.AddMessagePackSerializer();
                b.AddMessagePackEvents(write: false);
                b.AddEventFormat("acme", 0x11, _ => new IdFormatter(0x11), write: true);
            });

        var advertisement = provider.GetRequiredService<FormatNegotiation>().Advertisement!;
        var registry = provider.GetRequiredService<EventFormatRegistry>();

        advertisement.EventReads.Should().Equal("json", "02", "11");
        advertisement.Encode().Should().Contain("vr=json,02,03").And.Contain("er=json,02,11");
        registry.ReadTokens.Should().Equal("02", "11");
        registry.WriteToken.Should().Be("11");
    }

    [Fact]
    public void A_message_pack_node_still_advertises_er_json_02_and_writes_a_body_that_starts_with_02()
    {
        using var provider = Provider(b => b.AddMessagePackEvents(write: true), force: true);

        provider.GetRequiredService<FormatNegotiation>().Advertisement!.Encode().Should().EndWith("er=json,02");
        provider.GetRequiredService<IEventFormatterProxy<ICacheEvent>>().Encode(Event()).Span[0].Should().Be(0x02);
        MessagePackFormats.Event.Should().Be(0x02);
    }

    [Fact]
    public void A_body_that_does_not_start_with_the_declared_id_goes_out_as_json_and_one_error_is_logged()
    {
        var logger = new RecordedLogger();
        var original = Event();
        var composite = new CompositeEventFormatter(
            new CacheEventFormatter(),
            [(0x11, "a", new IdFormatter(0x11, startWith: 0x7F), true)],
            FixedEventWrites.Always,
            logger);

        var first = composite.Encode(original);
        var second = composite.Encode(original);

        first.Span[0].Should().Be((byte)'{');
        second.Span[0].Should().Be((byte)'{');
        composite.Decode(first)!.Id.Should().Be(original.Id);
        logger.Records.Where(r => r.Level == LogLevel.Error).Should().ContainSingle().Which.Message.Should().Contain("'a'").And.Contain("0x11").And.Contain("0x7F");
    }

    [Fact]
    public void An_empty_body_from_the_writer_is_a_mismatch_too()
    {
        var logger = new RecordedLogger();
        var composite = new CompositeEventFormatter(
            new CacheEventFormatter(),
            [(0x11, "a", new EmptyFormatter(), true)],
            FixedEventWrites.Always,
            logger);

        composite.Encode(Event()).Span[0].Should().Be((byte)'{');
        logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Error);
    }

    [Fact]
    public void A_matching_body_logs_nothing()
    {
        var logger = new RecordedLogger();
        var composite = new CompositeEventFormatter(new CacheEventFormatter(), [(0x11, "a", new IdFormatter(0x11), true)], FixedEventWrites.Always, logger);

        composite.Encode(Event()).Span[0].Should().Be(0x11);
        logger.Records.Should().BeEmpty();
    }

    [Fact]
    public void A_writer_that_throws_goes_out_as_json_and_one_error_is_logged_per_format()
    {
        var logger = new RecordedLogger();
        var original = Event();
        var composite = new CompositeEventFormatter(new CacheEventFormatter(), [(0x11, "a", new ThrowingFormatter(), true)], FixedEventWrites.Always, logger);

        var first = composite.Encode(original);
        var second = composite.Encode(original);

        first.Span[0].Should().Be((byte)'{');
        second.Span[0].Should().Be((byte)'{');
        composite.Decode(first)!.Id.Should().Be(original.Id);
        logger.Records.Where(r => r.Level == LogLevel.Error).Should().ContainSingle().Which.Message.Should().Contain("'a'").And.Contain("0x11");
    }

    [Fact]
    public void A_writer_that_throws_a_cancellation_falls_back_to_json()
    {
        foreach (var thrown in new Exception[] { new OperationCanceledException(), new TaskCanceledException() })
        {
            var composite = new CompositeEventFormatter(new CacheEventFormatter(), [(0x11, "a", new ThrowingFormatter(thrown), true)], FixedEventWrites.Always, NullLogger.Instance);

            var body = composite.Encode(Event());

            body.Span[0].Should().Be((byte)'{', thrown.GetType().Name);
        }
    }

    [Fact]
    public void The_composite_rejects_a_repeated_id_a_second_writer_and_an_id_outside_the_slots()
    {
        var json = new CacheEventFormatter();
        var one = new IdFormatter(0x11);
        var two = new IdFormatter(0x12);

        var repeated = () => new CompositeEventFormatter(json, [(0x11, "a", one, false), (0x11, "b", two, false)], FixedEventWrites.Always, NullLogger.Instance);
        var writers = () => new CompositeEventFormatter(json, [(0x11, "a", one, true), (0x12, "b", two, true)], FixedEventWrites.Always, NullLogger.Instance);
        var zero = () => new CompositeEventFormatter(json, [(0, "a", one, false)], FixedEventWrites.Always, NullLogger.Instance);
        var high = () => new CompositeEventFormatter(json, [(0x1F, "a", one, false)], FixedEventWrites.Always, NullLogger.Instance);

        repeated.Should().Throw<ArgumentException>();
        writers.Should().Throw<ArgumentException>();
        zero.Should().Throw<ArgumentOutOfRangeException>();
        high.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_add_caching_calls_with_a_format_each_resolve_one_composite_that_wraps_json_once(bool withJson)
    {
        var json = new RecordingJson();
        var formatA = new IdFormatter(0x11);
        var formatB = new IdFormatter(0x12);
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(
            b =>
            {
                if (withJson)
                {
                    b.Services.AddSingleton<IEventFormatterProxy<ICacheEvent>>(json);
                }

                b.AddEventFormat("a", 0x11, _ => formatA, write: false);
            },
            o => o.AppShortName = "seam");
        services.AddCaching(b => b.AddEventFormat("b", 0x12, _ => formatB, write: false), o => o.AppShortName = "seam");

        using var provider = services.BuildServiceProvider();
        var composite = provider.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();
        var original = Event();

        composite.Should().BeOfType<CompositeEventFormatter>();
        composite.Decode(formatA.Encode(original))!.Id.Should().Be(original.Id);
        composite.Decode(formatB.Encode(original))!.Id.Should().Be(original.Id);
        var innerJson = services.Where(d => d.IsKeyedService && d.ServiceType == typeof(IEventFormatterProxy<ICacheEvent>) && d.ServiceKey is not EventFormat);
        innerJson.Should().HaveCount(withJson ? 1 : 0, "the JSON formatter is wrapped once, and the composite is not wrapped");
        services.Count(d => !d.IsKeyedService && d.ServiceType == typeof(IEventFormatterProxy<ICacheEvent>)).Should().Be(1);
    }

    [Fact]
    public void The_source_matcher_is_picked_by_the_first_byte_per_format_and_a_format_without_one_never_matches()
    {
        var matching = new IdFormatter(0x11, matchesSource: true);
        var plain = new IdFormatter(0x12);
        var composite = new CompositeEventFormatter(new CacheEventFormatter(), [(0x11, "a", matching, true), (0x12, "b", plain, false)], FixedEventWrites.Always, NullLogger.Instance);

        var matcher = composite.CreateSourceMatcher(Source)!;

        matcher.IsFromSource(new byte[] { 0x11, 1 }).Should().BeTrue();
        matcher.IsFromSource(new byte[] { 0x12, 1 }).Should().BeFalse("its format has no matcher, so its bodies are decoded and compared");
        matcher.IsFromSource(Encoding.UTF8.GetBytes("{\"source\":\"urn:node-a\"}")).Should().BeFalse("the JSON formatter has none either");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cloud_events_before_or_after_gives_the_same_composite_with_the_cloud_events_json(bool cloudEventsFirst)
    {
        using var provider = Provider(
            b =>
            {
                if (cloudEventsFirst)
                {
                    b.AddCloudEvents();
                }

                b.AddEventFormat("a", 0x11, _ => new IdFormatter(0x11), write: false);
                if (!cloudEventsFirst)
                {
                    b.AddCloudEvents();
                }
            },
            force: true);
        var formatter = provider.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();
        var @event = provider.GetRequiredService<ICacheEventFactory>().Create("cache", KnownEventTypes.CacheSet, new CacheEventData("the-key", null));

        formatter.Should().BeOfType<CompositeEventFormatter>();
        formatter.Encode(@event).ToArray().Should().Equal(CacheEventFormatterProxy.CreateDefault().Encode(@event).ToArray());
    }

    [Fact]
    public async Task A_json_formatter_registered_by_factory_is_disposed_once_whichever_side_of_the_format_it_is_added()
    {
        var before = new CountingJson();
        var after = new CountingJson();
        var first = Provider(b => { b.Services.AddSingleton<IEventFormatterProxy<ICacheEvent>>(_ => before); b.AddEventFormat("a", 0x11, _ => new IdFormatter(0x11), write: false); }, force: true);
        var second = Provider(b => { b.AddEventFormat("a", 0x11, _ => new IdFormatter(0x11), write: false); b.Services.AddSingleton<IEventFormatterProxy<ICacheEvent>>(_ => after); }, force: true);
        _ = first.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();
        _ = second.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();

        await first.DisposeAsync();
        await second.DisposeAsync();

        before.Disposals.Should().Be(1);
        after.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task A_format_is_built_once_and_disposed_with_the_provider()
    {
        var made = new List<IdFormatter>();
        var provider = Provider(
            b => b.AddEventFormat(
                "a",
                0x11,
                _ =>
                {
                    var formatter = new IdFormatter(0x11);
                    made.Add(formatter);
                    return formatter;
                },
                write: true),
            force: true);
        _ = provider.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();
        _ = provider.GetRequiredService<IEventFormatterProxy<ICacheEvent>>();

        await provider.DisposeAsync();

        made.Should().ContainSingle().Which.Disposals.Should().Be(1);
    }

    private static EventFormatRegistry Registry(CachingBuilder builder) =>
        (EventFormatRegistry)builder.Services.Single(d => d.ServiceType == typeof(EventFormatRegistry)).ImplementationInstance!;

    private static CacheEvent Event() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = KnownEventTypes.CacheSet,
        Source = Source,
        Data = new CacheEventData("the-key", null),
    };

    private static ServiceProvider Provider(Action<ICachingBuilder> configure, bool force = false) =>
        new ServiceCollection()
            .AddLogging()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = "127.0.0.1:1");
                    b.AddRedis(o => o.FormatNegotiation.Mode = force ? FormatNegotiationMode.Force : FormatNegotiationMode.Auto);
                    b.AddBroadcast();
                    b.AddInMemoryRedis();
                    configure(b);
                },
                o =>
                {
                    o.AppShortName = "seam";
                    o.SourceUri = Source;
                })
            .BuildServiceProvider();

    private sealed class IdFormatter(byte id, byte? startWith = null, bool matchesSource = false) : IEventFormatterProxy<ICacheEvent>, IEventSourceMatcherProvider, IDisposable
    {
        private readonly CacheEventFormatter _json = new();
        private int _disposals;

        public int Disposals => Volatile.Read(ref _disposals);

        public List<byte> Offered { get; } = [];

        public ICacheEvent? Decode(ReadOnlyMemory<byte> body)
        {
            Offered.Add(body.IsEmpty ? (byte)0 : body.Span[0]);
            return body.IsEmpty || body.Span[0] != id ? null : _json.Decode(body[1..]);
        }

        public ReadOnlyMemory<byte> Encode(ICacheEvent @event) =>
            new[] { startWith ?? id }.Concat(_json.Encode(@event).ToArray()).ToArray();

        public IEventSourceMatcher? CreateSourceMatcher(Uri source) => matchesSource ? new AlwaysMatches() : null;

        public void Dispose() => Interlocked.Increment(ref _disposals);
    }

    private sealed class ThrowingFormatter(Exception? error = null) : IEventFormatterProxy<ICacheEvent>
    {
        public ICacheEvent? Decode(ReadOnlyMemory<byte> body) => null;

        public ReadOnlyMemory<byte> Encode(ICacheEvent @event) => throw error ?? new InvalidOperationException("unsupported value");
    }

    private sealed class AlwaysMatches : IEventSourceMatcher
    {
        public bool IsFromSource(ReadOnlyMemory<byte> payload) => true;
    }

    private sealed class EmptyFormatter : IEventFormatterProxy<ICacheEvent>
    {
        public ICacheEvent? Decode(ReadOnlyMemory<byte> body) => null;

        public ReadOnlyMemory<byte> Encode(ICacheEvent @event) => default;
    }

    private sealed class RecordingJson : IEventFormatterProxy<ICacheEvent>
    {
        public List<byte[]> Bodies { get; } = [];

        public ICacheEvent? Decode(ReadOnlyMemory<byte> body)
        {
            Bodies.Add(body.ToArray());
            return null;
        }

        public ReadOnlyMemory<byte> Encode(ICacheEvent @event) => Encoding.UTF8.GetBytes("{}");
    }

    private sealed class CountingJson : IEventFormatterProxy<ICacheEvent>, IDisposable
    {
        private int _disposals;

        public int Disposals => Volatile.Read(ref _disposals);

        public ICacheEvent? Decode(ReadOnlyMemory<byte> body) => null;

        public ReadOnlyMemory<byte> Encode(ICacheEvent @event) => Encoding.UTF8.GetBytes("{}");

        public void Dispose() => Interlocked.Increment(ref _disposals);
    }
}
