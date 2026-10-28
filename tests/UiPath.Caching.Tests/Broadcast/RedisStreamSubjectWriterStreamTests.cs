using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using UiPath.Caching.Telemetry;
using static UiPath.Caching.Tests.Broadcast.InMemoryStreamPort;

namespace UiPath.Caching.Tests.Broadcast;

/// <summary>The writer on an in-memory stream: each step waits for the operation it follows, not for time.</summary>
public sealed class RedisStreamSubjectWriterStreamTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _stop = new();
    private readonly RedisStreamContext _context = new("topic", "field", "consumer", "group", new Uri("urn:self"), 100, TimeSpan.FromMilliseconds(50), false, true);
    private readonly InMemoryStreamPort _stream = new("group", "field");
    private readonly IRedisConnector _redis = Substitute.For<IRedisConnector>();
    private readonly Channel<ICacheEvent> _events = Channel.CreateUnbounded<ICacheEvent>();
    private readonly TaskCompletionSource _missed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _stop.Dispose();
    }

    [Fact]
    public async Task Entries_added_are_dispatched_and_acknowledged()
    {
        var made = _stream.Hold(Op.CreateGroup);
        using var sut = Create();
        await Reached(made);
        made.Release();
        (await WaitUntil(() => _stream.HasGroup)).Should().BeTrue();

        var acknowledging = _stream.Hold(Op.Acknowledge);
        _stream.Add("a");

        (await _events.Reader.ReadAsync(_stop.Token).AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken)).Should().NotBeNull();
        await Reached(acknowledging);
        acknowledging.Release();

        (await WaitUntil(() => _stream.Acknowledged.Length == 1)).Should().BeTrue();
        _stream.Acknowledged.Should().Equal("1-0");
    }

    [Fact]
    public async Task A_group_lost_while_in_use_is_recreated_where_it_left_off_and_reported_once()
    {
        var made = _stream.Hold(Op.CreateGroup);
        using var sut = Create();
        await Reached(made);
        made.Release();
        (await WaitUntil(() => _stream.HasGroup)).Should().BeTrue();

        // Subscribed once the group is known, so the first create is not a loss.
        var acknowledging = _stream.Hold(Op.Acknowledge);
        _stream.Add("a");
        await Reached(acknowledging);
        sut.MarkSubscribed();
        acknowledging.Release();

        var recreating = _stream.Hold(Op.CreateGroup);
        _stream.DropGroup();
        await Reached(recreating);
        recreating.Release();

        await _missed.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        (await WaitUntil(() => _stream.HasGroup)).Should().BeTrue("the reader made the group again");
        _stream.LastDelivered.Should().Be("1-0", "from the last read, so what came since is replayed, not skipped");
        _stream.Calls(Op.CreateGroup).Should().Be(2);
    }

    [Fact]
    public async Task Entries_trimmed_before_the_group_read_them_are_reported()
    {
        var made = _stream.Hold(Op.CreateGroup);
        using var sut = Create();
        await Reached(made);
        made.Release();
        (await WaitUntil(() => _stream.HasGroup)).Should().BeTrue();

        // Subscribed once the group is known, so the first create is not a loss.
        var acknowledging = _stream.Hold(Op.Acknowledge);
        _stream.Add("a");
        await Reached(acknowledging);
        sut.MarkSubscribed();
        acknowledging.Release();

        // The next read starts after these: the stream it checks has lost 2-0 undelivered.
        var reading = _stream.Hold(Op.Read);
        await Reached(reading);
        _stream.Add("b");
        _stream.Add("c");
        _stream.Trim(2);
        reading.Release();

        await _missed.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_recovery_while_a_read_is_in_flight_is_neither_waited_out_nor_counted()
    {
        _stream.FailWith(Op.Read, new RedisException("down"));
        var waiter = Track(new ObservedWaiter(new SignalingFetchWaiter(TimeSpan.FromSeconds(5))));
        var read = _stream.Hold(Op.Read);
        using var sut = Create(waiter, TimeSpan.FromSeconds(5));

        for (var i = 1; i <= 8; i++)
        {
            await Reached(read);
            var failing = read;
            read = _stream.Hold(Op.Read);
            if (i == 4)
            {
                // The fourth failure would have been the first to back off, had the outage's streak gone on.
                _redis.OnConnectionRestored += Raise.Event<EventHandler>(_redis, EventArgs.Empty);
            }

            failing.Release();
            if (i is not 4 and not 8)
            {
                waiter.Signal();
            }
        }

        // Three failures of the new streak went at the poll's pace; the fourth backs off, and a ring does not end that.
        await waiter.LongWait();
        waiter.Signal();
        await waiter.LongWait();
        read.Reached.IsCompleted.Should().BeFalse("the ring woke the wait, and the backoff went back to waiting");
        waiter.LongWaits.Should().Equal([7, 8], "the backoff began on the seventh wait and the ring made it wait again");
    }

    [Fact]
    public async Task A_recovery_landing_after_the_check_but_before_the_count_still_ends_the_wait_and_starts_a_new_streak()
    {
        _stream.FailWith(Op.Read, new RedisException("down"));
        var waiter = Track(new ObservedWaiter(new SignalingFetchWaiter(TimeSpan.FromSeconds(5))));
        // The writer calls the waiter after it counted the failure: the recovery lands past the check and the count.
        waiter.BeforeWait = (ordinal, _) =>
        {
            if (ordinal == 4)
            {
                _redis.OnConnectionRestored += Raise.Event<EventHandler>(_redis, EventArgs.Empty);
            }
        };
        var read = _stream.Hold(Op.Read);
        using var sut = Create(waiter, TimeSpan.FromSeconds(5));

        for (var i = 1; i <= 8; i++)
        {
            // The fourth failure's backoff ends with no ring of the test's; the next read is reached all the same.
            await Reached(read);
            var failing = read;
            read = _stream.Hold(Op.Read);
            failing.Release();
            if (i is not 4 and not 8)
            {
                waiter.Signal();
            }
        }

        // The new streak went at the poll's pace for three failures; its fourth, the eighth wait, is the next to back off.
        await waiter.LongWait();
        await waiter.LongWait();
        waiter.LongWaits.Should().Equal(4, 8);
        read.Reached.IsCompleted.Should().BeFalse("the new streak's fourth failure backs off");
    }

    [Fact]
    public async Task A_recovery_during_a_backoff_ends_it()
    {
        _stream.FailWith(Op.Read, new RedisException("down"));
        var waiter = Track(new ObservedWaiter(new SignalingFetchWaiter(TimeSpan.FromSeconds(5))));
        var read = _stream.Hold(Op.Read);
        using var sut = Create(waiter, TimeSpan.FromSeconds(5));

        for (var i = 1; i <= 4; i++)
        {
            await Reached(read);
            var failing = read;
            read = _stream.Hold(Op.Read);
            failing.Release();
            if (i < 4)
            {
                waiter.Signal();
            }
        }

        await waiter.LongWait();
        read.Reached.IsCompleted.Should().BeFalse("the fourth failure backs off");
        waiter.LongWaits.Should().Equal(4);

        _redis.OnConnectionRestored += Raise.Event<EventHandler>(_redis, EventArgs.Empty);
        await Reached(read);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private static Task Reached(Held held) => held.Reached.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

    private ObservedWaiter Track(ObservedWaiter waiter)
    {
        _disposables.Add(waiter);
        return waiter;
    }

    private RedisStreamSubjectWriter<ICacheEvent> Create(ObservedWaiter? waiter = null, TimeSpan? pollInterval = null)
    {
        var connectionState = Substitute.For<IConnectionState>();
        connectionState.IsConnected.Returns(true);
        var formatter = Substitute.For<IEventFormatterProxy<ICacheEvent>>();
        formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(_ => new TestCacheEvent { Valid = true, Source = new Uri("urn:other") });
        var poll = pollInterval ?? _context.PollInterval;
        var fetchWaiter = waiter ?? Track(new ObservedWaiter(new SignalingFetchWaiter(poll)));
        return new RedisStreamSubjectWriter<ICacheEvent>(
            _context with { PollInterval = poll },
            connectionState,
            _redis,
            _events.Writer,
            formatter,
            Substitute.For<ILogger>(),
            Substitute.For<ICachingTelemetryProvider>(),
            Substitute.For<IRedisProfiler>(),
            fetchWaiter,
            () => _missed.TrySetResult(),
            _stop.Token,
            _stream);
    }

    /// <summary>Wraps a waiter to tell when a wait longer than the poll, a backoff, begins, and on which wait.</summary>
    private sealed class ObservedWaiter(SignalingFetchWaiter inner) : IFetchWaiter
    {
        private readonly SemaphoreSlim _longWait = new(0);
        private readonly List<int> _longWaits = [];
        private int _waits;

        /// <summary>Runs on the writer's thread at the start of each timed wait, with its number and timeout, before the inner waiter is called.</summary>
        public Action<int, TimeSpan>? BeforeWait { get; set; }

        /// <summary>The numbers of the timed waits longer than the poll, in the order they began.</summary>
        public IReadOnlyList<int> LongWaits
        {
            get
            {
                lock (_longWaits)
                {
                    return [.. _longWaits];
                }
            }
        }

        /// <summary>Completes when a wait longer than the poll has begun that was not yet awaited; fails if none begins in time.</summary>
        public async Task LongWait() =>
            (await _longWait.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken)).Should().BeTrue("a wait longer than the poll should have begun");

        public Task WaitAsync(CancellationToken cancellationToken) => inner.WaitAsync(cancellationToken);

        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var ordinal = Interlocked.Increment(ref _waits);
            BeforeWait?.Invoke(ordinal, timeout);
            if (timeout > TimeSpan.FromSeconds(6))
            {
                lock (_longWaits)
                {
                    _longWaits.Add(ordinal);
                }

                _longWait.Release();
            }

            return inner.WaitAsync(timeout, cancellationToken);
        }

        public void Signal() => inner.Signal();

        public void Dispose()
        {
            _longWait.Dispose();
            inner.Dispose();
        }
    }
}
