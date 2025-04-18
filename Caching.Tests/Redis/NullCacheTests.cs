namespace UiPath.Platform.Caching.Tests.Redis;

public class TestDto
{
    public int Id { get; set; }

    public string? Name { get; set; }
}

public class NullCacheTests : IAsyncLifetime
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "Test")]
    [InlineData(2, "Test2")]
    [InlineData(3, "Test3")]
    public async Task GetOrAdd_Works(int id, string? name)
    {
        var cache = new NullCache();
        var result = await cache.GetOrAddAsync<TestDto?>(new CacheKey("test"), _ => Task.FromResult<TestDto?>(new TestDto { Id = id, Name = name }), token: default);
        result.Should().NotBeNull();
        result.Id.Should().Be(id);
        result.Name.Should().Be(name);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "Test")]
    [InlineData(2, "Test2")]
    [InlineData(3, "Test3")]
    public async Task GetOrAdd_TimeSpan_Works(int id, string? name)
    {
        var cache = new NullCache();
        var result = await cache.GetOrAddAsync<TestDto?>(new CacheKey("test"), _ => Task.FromResult<TestDto?>(new TestDto { Id = id, Name = name }), expiration: (TimeSpan?)null, token: default);
        result.Should().NotBeNull();
        result.Id.Should().Be(id);
        result.Name.Should().Be(name);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "Test")]
    [InlineData(2, "Test2")]
    [InlineData(3, "Test3")]
    public async Task GetOrAdd_DateTimeOffset_Works(int id, string? name)
    {
        var cache = new NullCache();
        var result = await cache.GetOrAddAsync<TestDto?>(new CacheKey("test"), _ => Task.FromResult<TestDto?>(new TestDto { Id = id, Name = name }), expiration: (DateTimeOffset?)null, token: default);
        result.Should().NotBeNull();
        result.Id.Should().Be(id);
        result.Name.Should().Be(name);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public Task InitializeAsync() => Task.CompletedTask;
}
