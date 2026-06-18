namespace UiPath.Caching.Tests;

public class CacheEntryBuilderTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private ITopicKeyStrategy _topicKeyStrategy = default!;
    private CacheKey _cacheKey = default!;
    private TopicKey _topicKey = default!;
    private CacheEntryBuilder? _sut = null;

    private CacheEntryBuilder Sut => _sut ??= _fixture.Create<CacheEntryBuilder>();

    [Fact]
    public void Entry_for_Null_CacheKey()
    {
        Action act = () => Sut.BuildEntryOptions<string>(CacheKey.Null);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Entry_token_cancel()
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var token = cancellationTokenSource.Token;
        cancellationTokenSource.Cancel();
        Action act = () => Sut.BuildEntryOptions<string>(_cacheKey, token: token);
        act.Should().Throw<OperationCanceledException>();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _cacheKey = _fixture.Create<string>();
        _topicKey = _fixture.Create<string>();
        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        _topicKeyStrategy = _fixture.Create<ITopicKeyStrategy>();
        _cacheKeyStrategy.GetCacheKey<string>(_cacheKey).Returns(_cacheKey);
        _topicKeyStrategy.GetTopicKey<string>().Returns(_topicKey);
        return ValueTask.CompletedTask;
    }
}
