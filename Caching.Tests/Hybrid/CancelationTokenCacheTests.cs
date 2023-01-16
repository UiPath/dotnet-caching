namespace UiPath.Platform.Caching.Tests.Hybrid;

public class CancelationTokenRedisCacheTests : CancelationTokenCacheTests<RedisCache>
{
}

public class CancelationTokenHybridCacheTests : CancelationTokenCacheTests<HybridCache>
{
    protected override HybridCache CreateSut()
    {
        Fixture.Inject<Func<RedisCacheOptions, IRegionCache>>(options => Fixture.Create<IRegionCache>());
        Fixture.Inject(Options.Create(new HybridCacheOptions()));
        return Fixture.Create<HybridCache>();
    }
}

public abstract class CancelationTokenCacheTests<T> where T : ICache
{
    protected IFixture Fixture { get; } = AutoFixtureCreator.NSubsitute();

    [Fact]
    public Task Get() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.GetAsync<string>(Fixture.Create<string>(), token);
        });


    [Fact]
    public Task GetOrAdd() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.GetOrAddAsync(Fixture.Create<string>(), () => Task.FromResult(Fixture.Create<string?>()), Fixture.Create<TimeSpan>(), token);
        });

    [Fact]
    public Task Refresh() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.RefreshAsync<string>(Fixture.Create<string>(), Fixture.Create<TimeSpan>(), token);
        });

    [Fact]
    public Task Remove() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.RemoveAsync<string>(Fixture.Create<string>(), token);
        });

    [Fact]
    public Task Set() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.SetAsync(Fixture.Create<string>(), Fixture.Create<string>(), Fixture.Create<TimeSpan>(), token);
        });

    [Fact]
    public Task Contains() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.ContainsAsync(Fixture.Create<string>(), token);
        });

    private async Task ValidateCancellationToken(Func<ICache, CancellationToken, Task> act)
    {
        var sut = CreateSut();
        var cancelSource = new CancellationTokenSource();
        var token = cancelSource.Token;
        cancelSource.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => act(sut, token));
    }

    protected virtual T CreateSut() => Fixture.Create<T>();
}
