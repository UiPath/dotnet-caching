namespace UiPath.Platform.Caching.Tests.Hybrid;

public class CancelationTokenRedisHashSetCacheTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    [Fact]
    public Task Get() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.GetAsync<string>((Region)_fixture.Create<string>(), token);
        });

    [Fact]
    public Task Get_key() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.GetItemAsync<string>((Region)_fixture.Create<string>(), _fixture.Create<string>(), token);
        });

    [Fact]
    public Task Get_keys() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.GetAsync<string>((Region)_fixture.Create<string>(), _fixture.CreateMany<string>().ToArray(), token);
        });

    [Fact]
    public Task GetOrAdd() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.GetOrAddAsync((Region)_fixture.Create<string>(), () => Task.FromResult(_fixture.Create<IDictionary<string, string?>>()), _fixture.Create<TimeSpan>(), token);
        });

    [Fact]
    public Task Refresh() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.RefreshAsync<string>((Region)_fixture.Create<string>(), _fixture.Create<TimeSpan>(), token);
        });

    [Fact]
    public Task Remove() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.RemoveAsync<string>((Region)_fixture.Create<string>(), token);
        });

    [Fact]
    public Task Remove_key() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.RemoveAsync<string>((Region)_fixture.Create<string>(), _fixture.Create<string>(), token);
        });

    [Fact]
    public Task Set() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.SetAsync((Region)_fixture.Create<string>(), _fixture.Create<IDictionary<string, string?>>(), _fixture.Create<TimeSpan>(), token);
        });

    [Fact]
    public Task ContainsAsync() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.ContainsAsync((Region)_fixture.Create<string>(), token);
        });

    private async Task ValidateCancellationToken(Func<RedisHashSetCache, CancellationToken, Task> act)
    {
        var sut = _fixture.Create<RedisHashSetCache>();
        var cancelSource = new CancellationTokenSource();
        var token = cancelSource.Token;
        cancelSource.Cancel();
        Func<Task> innerAct = () => act(sut, token);
        await innerAct.Should().ThrowAsync<OperationCanceledException>();
    }
}
