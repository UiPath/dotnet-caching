namespace UiPath.Platform.Caching.Redis;

public sealed class RedisConnection : IRedisConnection
{
    private readonly ILogger _logger;
    private readonly Lazy<IConnectionMultiplexer> _connection;

    public RedisConnection(
        Func<IConnectionMultiplexer> multiplexerFactory,
        ILogger<RedisConnection> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connection = new Lazy<IConnectionMultiplexer>(multiplexerFactory);
    }

    public IConnectionMultiplexer Connection => _connection.Value;

    public void Dispose()
    {
        _logger.LogDebug("Dispose redis");
        if (_connection.IsValueCreated)
        {
            _connection.Value.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
