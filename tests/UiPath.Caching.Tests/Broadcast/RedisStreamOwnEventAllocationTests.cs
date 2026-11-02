using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Broadcast;

/// <summary>What a node's own entries cost its reader, per entry, over a 1000-entry backlog read on an in-memory stream.</summary>
[Collection(AllocationCollection.Name)]
public sealed class RedisStreamOwnEventAllocationTests(ITestOutputHelper output)
{
    private const int Entries = 1000;
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FarPoll = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Own_entries_cost_per_entry(bool withSourceField)
    {
        var context = new RedisStreamContext("topic", "event", "consumer", "group", new Uri("urn:self"), 100, FarPoll, false, false);
        var stream = new InMemoryStreamPort("group", "event");
        var redis = Substitute.For<IRedisConnector>();
        redis.Version.Returns(Version.Parse("7.0.0"));
        var connectionState = Substitute.For<IConnectionState>();
        connectionState.IsConnected.Returns(true);
        var formatter = new CountingFormatter(new TestCacheEventFormatterProxy());
        var channel = Channel.CreateUnbounded<ICacheEvent>();
        using var stop = new CancellationTokenSource();
        using var waiter = new SignalingFetchWaiter(FarPoll);
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
            stop.Token,
            stream,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        // The first read finds nothing and parks the reader until the poll or a signal.
        SpinWait.SpinUntil(() => stream.HasGroup && stream.Calls(InMemoryStreamPort.Op.Read) >= 1, WaitTimeout).Should().BeTrue();
        for (var i = 0; i < Entries; i++)
        {
            stream.Add(Payload(i), withSourceField ? "urn:self" : null);
        }

        var before = GC.GetTotalAllocatedBytes(precise: true);
        waiter.Signal();
        SpinWait.SpinUntil(() => stream.AcknowledgedCount == Entries, WaitTimeout).Should().BeTrue("every own entry is acknowledged");
        var perEntry = (GC.GetTotalAllocatedBytes(precise: true) - before) / Entries;

        output.WriteLine($"{(withSourceField ? "with" : "without")} the source field: {perEntry} bytes per own entry, {formatter.Decodes} decodes");
        stop.Cancel();
        channel.Reader.TryRead(out _).Should().BeFalse("own entries are not dispatched");
        stream.LastDelivered.Should().Be($"{Entries}-0");
        if (withSourceField)
        {
            formatter.Decodes.Should().Be(0, "an entry that names this node as its source is dropped before any decode");
            // The in-memory stream builds the reply tree the script path parses, as the client would off the wire; a decode alone is about 1.7 KB more.
            perEntry.Should().BeLessThan(1536, "an own entry costs its parse and its acknowledgement, not a decode (measured {0} bytes)", perEntry);
        }
        else
        {
            formatter.Decodes.Should().Be(Entries, "an entry without the field is decoded to find its source");
        }
    }

    private static string Payload(int i) => JsonSerializer.Serialize(new TestCacheEvent
    {
        Id = Guid.NewGuid().ToString(),
        Type = "UiPath.Caching.Set",
        Source = new Uri("urn:self"),
        Data = new CacheEventData($"tenant:{i}:user:{i}", new Dictionary<string, object?> { ["cache"] = "users", ["marker"] = "1" }),
    });

    private sealed class CountingFormatter(IEventFormatterProxy<ICacheEvent> inner) : IEventFormatterProxy<ICacheEvent>
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
}
