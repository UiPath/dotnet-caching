using UiPath.Platform.Caching;

namespace UiPath.Platform.Caching.Tests;

public class CancelationTokenRedisSetCacheTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public Task Add() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.AddAsync((CacheKey)_fixture.Create<string>(), _fixture.Create<string>(), policy: null, token: token);
        });

    [Fact]
    public Task Add_many() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.AddAsync<string>((CacheKey)_fixture.Create<string>(), _fixture.CreateMany<string>().ToArray(), policy: null, token: token);
        });

    [Fact]
    public Task Pop() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.PopAsync<string>((CacheKey)_fixture.Create<string>(), policy: null, token: token);
        });

    [Fact]
    public Task Pop_count() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.PopAsync<string>((CacheKey)_fixture.Create<string>(), count: 5, policy: null, token: token);
        });

    [Fact]
    public Task Members() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.MembersAsync<string>((CacheKey)_fixture.Create<string>(), policy: null, token: token);
        });

    [Fact]
    public Task ContainsItem() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.ContainsItemAsync((CacheKey)_fixture.Create<string>(), _fixture.Create<string>(), token);
        });

    [Fact]
    public Task Count() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.CountAsync<string>((CacheKey)_fixture.Create<string>(), token);
        });

    [Fact]
    public Task RemoveItem() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.RemoveItemAsync((CacheKey)_fixture.Create<string>(), _fixture.Create<string>(), token);
        });

    [Fact]
    public Task RemoveItems() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.RemoveItemsAsync((CacheKey)_fixture.Create<string>(), _fixture.CreateMany<string>().ToArray(), token);
        });

    [Fact]
    public Task Remove() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.RemoveAsync<string>((CacheKey)_fixture.Create<string>(), token);
        });

    [Fact]
    public Task Contains() =>
        ValidateCancellationToken(async (sut, token) =>
        {
            await sut.ContainsAsync<string>((CacheKey)_fixture.Create<string>(), token);
        });

    private async Task ValidateCancellationToken(Func<RedisSetCache, CancellationToken, Task> act)
    {
        var sut = _fixture.Create<RedisSetCache>();
        var cancelSource = new CancellationTokenSource();
        var token = cancelSource.Token;
        cancelSource.Cancel();
        Func<Task> innerAct = () => act(sut, token);
        await innerAct.Should().ThrowAsync<OperationCanceledException>();
    }
}
