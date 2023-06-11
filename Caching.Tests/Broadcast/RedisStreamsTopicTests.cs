using System.Reactive.Subjects;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class RedisStreamsTopicTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private RedisStreamsTopicOptions _redisStreamsTopicOptions = default!;
    private RedisCacheOptions _redisCacheOptions = default!;
    private CacheOptions _cacheOptions = default!;
    private ISubject<ICacheEvent> _subject = default!;
    private CancellationTokenSource _cancellationTokenSource = default!;
    private IObserver<ICacheEvent> _observer = default!;
    private IDatabase _database = default!;
    private IPolicyHolder _policyHolder = default!;

    private RedisStreamsTopic<ICacheEvent>? _sut = null;
    private RedisStreamsTopic<ICacheEvent> Sut => _sut ??= _fixture.Create<RedisStreamsTopic<ICacheEvent>>();

    [Fact]
    public void Dispose_works_as_expected()
    {
        var s = Sut;
        _database.Received().StreamCreateConsumerGroup(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                StreamPosition.NewMessages);
        var act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureStreamGroup_exception()
    {
        _database.StreamCreateConsumerGroup(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                StreamPosition.NewMessages).Throws<Exception>();
        Action act = () => Sut.Dispose();
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void EnsureStreamGroup_RedisServerException_BUSYGROUP()
    {
        _database.StreamCreateConsumerGroup(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                StreamPosition.NewMessages).Throws(new RedisServerException("BUSYGROUP Consumer Group name already exists"));
        Action act = () => Sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureStreamGroup_RedisServerException()
    {
        _database.StreamCreateConsumerGroup(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                StreamPosition.NewMessages).Throws(new RedisServerException(_fixture.Create<string>()));
        Action act = () => Sut.Dispose();
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
        var actual = await Sut.PublishAsync(ev, CancellationToken.None);
        await _database.Received()
            .StreamAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), maxLength: Arg.Any<int?>(), useApproximateMaxLength: true, flags: CommandFlags.DemandMaster);
        actual.Should().BeTrue();
    }

    [Fact]
    public async Task Publish_event_exception()
    {
        var ev = _fixture.Create<ICacheEvent>();
        _database
            .StreamAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue?>(), maxLength: Arg.Any<int?>(), useApproximateMaxLength: true, flags: CommandFlags.DemandMaster)
            .ThrowsAsync<Exception>();
        var actual = await Sut.PublishAsync(ev, CancellationToken.None);
        actual.Should().BeFalse();
    }

    public Task DisposeAsync()
    {
        //do nothing;
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _database = _fixture.Freeze<IDatabase>();
        _subject = _fixture.Freeze<ISubject<ICacheEvent>>();
        _observer = _fixture.Freeze<IObserver<ICacheEvent>>();
        _fixture.Inject<Func<ISubject<ICacheEvent>>>(() => _subject);
        _policyHolder = _fixture.Freeze<IPolicyHolder>();
        var noOpExecutor = new NoOpExecutor();
        _policyHolder.Read.Returns(noOpExecutor);
        _policyHolder.Write.Returns(noOpExecutor);
        _cancellationTokenSource = new CancellationTokenSource();
        _fixture.Inject(_cancellationTokenSource.Token);
        _redisStreamsTopicOptions = _fixture.Create<RedisStreamsTopicOptions>();
        _fixture.Inject(_redisStreamsTopicOptions);
        _redisCacheOptions = _fixture.Create<RedisCacheOptions>();
        _fixture.Inject(_redisCacheOptions);
        _cacheOptions = _fixture.Create<CacheOptions>();
        _fixture.Inject(_cacheOptions);
        return Task.CompletedTask;
    }
}
