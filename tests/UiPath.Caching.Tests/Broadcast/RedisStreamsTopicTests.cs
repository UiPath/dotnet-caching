using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using UiPath.Caching.Policies;

namespace UiPath.Caching.Tests.Broadcast;

public class RedisStreamsTopicTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private RedisStreamsTopicOptions _redisStreamsTopicOptions = default!;
    private RedisCacheOptions _redisCacheOptions = default!;
    private CacheOptions _cacheOptions = default!;
    private IEventSubject<ICacheEvent> _subject = default!;
    private CancellationTokenSource _cancellationTokenSource = default!;
    private IObserver<ICacheEvent> _observer = default!;
    private IConnectionState _connectionState = default!;
    private IDatabase _database = default!;
    private IRedisConnector _redis = default!;
    private IResiliencePipelineProvider _resiliencePipelineProvider = default!;
    private bool _isConnected = true;

    private RedisStreamsTopic<ICacheEvent>? _sut = null;
    private RedisStreamsTopic<ICacheEvent> Sut => _sut ??= _fixture.Create<RedisStreamsTopic<ICacheEvent>>();

    [Fact]
    public void A_notified_interval_below_the_poll_interval_is_refused_when_the_topic_is_made()
    {
        _redisStreamsTopicOptions.PollInterval = TimeSpan.FromSeconds(1);
        _redisStreamsTopicOptions.NotifiedPollInterval = TimeSpan.FromMilliseconds(500);
        _redisStreamsTopicOptions.NotifyMinInterval = TimeSpan.Zero;

        var act = () => Sut;

        act.Should().Throw<Exception>().Which.ToString().Should().Contain(nameof(RedisStreamsTopicOptions.NotifiedPollInterval));
    }

    [Fact]
    public void Dispose_works_as_expected()
    {
        var act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("ERR something")]
    [InlineData("BUSYGROUP Consumer Group name already exists")]
    public void Subscribe_sends_no_synchronous_group_create(string error)
    {
        var observer = _fixture.Create<IObserver<ICacheEvent>>();
        _database.StreamCreateConsumerGroup(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>())
            .Throws(new RedisServerException(RedisErrorKind.None, CommandFlags.None, error));

        Action act = () => Sut.Subscribe(observer);

        act.Should().NotThrow();
        _database.DidNotReceiveWithAnyArgs().StreamCreateConsumerGroup(default, default, default);
    }

    [Fact]
    public async Task Publish_When_NotConnected()
    {
        _isConnected = false;
        var actual = await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();
        await _database.DidNotReceive().StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>());
    }

    [Fact]
    public void It_Subscribes_to_subject()
    {
        var disposable = Sut.Subscribe(_observer);
        disposable.Should().NotBeNull();
        _subject.Received().Subscribe(Arg.Any<IObserver<ICacheEvent>>());
    }

    [Fact]
    public async Task Publish_event()
    {
        var ev = _fixture.Create<ICacheEvent>();
        var actual = await Sut.PublishAsync(ev, testContextAccessor.Current.CancellationToken);

        await _database.Received()
            .StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>());
        actual.Should().BeTrue();
    }

    [Fact]
    public async Task A_published_entry_carries_the_payload_under_the_field_name_and_the_source_after_it()
    {
        var formatter = _fixture.Freeze<IEventFormatterProxy<ICacheEvent>>();
        var ev = _fixture.Create<ICacheEvent>();
        ev.Source.Returns(new Uri("urn:Writer-Node"));
        byte[] payload = [1, 2, 3];
        formatter.Encode(ev).Returns(payload);
        var fields = ArrangeAddCapture();

        (await Sut.PublishAsync(ev, testContextAccessor.Current.CancellationToken)).Should().BeTrue();

        fields.Value.Should().NotBeNull();
        fields.Value!.Select(f => f.Name.ToString()).Should().Equal(_redisStreamsTopicOptions.FieldName, "src");
        ((byte[]?)fields.Value[0].Value).Should().Equal(payload);
        fields.Value[1].Value.ToString().Should().Be("urn:Writer-Node");
        // A 3.0 reader takes the payload by its field name (`@event[_context.FieldName]` in RedisStreamSubjectWriter at v3.0.0), so the second field is ignored.
        var entry = new StreamEntry("1-0", fields.Value);
        ((ReadOnlyMemory<byte>)entry[_redisStreamsTopicOptions.FieldName]).ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task An_event_without_a_source_is_published_with_the_payload_alone()
    {
        var ev = _fixture.Create<ICacheEvent>();
        ev.Source.Returns((Uri?)null);
        var fields = ArrangeAddCapture();

        (await Sut.PublishAsync(ev, testContextAccessor.Current.CancellationToken)).Should().BeTrue();

        fields.Value.Should().ContainSingle().Which.Name.Should().Be((RedisValue)_redisStreamsTopicOptions.FieldName);
    }

    [Fact]
    public async Task Publish_event_exception()
    {
        var ev = _fixture.Create<ICacheEvent>();
        _database
            .StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .ThrowsAsync<Exception>();
        var actual = await Sut.PublishAsync(ev, testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();
    }

    [Fact]
    public async Task Publish_does_not_publish_pubsub_when_notify_disabled()
    {
        _redisStreamsTopicOptions.NotifyEnabled = false;
        _sut = null;

        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns("1-0");

        var ev = _fixture.Create<ICacheEvent>();
        var ok = await Sut.PublishAsync(ev, testContextAccessor.Current.CancellationToken);

        ok.Should().BeTrue();
        await _database.DidNotReceiveWithAnyArgs().PublishAsync(
            channel: default,
            message: default,
            flags: default);
    }

    [Fact]
    public async Task Publish_publishes_pubsub_after_xadd_when_notify_enabled()
    {
        _redisStreamsTopicOptions.NotifyEnabled = true;
        _redisStreamsTopicOptions.NotifySubscriberTimeout = TimeSpan.FromSeconds(60);
        _redisStreamsTopicOptions.NotifySubscriberDueTime = TimeSpan.FromSeconds(60);
        _sut = null;

        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns("1-0");

        var ev = _fixture.Create<ICacheEvent>();
        var ok = await Sut.PublishAsync(ev, testContextAccessor.Current.CancellationToken);

        ok.Should().BeTrue();
        await _database.Received(1).PublishAsync(
            Arg.Any<RedisChannel>(),
            (RedisValue)(_cacheOptions.SourceUri ?? CacheOptions.MachineUri).ToString().ToLowerInvariant(),
            CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task On_a_single_primary_the_doorbell_goes_out_behind_the_xadd_before_its_reply()
    {
        ArrangeDoorbell();
        Primaries(ServerType.Standalone);
        var added = ArrangeGatedAdd();

        var publishing = Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken);

        await _database.Received(1).PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), CommandFlags.DemandMaster);
        publishing.IsCompleted.Should().BeFalse("the XADD has not been answered yet");
        added.SetResult("1-0");
        (await publishing).Should().BeTrue();
        await _database.Received(1).PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task On_a_cluster_or_before_the_topology_is_known_the_doorbell_waits_for_the_xadd_reply(bool cluster)
    {
        ArrangeDoorbell();
        if (cluster)
        {
            Primaries(ServerType.Cluster, ServerType.Cluster);
        }

        var added = ArrangeGatedAdd();

        var publishing = Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken);

        await _database.DidNotReceiveWithAnyArgs().PublishAsync(default, default, default);
        added.SetResult("1-0");
        (await publishing).Should().BeTrue();
        await _database.Received(1).PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), CommandFlags.DemandMaster);
    }

    [Fact]
    public async Task The_topology_is_read_once_it_is_known_and_again_after_a_reconnect()
    {
        ArrangeDoorbell();
        var token = testContextAccessor.Current.CancellationToken;
        var added = ArrangeGatedAdd();
        var publishing = Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token).AsTask();
        await _database.DidNotReceiveWithAnyArgs().PublishAsync(default, default, default);
        added.SetResult("1-0");
        (await publishing).Should().BeTrue();

        // Known now: the next doorbell goes out behind the XADD, and the one after that too, without asking again.
        Primaries(ServerType.Standalone);
        for (var i = 2; i <= 3; i++)
        {
            added = ArrangeGatedAdd();
            publishing = Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token).AsTask();
            await _database.Received(i).PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
            added.SetResult("1-0");
            (await publishing).Should().BeTrue();
        }

        _redis.Received(2).GetPrimaries();

        // A reconnect may be to a cluster: asked again, and the doorbell waits for the reply.
        Primaries(ServerType.Cluster);
        _redis.OnReconnected += Raise.Event<EventHandler>(_redis, EventArgs.Empty);
        added = ArrangeGatedAdd();
        publishing = Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token).AsTask();
        await _database.Received(3).PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        added.SetResult("1-0");
        (await publishing).Should().BeTrue();
        await _database.Received(4).PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task With_no_minimum_interval_set_the_folded_ring_goes_out_as_soon_as_the_doorbell_is_answered()
    {
        ArrangeDoorbell();
        Sut.DoorbellSpacing.Should().Be(TimeSpan.Zero, "the default spaces doorbells by the round trip alone");
        var first = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return first.Task;
            }

            secondSent.TrySetResult();
            return Task.FromResult(1L);
        });
        var token = testContextAccessor.Current.CancellationToken;

        for (var i = 0; i < 3; i++)
        {
            (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        }

        Volatile.Read(ref calls).Should().Be(1, "the rings during a doorbell fold into one more");
        first.SetResult(1);
        await secondSent.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        Volatile.Read(ref calls).Should().Be(2, "the folded ring is one doorbell, sent once the first is answered");
    }

    [Fact]
    public async Task A_burst_of_events_rings_the_doorbell_once_and_then_once_more_for_the_rest()
    {
        _redisStreamsTopicOptions.NotifyEnabled = true;
        _redisStreamsTopicOptions.NotifySubscriberTimeout = TimeSpan.FromSeconds(60);
        _redisStreamsTopicOptions.NotifySubscriberDueTime = TimeSpan.FromSeconds(60);
        _sut = null;
        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns("1-0");
        var gate = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ => gate.Task);

        for (var i = 0; i < 20; i++)
        {
            (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken)).Should().BeTrue();
        }

        await _database.ReceivedWithAnyArgs(1).PublishAsync(default, default, default);
        gate.SetResult(1);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (_database.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IDatabaseAsync.PublishAsync)) < 2 && DateTime.UtcNow < until)
        {
            await Task.Delay(5, testContextAccessor.Current.CancellationToken);
        }

        await Task.Delay(100, testContextAccessor.Current.CancellationToken);
        await _database.ReceivedWithAnyArgs(2).PublishAsync(default, default, default);
    }

    [Fact]
    public async Task Rings_within_the_minimum_interval_fold_into_one_doorbell_sent_once_it_has_passed()
    {
        _redisStreamsTopicOptions.NotifyEnabled = true;
        _redisStreamsTopicOptions.NotifySubscriberTimeout = TimeSpan.FromSeconds(60);
        _redisStreamsTopicOptions.NotifySubscriberDueTime = TimeSpan.FromSeconds(60);
        _sut = null;
        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns("1-0");
        var sent = new List<long>();
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ =>
            {
                lock (sent)
                {
                    sent.Add(Stopwatch.GetTimestamp());
                }

                return Task.FromResult(1L);
            });
        Sut.DoorbellSpacing = TimeSpan.FromSeconds(2);
        var token = testContextAccessor.Current.CancellationToken;

        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        for (var i = 0; i < 10; i++)
        {
            (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        }

        // The spacing is far longer than the publishes take, so even a starved test thread sees the first doorbell alone.
        // Waits for each doorbell rather than sleeping a fixed time, so a loaded machine cannot make the counts race.
        (await SentAtLeast(1)).Should().BeTrue();
        lock (sent)
        {
            sent.Should().HaveCount(1, "the first ring goes out at once and the rest wait out the interval");
        }

        (await SentAtLeast(2)).Should().BeTrue();
        await Task.Delay(2_500, token); // longer than the spacing, so a stray third doorbell would have arrived
        lock (sent)
        {
            sent.Should().HaveCount(2, "the waiting rings fold into one doorbell");
            Stopwatch.GetElapsedTime(sent[0], sent[1]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1_500));
        }

        async Task<bool> SentAtLeast(int count)
        {
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
            {
                lock (sent)
                {
                    if (sent.Count >= count)
                    {
                        return true;
                    }
                }

                await Task.Delay(10, token);
            }

            return false;
        }
    }

    [Fact]
    public async Task A_doorbell_stalled_on_redis_holds_the_rings_behind_it_only_for_its_timeout()
    {
        _redisStreamsTopicOptions.NotifyEnabled = true;
        _redisStreamsTopicOptions.NotifySubscriberTimeout = TimeSpan.FromSeconds(60);
        _redisStreamsTopicOptions.NotifySubscriberDueTime = TimeSpan.FromSeconds(60);
        _sut = null;
        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns("1-0");
        var stalled = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        // Only the first doorbell stalls: a second that stalled too would have its folded ring retried after its own timeout.
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref calls) == 1 ? stalled.Task : Task.FromResult(1L));
        Sut.DoorbellTimeout = TimeSpan.FromMilliseconds(200);

        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken)).Should().BeTrue();
        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken)).Should().BeTrue();

        var until = DateTime.UtcNow.AddSeconds(5);
        while (_database.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IDatabaseAsync.PublishAsync)) < 2 && DateTime.UtcNow < until)
        {
            await Task.Delay(5, testContextAccessor.Current.CancellationToken);
        }

        await _database.ReceivedWithAnyArgs(2).PublishAsync(default, default, default);
        stalled.SetResult(1);
    }

    [Fact]
    public async Task Publish_returns_true_when_doorbell_publish_throws_synchronously()
    {
        _redisStreamsTopicOptions.NotifyEnabled = true;
        _redisStreamsTopicOptions.NotifySubscriberTimeout = TimeSpan.FromSeconds(60);
        _redisStreamsTopicOptions.NotifySubscriberDueTime = TimeSpan.FromSeconds(60);
        _sut = null;

        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns("1-0");
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs<Task<long>>(_ => throw new RedisException("doorbell down"));

        var ev = _fixture.Create<ICacheEvent>();
        var ok = await Sut.PublishAsync(ev, testContextAccessor.Current.CancellationToken);

        ok.Should().BeTrue();
    }

    [Fact]
    public async Task A_doorbell_that_times_out_at_once_does_not_stop_the_next_ring()
    {
        _redisStreamsTopicOptions.NotifyEnabled = true;
        _redisStreamsTopicOptions.NotifySubscriberTimeout = TimeSpan.FromSeconds(60);
        _redisStreamsTopicOptions.NotifySubscriberDueTime = TimeSpan.FromSeconds(60);
        _sut = null;
        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns("1-0");
        var calls = 0;
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref calls) == 1 ? throw new TimeoutException("timed out") : Task.FromResult(1L));

        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken)).Should().BeTrue();
        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken)).Should().BeTrue();

        var until = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref calls) < 2 && DateTime.UtcNow < until)
        {
            await Task.Delay(5, testContextAccessor.Current.CancellationToken);
        }

        Volatile.Read(ref calls).Should().BeGreaterThanOrEqualTo(2);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(int.MaxValue, 4)]
    public async Task Rings_folded_into_a_doorbell_that_fails_are_retried_a_bounded_number_of_times(int failuresBeforeSuccess, int expectedCalls)
    {
        ArrangeDoorbell();
        var calls = 0;
        var first = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref calls) is var n && n == 1
                ? first.Task
                : n - 1 <= failuresBeforeSuccess
                    ? Task.FromException<long>(new RedisException("down"))
                    : Task.FromResult(1L));
        Sut.DoorbellSpacing = TimeSpan.FromMilliseconds(200);
        var token = testContextAccessor.Current.CancellationToken;

        // The first doorbell stays in flight until both later rings have folded into the next one.
        await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token);
        await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token);
        await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token);
        first.SetResult(1L);

        await DoorbellCallsSettledAsync(() => Volatile.Read(ref calls), expectedCalls, token);

        Volatile.Read(ref calls).Should().Be(expectedCalls, "the doorbell for the folded ring is retried until it goes out, at most twice");
    }

    [Fact]
    public async Task A_failed_doorbell_with_no_folded_ring_is_not_retried()
    {
        ArrangeDoorbell();
        var calls = 0;
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref calls) > 0 ? Task.FromException<long>(new RedisException("down")) : Task.FromResult(1L));
        Sut.DoorbellSpacing = TimeSpan.FromMilliseconds(200);
        var token = testContextAccessor.Current.CancellationToken;

        await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token);
        await Task.Delay(700, token);

        Volatile.Read(ref calls).Should().Be(1);
    }

    [Fact]
    public async Task A_ring_that_lands_during_a_doorbell_is_retried_when_its_follow_up_fails()
    {
        ArrangeDoorbell();
        var calls = 0;
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n == 1)
                {
                    Sut.PublishAsync(_fixture.Create<ICacheEvent>(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }

                return n == 2 ? Task.FromException<long>(new RedisException("down")) : Task.FromResult(1L);
            });
        Sut.DoorbellSpacing = TimeSpan.FromMilliseconds(100);

        await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken);
        await DoorbellCallsSettledAsync(() => Volatile.Read(ref calls), 3, testContextAccessor.Current.CancellationToken);

        Volatile.Read(ref calls).Should().Be(3, "the follow-up doorbell owes the ring that landed during the first one");
    }

    [Fact]
    public async Task A_ring_that_lands_after_the_retries_are_spent_gets_its_own_retries()
    {
        ArrangeDoorbell();
        var calls = 0;
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n is 1 or 4)
                {
                    Sut.PublishAsync(_fixture.Create<ICacheEvent>(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }

                return n == 1 ? Task.FromResult(1L) : Task.FromException<long>(new RedisException("down"));
            });
        Sut.DoorbellSpacing = TimeSpan.FromMilliseconds(100);

        await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken);
        await DoorbellCallsSettledAsync(() => Volatile.Read(ref calls), 7, testContextAccessor.Current.CancellationToken);

        Volatile.Read(ref calls).Should().Be(7, "the second ring is retried twice after its own failure, then the run ends");
    }

    [Fact]
    public async Task A_topic_disposed_while_a_ring_waits_stops_the_doorbell_without_a_failure()
    {
        var logger = _fixture.Freeze<ILogger<RedisStreamsTopic<ICacheEvent>>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        ArrangeDoorbell();
        var calls = 0;
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ =>
            {
                Interlocked.Increment(ref calls);
                // A ring that arrives during the send, and the topic going away before the doorbell loops to answer it.
                Sut.PublishAsync(_fixture.Create<ICacheEvent>(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Sut.Dispose();
                return Task.FromResult(1L);
            });
        Sut.DoorbellSpacing = TimeSpan.FromSeconds(10);

        await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken);
        await Task.Delay(200, testContextAccessor.Current.CancellationToken);

        Volatile.Read(ref calls).Should().Be(1);
        logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ILogger.Log) && c.GetArguments()[0] is LogLevel.Warning && c.GetArguments()[2]?.ToString()!.Contains("doorbell", StringComparison.Ordinal) == true)
            .Should().Be(0, "stopping is not a doorbell failure");
    }

    [Fact]
    public async Task A_stream_removed_while_in_use_invalidates_the_subject()
    {
        _database.StreamReadGroupAsync((RedisKey)"stream", "group", "consumer", ">", 1)
            .ReturnsForAnyArgs(StreamReads.NewOnly(
                Task.FromResult(Array.Empty<StreamEntry>()),
                Task.FromException<StreamEntry[]>(new RedisException("NOGROUP No such key or consumer group")),
                Task.FromResult(Array.Empty<StreamEntry>())));
        _redisStreamsTopicOptions.PollInterval = TimeSpan.FromMilliseconds(20);
        _redisStreamsTopicOptions.NotifyEnabled = false;
        _ = Sut;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !_subject.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IEventSubject<ICacheEvent>.Invalidate)))
        {
            await Task.Delay(20, testContextAccessor.Current.CancellationToken);
        }

        _subject.Received(1).Invalidate(MissedEventsReason.Lost);
    }

    [Fact]
    public async Task Every_half_MaxLength_own_publishes_wake_a_read()
    {
        // The first read, before the topic is in use, is plain; the ones a wake brings run the check-and-read script.
        var reads = 0;
        _database.StreamReadGroupAsync((RedisKey)"stream", "group", "consumer", ">", 1)
            .ReturnsForAnyArgs(StreamReads.NewOnly(_ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(Array.Empty<StreamEntry>());
            }));
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(_ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(RedisResult.Create(
                [
                    RedisResult.Create(new RedisValue[] { "length", 0, "last-generated-id", "0-0", "entries-added", 0 }),
                    RedisResult.Create(Array.Empty<RedisResult>()),
                    RedisResult.Create(RedisValue.Null),
                ]));
            });
        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns("1-0");
        _redisStreamsTopicOptions.PollInterval = TimeSpan.FromSeconds(30);
        _redisStreamsTopicOptions.MaxLength = 4;
        _redisStreamsTopicOptions.NotifyEnabled = false;
        var token = testContextAccessor.Current.CancellationToken;
        _ = Sut;
        (await ReadsAsync(1, token)).Should().BeTrue();

        await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token);
        await Task.Delay(300, token);
        Volatile.Read(ref reads).Should().Be(1, "one own entry is nowhere near the trim");
        await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token);

        (await ReadsAsync(2, token)).Should().BeTrue("own entries are read at the poll alone, so every MaxLength / 2 of them wakes a read before the trim can reach them");

        async Task<bool> ReadsAsync(int expected, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && Volatile.Read(ref reads) < expected)
            {
                await Task.Delay(20, ct);
            }

            return Volatile.Read(ref reads) == expected;
        }
    }

    [Fact]
    public async Task A_stream_removed_after_a_subscribe_but_before_the_first_read_invalidates_the_subject()
    {
        var subscribed = false;
        var reads = 0;
        var readsSinceSubscribed = 0;
        _database.StreamReadGroupAsync((RedisKey)"stream", "group", "consumer", ">", 1)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref reads) > 0 && (!Volatile.Read(ref subscribed) || Interlocked.Increment(ref readsSinceSubscribed) == 1)
                ? Task.FromException<StreamEntry[]>(new RedisException("NOGROUP No such key or consumer group"))
                : Task.FromResult(Array.Empty<StreamEntry>()));
        // The group the subscribe makes stays: a NOGROUP answered to a read issued before it recreates nothing and stays silent.
        _database.StreamCreateConsumerGroupAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>())
            .ThrowsAsyncForAnyArgs(new RedisServerException(RedisErrorKind.None, CommandFlags.None, StreamConstants.ConsumerGroupNameExistsErrorMessage));
        _redisStreamsTopicOptions.PollInterval = TimeSpan.FromMilliseconds(20);
        _redisStreamsTopicOptions.NotifyEnabled = false;

        using var subscription = Sut.Subscribe(_observer);
        // A read issued before the subscribe may answer after it; the stream goes once a read issued after it is seen.
        await ReadsAfterAsync(() => Volatile.Read(ref reads));
        Volatile.Write(ref subscribed, true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref readsSinceSubscribed) < 3)
        {
            await Task.Delay(20, testContextAccessor.Current.CancellationToken);
        }

        _subject.Received(1).Invalidate(MissedEventsReason.Lost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_observer_joining_while_a_loss_is_reported_is_told_itself(bool throws)
    {
        var lose = false;
        var lost = 0;
        var reads = 0;
        _database.StreamReadGroupAsync((RedisKey)"stream", "group", "consumer", ">", 1)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref reads) > 0 && Volatile.Read(ref lose) && Interlocked.Increment(ref lost) == 1
                ? Task.FromException<StreamEntry[]>(new RedisException("NOGROUP No such key or consumer group"))
                : Task.FromResult(Array.Empty<StreamEntry>()));
        _redisStreamsTopicOptions.PollInterval = TimeSpan.FromMilliseconds(20);
        _redisStreamsTopicOptions.NotifyEnabled = false;
        using var joining = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _subject.Subscribe(Arg.Any<IObserver<ICacheEvent>>()).Returns(_ =>
        {
            joining.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return Substitute.For<IDisposable>();
        });
        var entry = Substitute.For<IObserver<ICacheEvent>, IMissedEventsObserver>();
        if (throws)
        {
            ((IMissedEventsObserver)entry).When(e => e.OnEventsMissed(Arg.Any<MissedEventsReason>())).Do(_ => throw new InvalidOperationException("boom"));
        }

        _ = Sut;

        var subscribing = Task.Run(() => Sut.Subscribe(entry), testContextAccessor.Current.CancellationToken);
        joining.Wait(TimeSpan.FromSeconds(10), testContextAccessor.Current.CancellationToken).Should().BeTrue();
        // A read issued before the subscriber joined may answer after it; the loss comes to a read issued after.
        await ReadsAfterAsync(() => Volatile.Read(ref reads));
        Volatile.Write(ref lose, true);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !_subject.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IEventSubject<ICacheEvent>.Invalidate)))
        {
            await Task.Delay(20, testContextAccessor.Current.CancellationToken);
        }

        release.Set();
        (await subscribing).Dispose();

        ((IMissedEventsObserver)entry).Received(1).OnEventsMissed(MissedEventsReason.Lost);
    }

    public ValueTask DisposeAsync()
    {
        _sut?.Dispose();
        _cancellationTokenSource?.Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _database = _fixture.Freeze<IDatabase>();

        // Reads go apart from the gap check, so these tests drive them through StreamReadGroupAsync.
        _database.Configure().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(Task.FromException<RedisResult>(new RedisCommandException("scripts are disabled")));
        _database.Configure().StreamInfo(Arg.Any<RedisKey>()).ReturnsForAnyArgs(default(StreamInfo));
        _database.Configure().StreamInfoAsync(Arg.Any<RedisKey>()).ReturnsForAnyArgs(Task.FromResult(default(StreamInfo)));
        _database.Configure().StreamGroupInfoAsync(Arg.Any<RedisKey>()).ReturnsForAnyArgs(Task.FromResult(Array.Empty<StreamGroupInfo>()));
        // No primary known: the doorbell waits for the XADD reply, as before the topology is read; the tests of the other path set one.
        _redis = _fixture.Freeze<IRedisConnector>();
        _redis.Database.Returns(_database);
        _redis.GetPrimaries().Returns([]);
        _subject = _fixture.Freeze<IEventSubject<ICacheEvent>>();
        _observer = _fixture.Freeze<IObserver<ICacheEvent>>();
        _connectionState = _fixture.Freeze<IConnectionState>();
        _connectionState.IsConnected.Returns(info => _isConnected);
        _fixture.Inject<Func<IEventSubject<ICacheEvent>>>(() => _subject);
        _resiliencePipelineProvider = _fixture.Freeze<IResiliencePipelineProvider>();
        var noOpExecutor = new EmptyResiliencePipeline();
        _resiliencePipelineProvider.Get(ResiliencePipelineNames.Read).Returns(noOpExecutor);
        _resiliencePipelineProvider.Get(ResiliencePipelineNames.Write).Returns(noOpExecutor);
        _cancellationTokenSource = new CancellationTokenSource();
        _fixture.Inject(_cancellationTokenSource.Token);
        _redisStreamsTopicOptions = _fixture.Create<RedisStreamsTopicOptions>();
        _redisStreamsTopicOptions.NotifiedPollInterval = null;
        _fixture.Inject(_redisStreamsTopicOptions);
        _redisCacheOptions = _fixture.Create<RedisCacheOptions>();
        _fixture.Inject(_redisCacheOptions);
        _cacheOptions = _fixture.Create<CacheOptions>();
        _fixture.Inject(_cacheOptions);
        return ValueTask.CompletedTask;
    }

    /// <summary>The doorbell on the stream's own channel, which a script may publish to.</summary>
    [Fact]
    public async Task With_the_default_channel_the_entry_carries_its_doorbell_in_one_scripted_command()
    {
        ArrangeRingInAdd();
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Scripted("1-0"));

        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken)).Should().BeTrue();
        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken)).Should().BeTrue();

        await _database.Received(2).ScriptEvaluateAsync(
            Arg.Is<string>(script => script.Contains("PUBLISH")),
            Arg.Any<RedisKey[]>(),
            Arg.Is<RedisValue[]>(values => values[2] == (RedisValue)(_cacheOptions.SourceUri ?? CacheOptions.MachineUri).ToString().ToLowerInvariant()),
            CommandFlags.DemandMaster);
        await _database.DidNotReceiveWithAnyArgs().PublishAsync(default, default, default);
        _database.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IDatabase.StreamAddAsync)).Should().Be(0);
    }

    [Fact]
    public async Task An_entry_added_while_a_scripted_doorbell_is_in_flight_folds_into_one_doorbell_after_it()
    {
        ArrangeRingInAdd();
        var scripted = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>()).Returns(scripted.Task);
        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns("2-0");
        var token = testContextAccessor.Current.CancellationToken;

        var first = Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token);
        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        await _database.DidNotReceiveWithAnyArgs().PublishAsync(default, default, default);

        scripted.SetResult(Scripted("1-0"));
        (await first).Should().BeTrue();

        await DoorbellCallsSettledAsync(Doorbells, 1, token);
        Doorbells().Should().Be(1, "the two folded rings get one doorbell after the scripted one");
        await _database.Received(1).ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_refused_script_falls_back_to_the_xadd_and_a_doorbell_of_its_own_for_good()
    {
        ArrangeRingInAdd();
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM this user has no permissions to run the 'eval' command"));
        var token = testContextAccessor.Current.CancellationToken;

        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        await DoorbellCallsSettledAsync(Doorbells, 1, token);
        Doorbells().Should().Be(1);
        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        await DoorbellCallsSettledAsync(Doorbells, 2, token);
        Doorbells().Should().Be(2);

        await _database.Received(1).ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_script_that_adds_but_cannot_publish_keeps_its_one_entry_and_rings_with_a_doorbell_of_its_own()
    {
        // The XADD in the script stays when its PUBLISH is refused (scripts do not roll back), so the entry must not be added again.
        ArrangeRingInAdd();
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Scripted("1-0", rung: false, error: "NOPERM No permissions to access a channel"));
        var token = testContextAccessor.Current.CancellationToken;

        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        await DoorbellCallsSettledAsync(Doorbells, 1, token);

        Doorbells().Should().Be(1);
        _database.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IDatabase.StreamAddAsync)).Should().Be(0);
        Sut.RingInAdd.Should().BeFalse("a script that may not publish rings with a command of its own from now on");
    }

    [Fact]
    public async Task A_script_the_client_refuses_falls_back_to_the_xadd_and_a_doorbell_of_its_own()
    {
        // A command map that disables EVAL fails it in the client, before anything is sent.
        ArrangeRingInAdd();
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisCommandException("This operation has been disabled in the command-map and cannot be used: EVAL"));
        var token = testContextAccessor.Current.CancellationToken;

        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        await DoorbellCallsSettledAsync(Doorbells, 1, token);

        Doorbells().Should().Be(1);
        _database.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IDatabase.StreamAddAsync)).Should().Be(1);
        Sut.RingInAdd.Should().BeFalse();
    }

    [Theory]
    [InlineData("BUSY Redis is busy running a script. You can only call SCRIPT KILL or SHUTDOWN NOSAVE.")]
    [InlineData("LOADING Redis is loading the dataset in memory")]
    [InlineData("ERR Error running script (call to f_1a2b): @user_script:3: @user_script: 3: -READONLY You can't write against a read only replica.")]
    public async Task A_script_that_fails_for_a_passing_reason_keeps_scripts_on_and_sends_no_doorbell(string error)
    {
        ArrangeRingInAdd();
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisServerException(RedisErrorKind.None, CommandFlags.None, error));
        var token = testContextAccessor.Current.CancellationToken;

        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeFalse();
        await DoorbellCallsSettledAsync(Doorbells, 0, token);

        Doorbells().Should().Be(0, "no entry was added, so there is nothing to ring for");
        Sut.RingInAdd.Should().BeTrue();
    }

    [Fact]
    public async Task Rings_folded_behind_a_scripted_doorbell_keep_their_retries_when_the_doorbell_after_it_fails()
    {
        ArrangeRingInAdd();
        var scripted = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>()).Returns(scripted.Task);
        var calls = 0;
        _database.PublishAsync(default, default, default)
            .ReturnsForAnyArgs(_ => Interlocked.Increment(ref calls) == 1 ? Task.FromException<long>(new RedisException("down")) : Task.FromResult(1L));
        var token = testContextAccessor.Current.CancellationToken;

        var first = Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token);
        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        scripted.SetResult(Scripted("1-0"));
        (await first).Should().BeTrue();

        await DoorbellCallsSettledAsync(() => Volatile.Read(ref calls), 2, token);
        Volatile.Read(ref calls).Should().Be(2, "the doorbell for the folded ring is retried once it fails");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_connection_that_prefixes_keys_or_channels_rings_with_a_doorbell_of_its_own(bool keyPrefix)
    {
        // The script's channel is an argument, which neither a key prefix nor a channel prefix reaches; the PUBLISH gets both.
        ArrangeRingInAdd();
        if (keyPrefix)
        {
            _redis.Database.Returns(_database.WithKeyPrefix("pfx:"));
        }
        else
        {
            _database.Multiplexer.Configuration.Returns("localhost:6379,channelPrefix=app:");
        }

        var token = testContextAccessor.Current.CancellationToken;

        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        await DoorbellCallsSettledAsync(Doorbells, 1, token);

        await _database.DidNotReceiveWithAnyArgs().ScriptEvaluateAsync(default(string)!, default, default, default);
        _database.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IDatabase.StreamAddAsync)).Should().Be(1);
        Doorbells().Should().Be(1);
    }

    [Fact]
    public async Task A_sharded_channel_keeps_the_xadd_and_a_doorbell_of_its_own()
    {
        ArrangeRingInAdd();
        _redisStreamsTopicOptions.NotifyShardedPubSub = true;
        _sut = null;
        var token = testContextAccessor.Current.CancellationToken;

        (await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), token)).Should().BeTrue();
        await DoorbellCallsSettledAsync(Doorbells, 1, token);

        await _database.DidNotReceiveWithAnyArgs().ScriptEvaluateAsync(default(string)!, default, default, default);
        Doorbells().Should().Be(1);
    }

    private static RedisResult Scripted(string id, bool rung = true, string? error = null) =>
        RedisResult.Create(error is null
            ? [RedisResult.Create((RedisValue)id), RedisResult.Create((RedisValue)(rung ? 1 : 0))]
            : [RedisResult.Create((RedisValue)id), RedisResult.Create((RedisValue)(rung ? 1 : 0)), RedisResult.Create((RedisValue)error)]);

    /// <summary>Waits for the expected number of doorbells rather than a fixed time, then a little longer to show no further one follows.</summary>
    private static async Task DoorbellCallsSettledAsync(Func<int> calls, int expected, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        while (calls() < expected && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(10, token);
        }

        await Task.Delay(700, token);
    }

    private int Doorbells() => _database.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IDatabase.PublishAsync));

    /// <summary>Answers the XADD with an id and captures the fields it was given.</summary>
    private StrongBox<NameValueEntry[]?> ArrangeAddCapture()
    {
        var fields = new StrongBox<NameValueEntry[]?>();
        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Do<NameValueEntry[]>(f => fields.Value = f),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>("1-0"));
        return fields;
    }

    /// <summary>Waits for a read issued from now on: one issued before whatever the test does next may answer after it.</summary>
    private async Task ReadsAfterAsync(Func<int> reads)
    {
        var seen = reads();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < until && reads() <= seen)
        {
            await Task.Delay(5, testContextAccessor.Current.CancellationToken);
        }
    }

    private void ArrangeRingInAdd()
    {
        ArrangeDoorbell();
        _redisStreamsTopicOptions.NotifyChannelStrategy = null;
        _redisStreamsTopicOptions.NotifyShardedPubSub = false;
        Primaries(ServerType.Cluster);
    }

    // The servers are made before GetPrimaries is called: configuring them in its Returns argument would steal the last call.
    private void Primaries(params ServerType[] types)
    {
        var servers = Array.ConvertAll(types, static type =>
        {
            var server = Substitute.For<IServer>();
            server.ServerType.Returns(type);
            return server;
        });
        _redis.GetPrimaries().Returns(servers);
    }

    /// <summary>The next XADD answers only when the returned source is completed.</summary>
    private TaskCompletionSource<RedisValue> ArrangeGatedAdd()
    {
        var added = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns(added.Task);
        return added;
    }

    private void ArrangeDoorbell()
    {
        _redisStreamsTopicOptions.NotifyEnabled = true;
        // The fixture fills the options at random; the default spacing is the one under test unless a test sets its own.
        _redisStreamsTopicOptions.NotifyMinInterval = null;
        _redisStreamsTopicOptions.NotifySubscriberTimeout = TimeSpan.FromSeconds(60);
        _redisStreamsTopicOptions.NotifySubscriberDueTime = TimeSpan.FromSeconds(60);
        _sut = null;
        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamPairs: Arg.Any<NameValueEntry[]>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns("1-0");
    }
}
