using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
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

            using var sut = new RedisStreamSubjectWriter<ICacheEvent>(
                context,
                connectionState,
                connector,
                channel.Writer,
                formatter,
                NullLogger.Instance,
                NullTelemetryProvider.Instance,
                NullRedisProfiler.Instance,
                new TimedFetchWaiter(pollInterval),
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

    private RedisConnector NewConnector()
    {
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = fixture.ConnectionString, EnableHangDetection = false });
        var optionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options);
        var factory = new ConnectionMultiplexerFactory(options, NullRedisProfiler.Instance);
        return new RedisConnector(NullTelemetryProvider.Instance, optionsProvider, factory, options);
    }
}
