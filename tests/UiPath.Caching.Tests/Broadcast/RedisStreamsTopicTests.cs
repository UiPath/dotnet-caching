using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using StackExchange.Redis;
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
    private IResiliencePipelineProvider _resiliencePipelineProvider = default!;
    private bool _isConnected = true;

    private RedisStreamsTopic<ICacheEvent>? _sut = null;
    private RedisStreamsTopic<ICacheEvent> Sut => _sut ??= _fixture.Create<RedisStreamsTopic<ICacheEvent>>();

    [Fact]
    public void Dispose_works_as_expected()
    {
        var act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureStreamGroup_exception()
    {
        var observer = _fixture.Create<IObserver<ICacheEvent>>();
        _database.StreamCreateConsumerGroup(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                StreamPosition.NewMessages).Throws<Exception>();
        Action act = () => Sut.Subscribe(observer);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void EnsureStreamGroup_RedisServerException_BUSYGROUP()
    {
        var observer = _fixture.Create<IObserver<ICacheEvent>>();
        _database.StreamCreateConsumerGroup(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                StreamPosition.NewMessages).Throws(new RedisServerException(RedisErrorKind.None, CommandFlags.None, "BUSYGROUP Consumer Group name already exists"));
        Action act = () => Sut.Subscribe(observer);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Publish_When_NotConnected()
    {
        _isConnected = false;
        var actual = await Sut.PublishAsync(_fixture.Create<ICacheEvent>(), testContextAccessor.Current.CancellationToken);
        actual.Should().BeFalse();
        await _database.DidNotReceive().StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamField: Arg.Any<RedisValue>(),
                streamValue: Arg.Any<RedisValue>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>());
    }

    [Fact]
    public void EnsureStreamGroup_RedisServerException()
    {
        var observer = _fixture.Create<IObserver<ICacheEvent>>();
        _database.StreamCreateConsumerGroup(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                StreamPosition.NewMessages).Throws(new RedisServerException(RedisErrorKind.None, CommandFlags.None, _fixture.Create<string>()));
        Action act = () => Sut.Subscribe(observer);
        act.Should().Throw<Exception>();
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
                streamField: Arg.Any<RedisValue>(),
                streamValue: Arg.Any<RedisValue>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>());
        actual.Should().BeTrue();
    }

    [Fact]
    public async Task Publish_event_exception()
    {
        var ev = _fixture.Create<ICacheEvent>();
        _database
            .StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamField: Arg.Any<RedisValue>(),
                streamValue: Arg.Any<RedisValue>(),
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
                streamField: Arg.Any<RedisValue>(),
                streamValue: Arg.Any<RedisValue>(),
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
                streamField: Arg.Any<RedisValue>(),
                streamValue: Arg.Any<RedisValue>(),
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
        await _database.ReceivedWithAnyArgs(1).PublishAsync(
            channel: default,
            message: default,
            flags: CommandFlags.FireAndForget);
    }

    [Fact]
    public async Task Publish_returns_true_when_doorbell_publish_faults_asynchronously()
    {
        _redisStreamsTopicOptions.NotifyEnabled = true;
        _redisStreamsTopicOptions.NotifySubscriberTimeout = TimeSpan.FromSeconds(60);
        _redisStreamsTopicOptions.NotifySubscriberDueTime = TimeSpan.FromSeconds(60);
        _sut = null;

        _database.StreamAddAsync(
                key: Arg.Any<RedisKey>(),
                streamField: Arg.Any<RedisValue>(),
                streamValue: Arg.Any<RedisValue>(),
                messageId: Arg.Any<RedisValue?>(),
                maxLength: Arg.Any<long?>(),
                useApproximateMaxLength: Arg.Any<bool>(),
                limit: Arg.Any<long?>(),
                trimMode: Arg.Any<StreamTrimMode>(),
                flags: Arg.Any<CommandFlags>())
            .Returns("1-0");
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromException<long>(new RedisException("doorbell async fault")));

        var ev = _fixture.Create<ICacheEvent>();
        var ok = await Sut.PublishAsync(ev, testContextAccessor.Current.CancellationToken);

        ok.Should().BeTrue();
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
                streamField: Arg.Any<RedisValue>(),
                streamValue: Arg.Any<RedisValue>(),
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
    public async Task A_stream_removed_while_in_use_invalidates_the_subject()
    {
        _database.StreamReadGroupAsync((RedisKey)"stream", "group", "consumer", ">", 1)
            .ReturnsForAnyArgs(
                Task.FromResult(Array.Empty<StreamEntry>()),
                Task.FromException<StreamEntry[]>(new RedisException("NOGROUP No such key or consumer group")),
                Task.FromResult(Array.Empty<StreamEntry>()));
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
    public async Task A_stream_removed_after_a_subscribe_but_before_the_first_read_invalidates_the_subject()
    {
        var subscribed = false;
        var readsSinceSubscribed = 0;
        _database.StreamReadGroupAsync((RedisKey)"stream", "group", "consumer", ">", 1)
            .ReturnsForAnyArgs(_ => !Volatile.Read(ref subscribed) || Interlocked.Increment(ref readsSinceSubscribed) == 1
                ? Task.FromException<StreamEntry[]>(new RedisException("NOGROUP No such key or consumer group"))
                : Task.FromResult(Array.Empty<StreamEntry>()));
        _redisStreamsTopicOptions.PollInterval = TimeSpan.FromMilliseconds(20);
        _redisStreamsTopicOptions.NotifyEnabled = false;

        using var subscription = Sut.Subscribe(_observer);
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
        _database.StreamReadGroupAsync((RedisKey)"stream", "group", "consumer", ">", 1)
            .ReturnsForAnyArgs(_ => Volatile.Read(ref lose) && Interlocked.Increment(ref lost) == 1
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
        _fixture.Inject(_redisStreamsTopicOptions);
        _redisCacheOptions = _fixture.Create<RedisCacheOptions>();
        _fixture.Inject(_redisCacheOptions);
        _cacheOptions = _fixture.Create<CacheOptions>();
        _fixture.Inject(_cacheOptions);
        return ValueTask.CompletedTask;
    }
}
