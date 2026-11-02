using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.Broadcast;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class RedisStreamSubjectWriterIntegrationTests(RedisContainerFixture fixture)
{
    [Fact]
    public async Task Entries_of_a_read_whose_answer_was_lost_are_dispatched_and_acknowledged()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");

        RedisKey stream = $"it:pending:{Guid.NewGuid():N}";
        RedisValue group = "group";
        RedisValue consumer = "consumer";
        RedisValue field = "event";
        using var connector = NewConnector();
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        var db = connector.Database;
        using var stop = new CancellationTokenSource();

        try
        {
            await db.StreamCreateConsumerGroupAsync(stream, group, StreamPosition.NewMessages, createStream: true);
            await db.StreamAddAsync(stream, field, "first");
            await db.StreamAddAsync(stream, field, "second");
            var deleted = await db.StreamAddAsync(stream, field, "deleted");
            // A read whose answer never reached the client: Redis moved the group on and made the entries pending.
            (await db.StreamReadGroupAsync(stream, group, consumer, StreamConstants.UndeliveredMessages, 10)).Should().HaveCount(3);
            await db.StreamDeleteAsync(stream, [deleted]);

            var formatter = Substitute.For<IEventFormatterProxy<ICacheEvent>>();
            formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>())
                .Returns(call => call.Arg<ReadOnlyMemory<byte>>().IsEmpty ? null : new TestCacheEvent { Valid = true, Source = new Uri("urn:other-source") });
            var connectionState = Substitute.For<IConnectionState>();
            connectionState.IsConnected.Returns(true);
            var channel = Channel.CreateUnbounded<ICacheEvent>();
            var pollInterval = TimeSpan.FromMilliseconds(50);
            var context = new RedisStreamContext(stream, field, consumer, group, new Uri("urn:this-source"), 10, pollInterval, false, false);

            using var waiter = new SignalingFetchWaiter(pollInterval);
            using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
                context,
                connectionState,
                connector,
                channel.Writer,
                formatter,
                NullLogger.Instance,
                NullTelemetryProvider.Instance,
                NullRedisProfiler.Instance,
                waiter,
                () => { },
                stop.Token);

            for (var i = 0; i < 2; i++)
            {
                await channel.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((await db.StreamPendingAsync(stream, group)).PendingMessageCount > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            (await db.StreamPendingAsync(stream, group)).PendingMessageCount.Should().Be(0, "the deleted entry is acknowledged too");
        }
        finally
        {
            await stop.CancelAsync();
            await db.KeyDeleteAsync(stream);
        }
    }

    [Fact]
    public async Task Reads_of_a_subscribed_writer_run_the_script_only_on_Redis_7_and_later()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");

        RedisKey stream = $"it:script:{Guid.NewGuid():N}";
        RedisValue group = "group";
        RedisValue field = "event";
        using var connector = NewConnector();
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        var db = connector.Database;
        await using var admin = await ConnectionMultiplexer.ConnectAsync($"{fixture.ConnectionString},allowAdmin=true");
        using var stop = new CancellationTokenSource();

        try
        {
            await db.StreamCreateConsumerGroupAsync(stream, group, StreamPosition.NewMessages, createStream: true);
            var formatter = Substitute.For<IEventFormatterProxy<ICacheEvent>>();
            formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>())
                .Returns(call => call.Arg<ReadOnlyMemory<byte>>().IsEmpty ? null : new TestCacheEvent { Valid = true, Source = new Uri("urn:other-source") });
            var connectionState = Substitute.For<IConnectionState>();
            connectionState.IsConnected.Returns(true);
            var channel = Channel.CreateUnbounded<ICacheEvent>();
            var pollInterval = TimeSpan.FromMilliseconds(50);
            var context = new RedisStreamContext(stream, field, "consumer", group, new Uri("urn:this-source"), 10, pollInterval, false, false);

            var before = await EvalCallsAsync(admin);
            using var waiter = new SignalingFetchWaiter(pollInterval);
            using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
                context,
                connectionState,
                connector,
                channel.Writer,
                formatter,
                NullLogger.Instance,
                NullTelemetryProvider.Instance,
                NullRedisProfiler.Instance,
                waiter,
                () => { },
                stop.Token);
            sut.MarkSubscribed();

            for (var i = 0; i < 5; i++)
            {
                await db.StreamAddAsync(stream, field, $"entry-{i}");
                await channel.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }

            var scripts = await EvalCallsAsync(admin) - before;
            if (fixture.ServerVersion.Major < 7)
            {
                scripts.Should().Be(0, "the script's gap check has no data before Redis 7.0");
            }
            else
            {
                scripts.Should().BeGreaterThan(0);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await db.KeyDeleteAsync(stream);
        }
    }

    [Fact]
    public async Task An_entry_the_topic_publishes_reads_back_with_the_payload_by_name_and_the_source_beside_it()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");

        using var connector = NewConnector();
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        var db = connector.Database;
        var cacheOptions = new CacheOptions { Enabled = true, AppShortName = $"it{Guid.NewGuid():N}"[..10], SourceUri = new Uri("urn:node") };
        var options = new RedisStreamsTopicOptions { Enabled = true };
        var stream = new PrefixStrategy(RedisKeyspaces.Streams, cacheOptions).GetRedisKey("orders");
        var connectionState = Substitute.For<IConnectionState>();
        connectionState.IsConnected.Returns(true);
        var formatter = new TestCacheEventFormatterProxy();
        using var stop = new CancellationTokenSource();

        try
        {
            using var topic = new RedisStreamsTopic<ICacheEvent>(
                "orders",
                connectionState,
                connector,
                () => new KeyedSubject<ICacheEvent>(NullLogger.Instance),
                formatter,
                new EmptyResiliencePipelineProvider(),
                options,
                cacheOptions,
                NullLogger<RedisStreamsTopic<ICacheEvent>>.Instance,
                NullTelemetryProvider.Instance,
                NullRedisProfiler.Instance,
                stop.Token);
            var ev = new TestCacheEvent { Id = "e1", Type = "set", Source = new Uri("urn:node"), Data = new CacheEventData("order:1") };
            (await topic.PublishAsync(ev, TestContext.Current.CancellationToken)).Should().BeTrue();

            var entries = await db.StreamRangeAsync(stream);
            entries.Should().ContainSingle();
            var entry = entries[0];
            entry.Values.Select(v => v.Name.ToString()).Should().Equal([options.FieldName, "src"], "the payload first, under its configured name, then the source");
            entry["src"].ToString().Should().Be("urn:node");
            // What a 3.0 reader does with the entry: the payload by its field name, decoded; the client parses the field it does not know like any other.
            var decoded = formatter.Decode((ReadOnlyMemory<byte>)entry[options.FieldName]);
            decoded.Should().NotBeNull();
            decoded!.Id.Should().Be("e1");
            decoded.Key.Should().Be("order:1");
        }
        finally
        {
            await stop.CancelAsync();
            await db.KeyDeleteAsync(stream);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Own_entries_are_acknowledged_without_a_decode_and_the_others_dispatched(bool scripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipWhen(scripts && fixture.ServerVersion.Major < 7, "The script read needs Redis 7.0.");

        RedisKey stream = $"it:own:{Guid.NewGuid():N}";
        RedisValue group = "group";
        RedisValue field = "event";
        using var connector = NewConnector();
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        var db = connector.Database;
        IRedisConnector redis = connector;
        if (!scripts)
        {
            // Reported below 7.0, the reader sends the check and the read apart instead of the script.
            var below7 = Substitute.For<IRedisConnector>();
            below7.Database.Returns(db);
            below7.Version.Returns(Version.Parse("6.2.0"));
            below7.IsConnected.Returns(true);
            redis = below7;
        }

        using var stop = new CancellationTokenSource();

        try
        {
            await db.StreamCreateConsumerGroupAsync(stream, group, StreamPosition.NewMessages, createStream: true);
            var decoded = new ConcurrentQueue<string>();
            var formatter = Substitute.For<IEventFormatterProxy<ICacheEvent>>();
            formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(call =>
            {
                var text = Encoding.UTF8.GetString(call.Arg<ReadOnlyMemory<byte>>().Span);
                decoded.Enqueue(text);
                return new TestCacheEvent { Id = text, Valid = true, Source = new Uri(text == "own-old" ? "urn:this-source" : "urn:other-source") };
            });
            var connectionState = Substitute.For<IConnectionState>();
            connectionState.IsConnected.Returns(true);
            var channel = Channel.CreateUnbounded<ICacheEvent>();
            var pollInterval = TimeSpan.FromMilliseconds(50);
            var context = new RedisStreamContext(stream, field, "consumer", group, new Uri("urn:this-source"), 10, pollInterval, false, false);
            using var waiter = new SignalingFetchWaiter(pollInterval);
            using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
                context,
                connectionState,
                redis,
                channel.Writer,
                formatter,
                NullLogger.Instance,
                NullTelemetryProvider.Instance,
                NullRedisProfiler.Instance,
                waiter,
                () => { },
                stop.Token);
            sut.MarkSubscribed();

            await db.StreamAddAsync(stream, [new NameValueEntry(field, "own-new"), new NameValueEntry("src", "urn:this-source")]);
            await db.StreamAddAsync(stream, [new NameValueEntry(field, "theirs-new"), new NameValueEntry("src", "urn:other-source")]);
            await db.StreamAddAsync(stream, field, "own-old");
            var last = await db.StreamAddAsync(stream, field, "theirs-old");

            (await NextAsync(channel)).Id.Should().Be("theirs-new");
            (await NextAsync(channel)).Id.Should().Be("theirs-old");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && ((await db.StreamPendingAsync(stream, group)).PendingMessageCount > 0 || await LastDeliveredAsync(db, stream, group) != last))
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            (await db.StreamPendingAsync(stream, group)).PendingMessageCount.Should().Be(0, "the own entries are acknowledged too");
            (await LastDeliveredAsync(db, stream, group)).Should().Be(last);
            decoded.Should().BeEquivalentTo(["theirs-new", "own-old", "theirs-old"], "an entry that names this node is dropped unread; one without the field is decoded to tell");
            channel.Reader.TryRead(out _).Should().BeFalse();
        }
        finally
        {
            await stop.CancelAsync();
            await db.KeyDeleteAsync(stream);
        }
    }

    private static Task<ICacheEvent> NextAsync(Channel<ICacheEvent> channel) =>
        channel.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private static async Task<RedisValue> LastDeliveredAsync(IDatabase db, RedisKey stream, RedisValue group) =>
        Array.Find(await db.StreamGroupInfoAsync(stream), g => g.Name == group).LastDeliveredId;

    private static async Task<long> EvalCallsAsync(ConnectionMultiplexer admin)
    {
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        var commands = (await server.InfoAsync("commandstats")).SelectMany(group => group);
        return commands
            .Where(pair => pair.Key.StartsWith("cmdstat_eval", StringComparison.Ordinal))
            .Sum(pair => long.Parse(pair.Value.Split("calls=")[1].Split(',')[0], System.Globalization.CultureInfo.InvariantCulture));
    }

    private RedisConnector NewConnector()
    {
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = fixture.ConnectionString, EnableHangDetection = false });
        var optionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options);
        var factory = new ConnectionMultiplexerFactory(options, NullRedisProfiler.Instance);
        return new RedisConnector(NullTelemetryProvider.Instance, optionsProvider, factory, options);
    }
}
