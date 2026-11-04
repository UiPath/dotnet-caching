using System.Text;
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
    // Far enough that only a signal, or the clock the test moves, lets a read go.
    private static readonly TimeSpan FarPoll = TimeSpan.FromSeconds(30);

    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UnixEpoch);
    private readonly CancellationTokenSource _stop = new();
    private readonly RedisStreamContext _context = new("topic", "field", "consumer", "group", new Uri("urn:self"), 100, TimeSpan.FromMilliseconds(50), false, true);
    private readonly InMemoryStreamPort _stream = new("group", "field");
    private readonly IRedisConnector _redis = Substitute.For<IRedisConnector>();
    private readonly Channel<ICacheEvent> _events = Channel.CreateUnbounded<ICacheEvent>();
    private readonly TaskCompletionSource _missed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<IDisposable> _disposables = [];
    private int _missedCount;

    public RedisStreamSubjectWriterStreamTests() => _redis.Version.Returns(Version.Parse("7.0.0"));

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

    [Theory]
    [InlineData("7.0.0")]
    [InlineData("6.2.0")]
    public async Task An_entry_that_names_this_node_as_its_source_is_acknowledged_and_passed_without_a_decode(string version)
    {
        var scripts = version.StartsWith('7');
        _redis.Version.Returns(Version.Parse(version));
        _stream.HasCounts = scripts;
        var formatter = Formatter();
        using var sut = Create(formatter: formatter, context: _context with { EmitStreamReceivedEvent = false });
        (await WaitUntil(() => _stream.HasGroup)).Should().BeTrue();
        sut.MarkSubscribed();
        _stream.Add("own", "urn:self");
        _stream.Add("theirs", "urn:other");
        _stream.Add("old");

        (await Next()).Id.Should().Be("theirs", "another node's entry is decoded and dispatched");
        (await Next()).Id.Should().Be("old", "an entry without the field, a 3.0 node's, is decoded to tell whose it is");
        (await WaitUntil(() => _stream.AcknowledgedCount == 3)).Should().BeTrue();

        _stream.Acknowledged.Should().Equal(["1-0", "2-0", "3-0"], "the own entry is acknowledged like any other");
        _stream.LastDelivered.Should().Be("3-0");
        formatter.Received(2).Decode(Arg.Any<ReadOnlyMemory<byte>>());
        formatter.DidNotReceive().Decode(Arg.Is<ReadOnlyMemory<byte>>(body => Decoded(body, "own")));
        (_stream.ScriptReads > 0).Should().Be(scripts, "the script read and the read sent behind the check both skip the own entry");
    }

    [Fact]
    public async Task An_own_entry_without_the_source_field_is_decoded_then_dropped()
    {
        var formatter = Formatter();
        using var sut = Create(formatter: formatter, context: _context with { EmitStreamReceivedEvent = false });
        (await WaitUntil(() => _stream.HasGroup)).Should().BeTrue();
        _stream.Add("own");
        _stream.Add("theirs");

        (await Next()).Id.Should().Be("theirs", "the own entry, written by a 3.0 node, is found by its decoded source and dropped");
        (await WaitUntil(() => _stream.AcknowledgedCount == 2)).Should().BeTrue();

        formatter.Received(1).Decode(Arg.Is<ReadOnlyMemory<byte>>(body => Decoded(body, "own")));
        _stream.LastDelivered.Should().Be("2-0");
        _events.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_own_entry_whose_source_differs_in_case_is_dropped_once_decoded()
    {
        var formatter = Formatter();
        using var sut = Create(formatter: formatter, context: _context with { EmitStreamReceivedEvent = false });
        (await WaitUntil(() => _stream.HasGroup)).Should().BeTrue();
        _stream.Add("OWN", "urn:SELF");

        (await WaitUntil(() => _stream.AcknowledgedCount == 1)).Should().BeTrue();

        formatter.Received(1).Decode(Arg.Any<ReadOnlyMemory<byte>>());
        _events.Reader.TryRead(out _).Should().BeFalse("the field is not this node's byte for byte, so the decoded source decides, case-insensitively as before");
    }

    [Fact]
    public async Task With_the_receipt_diagnostic_on_an_own_entry_is_decoded_so_its_receipt_names_the_event()
    {
        var formatter = Formatter();
        var telemetry = Substitute.For<ICachingTelemetryProvider>();
        using var sut = Create(formatter: formatter, telemetry: telemetry, context: _context with { EmitStreamReceivedEvent = true });
        (await WaitUntil(() => _stream.HasGroup)).Should().BeTrue();
        _stream.Add("own", "urn:self");

        (await WaitUntil(() => _stream.AcknowledgedCount == 1)).Should().BeTrue();

        formatter.Received(1).Decode(Arg.Any<ReadOnlyMemory<byte>>());
        _events.Reader.TryRead(out _).Should().BeFalse();
        telemetry.Received(1).TrackEvent("Caching.RedisStreamSubjectWriter.DispatchEventsAsync.EventReceived", Arg.Any<TelemetryTags<string>>(), Arg.Any<TelemetryTags<double>>());
    }

    [Fact]
    public async Task A_server_below_7_is_read_without_the_script()
    {
        _redis.Version.Returns(Version.Parse("6.2.0"));
        using var sut = Create();
        (await WaitUntil(() => _stream.HasGroup)).Should().BeTrue();
        sut.MarkSubscribed();
        _stream.Add("a");

        (await _events.Reader.ReadAsync(_stop.Token).AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken)).Should().NotBeNull();
        _stream.ScriptReads.Should().Be(0);
        _stream.Calls(Op.Read).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_server_at_7_is_read_with_the_script()
    {
        using var sut = Create();
        (await WaitUntil(() => _stream.HasGroup)).Should().BeTrue();
        sut.MarkSubscribed();
        _stream.Add("a");

        (await _events.Reader.ReadAsync(_stop.Token).AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken)).Should().NotBeNull();
        _stream.ScriptReads.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Below_7_entries_trimmed_before_a_read_within_the_poll_interval_are_reported_on_that_read()
    {
        var (_, waiter) = await StartBelow7Async();

        // The check found nothing; e1..e5 are added and trimmed to e4, e5 before the doorbell's read, within the poll interval.
        var reading = _stream.Hold(Op.Read);
        waiter.Signal();
        await Reached(reading);
        for (var i = 1; i <= 5; i++)
        {
            _stream.Add($"e{i}");
        }

        _stream.Trim(3);
        reading.Release();

        await _missed.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        (await Next()).Should().NotBeNull("the read behind the check delivers what is left");
        (await Next()).Should().NotBeNull();
        _stream.Calls(Op.Groups).Should().Be(1, "the gap shows against the position this node knows, without asking the server for it");
        _stream.Calls(Op.Info).Should().Be(2, "the check went with the read");
    }

    [Fact]
    public async Task Below_7_the_group_position_is_asked_of_the_server_once_a_poll_interval()
    {
        var (_, waiter) = await StartBelow7Async();

        for (var i = 1; i <= 3; i++)
        {
            var reading = _stream.Hold(Op.Read);
            _stream.Add($"e{i}");
            waiter.Signal();
            await Reached(reading);
            reading.Release();
            (await Next()).Should().NotBeNull();
        }

        _stream.Calls(Op.Groups).Should().Be(1, "within a poll interval of the last check the reads are checked against the position this node knows");
        _stream.Calls(Op.Info).Should().Be(4, "every read is checked against the stream");

        _clock.Advance(FarPoll);
        var due = _stream.Hold(Op.Read);
        _stream.Add("e4");
        waiter.Signal();
        await Reached(due);
        due.Release();

        (await Next()).Should().NotBeNull();
        _stream.Calls(Op.Groups).Should().Be(2, "a poll interval after the last check the position is taken from the server again");
    }

    [Fact]
    public async Task Below_7_a_group_dropped_between_two_position_checks_is_reported_and_made_again_with_a_check_of_its_own()
    {
        var (_, waiter) = await StartBelow7Async();

        var recreating = _stream.Hold(Op.CreateGroup);
        _stream.DropGroup();
        waiter.Signal();
        await Reached(recreating);
        recreating.Release();

        await _missed.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _stream.Calls(Op.CreateGroup).Should().Be(2);

        // After the loss was reported nothing local can be stale: the pending read and a plain one follow, and the group is in use again.
        var pending = _stream.Hold(Op.Read);
        var plain = _stream.Hold(Op.Read);
        waiter.Signal();
        await Reached(pending);
        pending.Release();
        await Reached(plain);
        plain.Release();
        _stream.Calls(Op.Groups).Should().Be(1);

        // The new group's first checked read takes its position from the server, however recent the last check.
        var checking = _stream.Hold(Op.Read);
        waiter.Signal();
        await Reached(checking);
        checking.Release();

        _stream.Calls(Op.Groups).Should().Be(2);
    }

    [Fact]
    public async Task Below_7_a_trim_past_a_group_another_actor_moved_is_reported_against_the_position_this_node_knows()
    {
        var (_, waiter) = await StartBelow7Async();

        // Another actor moves the group to the head past e1..e3, then a trim removes them: against the server's position nothing is past it.
        var reading = _stream.Hold(Op.Read);
        for (var i = 1; i <= 3; i++)
        {
            _stream.Add($"e{i}");
        }

        _stream.MoveGroupToHead();
        _stream.Trim(3);
        waiter.Signal();
        await Reached(reading);
        reading.Release();

        await _missed.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _stream.Calls(Op.Groups).Should().Be(1, "the server's position would have hidden the entries it skipped");
    }

    [Fact]
    public async Task Below_7_a_denied_check_reports_a_loss_once_and_the_read_behind_it_still_delivers()
    {
        var (_, waiter) = await StartBelow7Async();
        _stream.FailWith(Op.Info, new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM this user has no permissions to run the 'xinfo|stream' command"));

        var reading = _stream.Hold(Op.Read);
        _stream.Add("e1");
        waiter.Signal();
        await Reached(reading);
        reading.Release();

        await _missed.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        (await Next()).Should().NotBeNull("the read sent with the denied check still delivers");

        var next = _stream.Hold(Op.Read);
        _stream.Add("e2");
        waiter.Signal();
        await Reached(next);
        next.Release();

        (await Next()).Should().NotBeNull();
        _stream.Calls(Op.Info).Should().Be(2, "a check denied is not sent again");
    }

    [Fact]
    public async Task A_group_lost_while_in_use_is_recreated_where_it_left_off_and_reported_once()
    {
        var made = _stream.Hold(Op.CreateGroup);
        using var sut = Create(formatter: Formatter());
        await Reached(made);
        made.Release();
        (await WaitUntil(() => _stream.HasGroup)).Should().BeTrue();

        // Subscribed once the group is known, so the first create is not a loss.
        var acknowledging = _stream.Hold(Op.Acknowledge);
        _stream.Add("a");
        await Reached(acknowledging);
        sut.MarkSubscribed();
        acknowledging.Release();
        (await Next()).Id.Should().Be("a");

        // "b" lands between the loss and the group made again, on the stream that still holds "a".
        var recreating = _stream.Hold(Op.CreateGroup);
        _stream.DropGroup();
        _stream.Add("b");
        await Reached(recreating);
        recreating.Release();

        await _missed.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        (await Next()).Id.Should().Be("b", "from the last read, so what came since is replayed, not skipped");
        (await WaitUntil(() => _stream.AcknowledgedCount == 2)).Should().BeTrue();
        _events.Reader.TryRead(out _).Should().BeFalse("the stream still holds the entry last read, so the group is not made at 0-0 and nothing before it is read again");
        _stream.Calls(Op.CreateGroup).Should().Be(2);
        _stream.Calls(Op.Range).Should().Be(1, "the entry last read is looked up once, when the group is made again");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_group_made_at_the_start_by_a_create_whose_reply_was_lost_does_not_take_the_first_entry_for_a_trim(bool retryFindsIt)
    {
        _redis.Version.Returns(Version.Parse("6.2.0"));
        _stream.HasCounts = false;
        var made = _stream.Hold(Op.CreateGroup);
        using var sut = Create(formatter: Formatter());
        await Reached(made);
        made.Release();
        (await WaitUntil(() => _stream.HasGroup)).Should().BeTrue();
        sut.MarkSubscribed();
        _stream.Add("a");
        (await Next()).Id.Should().Be("a");

        // The stream is made anew, and the create at 0-0 runs on the server but its reply is lost. A subscriber joins
        // meanwhile, so the next read checks the group, which sits at 0-0 below the new stream's first entry.
        var recreating = _stream.Hold(Op.CreateGroup);
        _stream.Delete();
        _stream.Add("a2");
        await Reached(recreating);
        // Counted from here: the fake has no head, so the first group's first entry may have been taken for a trim already.
        var reported = Volatile.Read(ref _missedCount);
        sut.MarkSubscribed();
        _stream.LoseNextReply(Op.CreateGroup);
        var read = retryFindsIt ? _stream.Hold(Op.Read) : null;
        recreating.Release();
        if (read is not null)
        {
            // As in the head path's test: the read still answers NOGROUP, so a retry finds the group.
            await Reached(read);
            read.Fail(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOGROUP No such key or consumer group"));
        }

        (await Next()).Id.Should().Be("a2");
        _stream.Add("b");
        (await Next()).Id.Should().Be("b");
        _stream.Calls(Op.CreateGroup).Should().Be(retryFindsIt ? 3 : 2);
        Volatile.Read(ref _missedCount).Should().Be(reported + 1, "the replacement is reported once, and the group is the one this node made at 0-0, whose first entry Redis 6 cannot tell from a trim");
    }

    [Theory]
    [InlineData("7.0.0", false)]
    [InlineData("7.0.0", true)]
    [InlineData("6.2.0", false)]
    public async Task A_stream_replaced_under_the_group_is_read_from_its_start_though_its_ids_do_not_pass_the_last_one_read(string version, bool autoAck)
    {
        _redis.Version.Returns(Version.Parse(version));
        _stream.HasCounts = version.StartsWith('7');
        var made = _stream.Hold(Op.CreateGroup);
        using var sut = Create(formatter: Formatter(), context: _context with { AutoAck = autoAck });
        await Reached(made);
        made.Release();
        (await WaitUntil(() => _stream.HasGroup)).Should().BeTrue();
        sut.MarkSubscribed();
        _stream.Add("a");
        (await Next()).Id.Should().Be("a");

        // Deleted, then made anew by another node's add in the same millisecond: its first entry has the id of the one last read.
        var recreating = _stream.Hold(Op.CreateGroup);
        _stream.Delete();
        _stream.Add("a2");
        await Reached(recreating);
        recreating.Release();

        await _missed.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        (await Next()).Id.Should().Be("a2", "a group made again where the old stream was read would never be delivered the new stream's first entries");
        _stream.Add("b");
        (await Next()).Id.Should().Be("b");
        _stream.Calls(Op.CreateGroup).Should().Be(2);
        _stream.Calls(Op.Range).Should().Be(1);
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

    /// <summary>Decodes a payload to an event with the payload as its id, from this node for <c>own</c>, from <c>urn:SELF</c> for <c>OWN</c>, and from another node otherwise.</summary>
    private static IEventFormatterProxy<ICacheEvent> Formatter()
    {
        var formatter = Substitute.For<IEventFormatterProxy<ICacheEvent>>();
        formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(call =>
        {
            var text = Encoding.UTF8.GetString(call.Arg<ReadOnlyMemory<byte>>().Span);
            var source = text switch
            {
                "own" => "urn:self",
                "OWN" => "urn:SELF",
                _ => "urn:other",
            };
            return new TestCacheEvent { Id = text, Valid = true, Source = new Uri(source) };
        });
        return formatter;
    }

    private static bool Decoded(ReadOnlyMemory<byte> body, string payload) => Encoding.UTF8.GetString(body.Span) == payload;

    private Task<ICacheEvent> Next() => _events.Reader.ReadAsync(_stop.Token).AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

    private ObservedWaiter Track(ObservedWaiter waiter)
    {
        _disposables.Add(waiter);
        return waiter;
    }

    /// <summary>
    /// A server below 7.0 with "a" (1-0) in the stream before the reader makes its group at the head, so the first entry
    /// is not past the group; subscribed, read once with the group's position taken from the server, and waiting with the
    /// poll far away, so each read that follows is a signal's, within the poll interval unless the clock is moved.
    /// </summary>
    private async Task<(RedisStreamSubjectWriter<ICacheEvent> Sut, ObservedWaiter Waiter)> StartBelow7Async()
    {
        _redis.Version.Returns(Version.Parse("6.2.0"));
        _stream.HasCounts = false;
        _stream.Add("a");
        var waiter = Track(new ObservedWaiter(new SignalingFetchWaiter(FarPoll)));
        var first = _stream.Hold(Op.Read);
        var made = _stream.Hold(Op.CreateGroup);
        var sut = Create(waiter, FarPoll);
        _disposables.Add(sut);
        await Reached(first);
        first.Release();
        await Reached(made);
        made.Release();

        // Subscribed once the reader knows the group, so the create is not a loss; the pending read comes before the checked one.
        var pending = _stream.Hold(Op.Read);
        var checking = _stream.Hold(Op.Read);
        waiter.Signal();
        await Reached(pending);
        sut.MarkSubscribed();
        pending.Release();
        await Reached(checking);
        checking.Release();
        // Run before the test changes the stream, or the check would take the changed state for the group's own.
        await checking.Completed.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        _stream.Calls(Op.Groups).Should().Be(1, "the first read in use takes the group's position from the server");
        return (sut, waiter);
    }

    private RedisStreamSubjectWriter<ICacheEvent> Create(
        ObservedWaiter? waiter = null,
        TimeSpan? pollInterval = null,
        IEventFormatterProxy<ICacheEvent>? formatter = null,
        ICachingTelemetryProvider? telemetry = null,
        RedisStreamContext? context = null)
    {
        var connectionState = Substitute.For<IConnectionState>();
        connectionState.IsConnected.Returns(true);
        if (formatter is null)
        {
            formatter = Substitute.For<IEventFormatterProxy<ICacheEvent>>();
            formatter.Decode(Arg.Any<ReadOnlyMemory<byte>>()).Returns(_ => new TestCacheEvent { Valid = true, Source = new Uri("urn:other") });
        }

        var poll = pollInterval ?? _context.PollInterval;
        var fetchWaiter = waiter ?? Track(new ObservedWaiter(new SignalingFetchWaiter(poll)));
        return new RedisStreamSubjectWriter<ICacheEvent>(
            (context ?? _context) with { PollInterval = poll },
            connectionState,
            _redis,
            _events.Writer,
            formatter,
            Substitute.For<ILogger>(),
            telemetry ?? Substitute.For<ICachingTelemetryProvider>(),
            Substitute.For<IRedisProfiler>(),
            fetchWaiter,
            () =>
            {
                Interlocked.Increment(ref _missedCount);
                _missed.TrySetResult();
            },
            _stop.Token,
            _stream,
            _clock);
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
