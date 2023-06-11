namespace UiPath.Platform.Caching.Tests;

public class HashCacheEntryBuilderTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    private IKeyResolver _keyResolver = default!;
    private CacheKey _cacheKey = default!;

    private HashCacheEntryBuilder? _sut = null;

    private HashCacheEntryBuilder Sut => _sut ??= _fixture.Create<HashCacheEntryBuilder>();

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
        Action act = () => Sut.BuildEntryOptions<string>(_cacheKey, token);
        act.Should().Throw<OperationCanceledException>();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _cacheKey = _fixture.Create<string>();
        _keyResolver = new KeyResolver(Options.Create(new CacheOptions()));
        _fixture.Inject(_keyResolver);
        return Task.CompletedTask;
    }
}
