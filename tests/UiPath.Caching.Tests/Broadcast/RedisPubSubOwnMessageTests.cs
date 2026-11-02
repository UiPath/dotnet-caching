using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CloudNative.CloudEvents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.CloudEvents;

namespace UiPath.Caching.Tests.Broadcast;

/// <summary>A node's own Pub/Sub messages are dropped before they are decoded, and only its own.</summary>
public sealed class RedisPubSubOwnMessageTests
{
    private static readonly Uri Self = new("urn:self");
    private static readonly RedisChannel Channel = RedisChannel.Literal("topic");

    public static TheoryData<string> Sources() =>
    [
        "urn:self", "urn:a+b&c", "urn:café:🙂", "urn:Self", "URN:self", "urn:self2", "https://host.example/a b", "https://Host.Example/a%20b",
        "https://host.example/%7Euser", "HTTPS://Host.Example:443/a/../b", "relative/path", "/abs/path", "  urn:padded  ", "urn:<x>'\"\\",
    ];

    [Fact]
    public async Task An_own_message_is_dropped_without_a_decode()
    {
        var formatter = new CountingFormatter(new CacheEventFormatter());
        await using var node = await Node.StartAsync(formatter, Self);

        node.Receive(Encode(Event("urn:self")));

        formatter.Decodes.Should().Be(0);
        node.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task A_foreign_message_is_decoded_and_applied()
    {
        var formatter = new CountingFormatter(new CacheEventFormatter());
        await using var node = await Node.StartAsync(formatter, Self);

        node.Receive(Encode(Event("urn:other")));

        formatter.Decodes.Should().Be(1);
        node.Received.Should().ContainSingle().Which.Key.Should().Be("k");
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task A_message_is_dropped_exactly_when_SameSource_says_it_is_this_nodes(string payloadSource)
    {
        foreach (var self in new[] { Self, new Uri("urn:a+b&c"), new Uri("urn:café:🙂") })
        {
            var formatter = new CountingFormatter(new CacheEventFormatter());
            await using var sut = await Node.StartAsync(formatter, self);
            var ev = Event(payloadSource);

            sut.Receive(Encode(ev));

            var own = ev.SameSource(self);
            sut.Received.Should().HaveCount(own ? 0 : 1, "{0} as seen by {1}", payloadSource, self);
            if (payloadSource == self.OriginalString)
            {
                formatter.Decodes.Should().Be(0, "the payload names {0} as this node writes it", self);
            }
        }
    }

    [Fact]
    public void The_default_formatter_matches_only_what_SameSource_does()
    {
        var formatter = new CacheEventFormatter();
        AssertMatchesSameSource(source => formatter.CreateSourceMatcher(source), source => formatter.Encode(Event(source)), formatter.Decode);
    }

    [Fact]
    public void The_cloud_events_formatter_matches_only_what_SameSource_does()
    {
        var formatter = CacheEventFormatterProxy.CreateDefault();
        AssertMatchesSameSource(
            source => formatter.CreateSourceMatcher(source),
            source => formatter.Encode(new CacheCloudEventWrapper("1", KnownEventTypes.CacheSet, new Uri(source, UriKind.RelativeOrAbsolute), new CacheEventData("k"))),
            formatter.Decode);
    }

    [Fact]
    public void An_escaped_source_in_the_payload_matches_its_unescaped_text()
    {
        var matcher = new CacheEventFormatter().CreateSourceMatcher(new Uri("urn:a+b&c"))!;
        var written = Encode(Event("urn:a+b&c"));
        Encoding.UTF8.GetString(written).Should().Contain("""urn:a\u002Bb\u0026c""", "the default encoder escapes + and &");
        matcher.IsFromSource(written).Should().BeTrue();

        matcher.IsFromSource("""{"Id":"1","Source":"urn:a\u002Bb\u0026c","Data":{"Key":"k"},"Type":"t"}"""u8.ToArray()).Should().BeTrue();
        matcher.IsFromSource("""{"Id":"1","Source":"urn:\u0061+b&c","Data":{"Key":"k"},"Type":"t"}"""u8.ToArray()).Should().BeTrue();
        matcher.IsFromSource("""{"Id":"1","\u0053ource":"urn:a+b&c","Data":{"Key":"k"},"Type":"t"}"""u8.ToArray()).Should().BeTrue();
        matcher.IsFromSource("""{"Id":"1","Source":"urn:a+b&cd","Data":{"Key":"k"},"Type":"t"}"""u8.ToArray()).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"Id":"1","Data":{"Key":"k","Properties":{"Source":"urn:self"}},"Type":"t"}""", false, "a nested Source is not the event's")]
    [InlineData("""{"Id":"1","Data":{"Key":"k"},"Type":"t"}""", false, "no Source")]
    [InlineData("""{"Id":"1","Source":null,"Data":{"Key":"k"},"Type":"t"}""", false, "a null Source")]
    [InlineData("""{"Id":"1","source":"urn:self","Data":{"Key":"k"},"Type":"t"}""", false, "the decoder binds Source case-sensitively")]
    [InlineData("""{"Id":"1","Source":"urn:self","Source":"urn:other","Data":{"Key":"k"},"Type":"t"}""", false, "the last Source wins the decode")]
    [InlineData("""{"Id":"1","Source":"urn:other","Source":"urn:self","Data":{"Key":"k"},"Type":"t"}""", false, "a duplicate is never taken as own")]
    [InlineData("""{"Id":"1","Source":"urn:self","Data":{"Key":"k"},"Type":"t"}{}""", false, "trailing content")]
    [InlineData("""{"Id":"1","Source":"urn:self","Data":{"Key":"k"}""", false, "truncated")]
    [InlineData("""["urn:self"]""", false, "not an object")]
    [InlineData("", false, "empty")]
    [InlineData("""{"Id":"1","Data":{"Key":"k","Properties":{"a":[1,{"b":null}]}},"Type":"t","Source":"urn:self"}""", true, "Source after Data is found where it is")]
    public void Only_a_single_top_level_Source_that_is_this_nodes_matches(string payload, bool expected, string because)
    {
        var matcher = new CacheEventFormatter().CreateSourceMatcher(Self)!;

        matcher.IsFromSource(Encoding.UTF8.GetBytes(payload)).Should().Be(expected, because);
    }

    [Fact]
    public async Task A_message_whose_Source_is_after_Data_is_dropped_only_when_it_is_this_nodes()
    {
        var formatter = new CountingFormatter(new CacheEventFormatter());
        await using var node = await Node.StartAsync(formatter, Self);

        node.Receive("""{"Id":"1","Data":{"Key":"k"},"Type":"t","Source":"urn:self"}"""u8.ToArray());
        node.Receive("""{"Id":"2","Data":{"Key":"k"},"Type":"t","Source":"urn:other"}"""u8.ToArray());
        node.Receive("""{"Id":"3","Source":"urn:self","Data":{"Key":"k"},"Type":"t","Source":"urn:other"}"""u8.ToArray());

        formatter.Decodes.Should().Be(2);
        node.Received.Select(e => e.Id).Should().Equal("2", "3");
    }

    [Fact]
    public async Task A_message_without_a_Source_is_decoded()
    {
        var formatter = new CountingFormatter(new CacheEventFormatter());
        await using var node = await Node.StartAsync(formatter, Self);

        node.Receive("""{"Id":"1","Data":{"Key":"k"},"Type":"t"}"""u8.ToArray());

        formatter.Decodes.Should().Be(1);
        node.Received.Should().BeEmpty("an event without a source is invalid");
    }

    [Fact]
    public async Task A_formatter_without_the_capability_decodes_an_own_message()
    {
        var formatter = new PlainCountingFormatter(new CacheEventFormatter());
        await using var node = await Node.StartAsync(formatter, Self);

        node.Receive(Encode(Event("urn:self")));

        formatter.Decodes.Should().Be(1);
        node.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task With_debug_logs_on_an_own_message_is_decoded_and_logged_with_its_id()
    {
        var formatter = new CountingFormatter(new CacheEventFormatter());
        var logger = new RecordingLogger();
        await using var node = await Node.StartAsync(formatter, Self, logger);
        var ev = Event("urn:self");

        node.Receive(Encode(ev));

        formatter.Decodes.Should().Be(1);
        node.Received.Should().BeEmpty();
        logger.Messages.Should().Contain(m => m.Contains("Event from current source") && m.Contains(ev.Id!));
    }

    [Fact]
    public void A_cloud_events_formatter_that_is_not_json_offers_no_matcher()
    {
        new CacheEventFormatterProxy(Substitute.For<CloudEventFormatter>()).CreateSourceMatcher(Self).Should().BeNull();
    }

    private static void AssertMatchesSameSource(Func<Uri, IEventSourceMatcher?> create, Func<string, ReadOnlyMemory<byte>> encode, Func<ReadOnlyMemory<byte>, ICacheEvent?> decode)
    {
        var sources = Sources().Select(row => (string)row.Data).ToArray();
        var failures = new List<string>();
        foreach (var self in sources)
        {
            var node = new Uri(self, UriKind.RelativeOrAbsolute);
            var matcher = create(node);
            matcher.Should().NotBeNull("{0} can be matched", self);
            foreach (var other in sources)
            {
                var payload = encode(other);
                var matched = matcher!.IsFromSource(payload);
                var same = decode(payload)!.SameSource(node);
                if (matched && !same)
                {
                    failures.Add($"{other} matched {self} but SameSource says no");
                }

                if (other == self && !matched)
                {
                    failures.Add($"{other} as written did not match itself");
                }
            }
        }

        failures.Should().BeEmpty();
    }

    private static CacheEvent Event(string source) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = "UiPath.Caching.Set",
        Source = new Uri(source, UriKind.RelativeOrAbsolute),
        Data = new CacheEventData("k", new Dictionary<string, object?> { ["cache"] = "users", ["marker"] = "1" }),
    };

    private static byte[] Encode(CacheEvent ev) => new CacheEventFormatter().Encode(ev).ToArray();

    /// <summary>A writer subscribed to a substitute channel, with its handler captured so a test hands it messages in turn.</summary>
    internal sealed class Node : IAsyncDisposable
    {
        private readonly RedisPubSubSubjectWriter<ICacheEvent> _sut;
        private readonly Action<RedisChannel, RedisValue> _handler;
        private readonly Channel<ICacheEvent> _channel;

        private Node(RedisPubSubSubjectWriter<ICacheEvent> sut, Action<RedisChannel, RedisValue> handler, Channel<ICacheEvent> channel)
        {
            _sut = sut;
            _handler = handler;
            _channel = channel;
        }

        public IReadOnlyList<ICacheEvent> Received
        {
            get
            {
                var received = new List<ICacheEvent>();
                while (_channel.Reader.TryRead(out var ev))
                {
                    received.Add(ev);
                }

                return received;
            }
        }

        public static async Task<Node> StartAsync(IEventFormatterProxy<ICacheEvent> formatter, Uri source, ILogger? logger = null)
        {
            var subscribed = new TaskCompletionSource<Action<RedisChannel, RedisValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var subscriber = Substitute.For<ISubscriber>();
            subscriber.When(x => x.Subscribe(Channel, Arg.Any<Action<RedisChannel, RedisValue>>()))
                .Do(ctx => subscribed.TrySetResult(ctx.Arg<Action<RedisChannel, RedisValue>>()));
            var redis = Substitute.For<IRedisConnector>();
            redis.Subscriber.Returns(subscriber);
            var channel = System.Threading.Channels.Channel.CreateUnbounded<ICacheEvent>();
            var options = new RedisPubSubTopicOptions { SubscriberTimeout = TimeSpan.FromHours(1), SubscriberDueTime = TimeSpan.Zero };
            var sut = new RedisPubSubSubjectWriter<ICacheEvent>(source, Channel, redis, channel.Writer, formatter, options, logger ?? NullLogger.Instance, () => { }, () => { });
            var handler = await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return new Node(sut, handler, channel);
        }

        public void Receive(byte[] payload) => _handler(Channel, payload);

        public ValueTask DisposeAsync()
        {
            _sut.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Counts decodes and offers its inner formatter's matcher.</summary>
    internal sealed class CountingFormatter(CacheEventFormatter inner) : IEventFormatterProxy<ICacheEvent>, IEventSourceMatcherProvider
    {
        private int _decodes;

        public int Decodes => Volatile.Read(ref _decodes);

        public ICacheEvent? Decode(ReadOnlyMemory<byte> body)
        {
            Interlocked.Increment(ref _decodes);
            return inner.Decode(body);
        }

        public ReadOnlyMemory<byte> Encode(ICacheEvent @event) => inner.Encode(@event);

        public IEventSourceMatcher? CreateSourceMatcher(Uri source) => inner.CreateSourceMatcher(source);
    }

    /// <summary>Counts decodes, and cannot tell a source without one, as a custom formatter.</summary>
    internal sealed class PlainCountingFormatter(CacheEventFormatter inner) : IEventFormatterProxy<ICacheEvent>
    {
        private int _decodes;

        public int Decodes => Volatile.Read(ref _decodes);

        public ICacheEvent? Decode(ReadOnlyMemory<byte> body)
        {
            Interlocked.Increment(ref _decodes);
            return inner.Decode(body);
        }

        public ReadOnlyMemory<byte> Encode(ICacheEvent @event) => inner.Encode(@event);
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _messages.Enqueue(formatter(state, exception));
    }
}
