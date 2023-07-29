using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace UiPath.Platform.Caching.Tests.Redis;

public class RedisConnectionTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private IConnectionMultiplexer _multiplexer = default!;
    private Func<IConnectionMultiplexer> _multiplexerBuilder = default!;
    private ILogger<RedisConnection> _logger = default!;

    [Fact]
    public void NotNullConnection()
    {
        var conection = new RedisConnection(_multiplexerBuilder, _logger);
        conection.Connection.Should().NotBeNull();
        conection.Dispose();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _multiplexer = _fixture.Create<IConnectionMultiplexer>();
        _multiplexerBuilder = () => _multiplexer;
        _logger = _fixture.Create<ILogger<RedisConnection>>();
        return Task.CompletedTask;
    }
}
