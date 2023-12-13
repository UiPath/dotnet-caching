using UiPath.Platform.Caching;

namespace UiPath.Platform.Caching.Tests;

public class CancelationTokenRedisHashSetCacheTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    [Fact]
    public Task Get() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.GetAsync<string>((CacheKey)_fixture.Create<string>(), null, token);
        });

    [Fact]
    public Task Get_field() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.GetItemAsync<string>((CacheKey)_fixture.Create<string>(), _fixture.Create<string>(), null, token);
        });

    [Fact]
    public Task Get_fields() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.GetAsync<string>((CacheKey)_fixture.Create<string>(), _fixture.CreateMany<string>().ToArray(), null, token);
        });

    [Fact]
    public Task GetOrAdd() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.GetOrAddAsync((CacheKey)_fixture.Create<string>(), () => ValueTask.FromResult(_fixture.Create<IDictionary<string, string?>>()), _fixture.Create<TimeSpan>(), token);
        });

    [Fact]
    public Task Refresh() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.RefreshAsync<string>((CacheKey)_fixture.Create<string>(), _fixture.Create<TimeSpan>(), token);
        });

    [Fact]
    public Task Remove() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.RemoveAsync<string>((CacheKey)_fixture.Create<string>(), token);
        });

    [Fact]
    public Task Set() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.SetAsync((CacheKey)_fixture.Create<string>(), _fixture.Create<IDictionary<string, string?>>(), _fixture.Create<TimeSpan>(), token);
        });

    [Fact]
    public Task ContainsAsync() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.ContainsAsync<string>((CacheKey)_fixture.Create<string>(), token);
        });

    private async Task ValidateCancellationToken(Func<RedisHashCache, CancellationToken, Task> act)
    {
        var sut = _fixture.Create<RedisHashCache>();
        var cancelSource = new CancellationTokenSource();
        var token = cancelSource.Token;
        cancelSource.Cancel();
        Func<Task> innerAct = () => act(sut, token);
        await innerAct.Should().ThrowAsync<OperationCanceledException>();
    }
}
