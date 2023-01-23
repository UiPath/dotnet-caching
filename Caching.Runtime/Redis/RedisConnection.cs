using StackExchange.Redis.Profiling;

namespace UiPath.Platform.Caching.Redis;

public class RedisConnection : IRedisConnection
{
    private readonly Func<ConfigurationOptions, IConnectionMultiplexer> _multiplexerBuilder;
    private readonly ILogger _logger;
    private readonly Lazy<IConnectionMultiplexer> _connection;

    public RedisConnection(IOptions<RedisConnectionOptions> optionsAccessor, Func<ConfigurationOptions, IConnectionMultiplexer> multiplexerBuilder, ILogger<RedisConnection> logger)
    {
        _multiplexerBuilder = multiplexerBuilder;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var readSettings = (optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor))).Value;
        var configuration = BuildConfiguration(readSettings);
        _connection = new Lazy<IConnectionMultiplexer>(() => Connect(configuration, readSettings.ProfilingSession));
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

    private IConnectionMultiplexer Connect(ConfigurationOptions configuration, Func<ProfilingSession>? profiler)
    {
        _logger.LogDebug($"Connecting to redis");
        var cnn = _multiplexerBuilder(configuration);
        if (profiler != null)
        {
            cnn.RegisterProfiler(profiler);
        }
        return cnn;
    }

    private static ConfigurationOptions BuildConfiguration(RedisConnectionOptions options)
    {
        var config = ConfigurationOptions.Parse(options.ConnectionString);
        config.AbortOnConnectFail = false; // if the connection fails, the multiplexer will silently retry in the background

        if (options.BackOffMilliseconds.HasValue)
        {
            config.ReconnectRetryPolicy = new ExponentialRetry(options.BackOffMilliseconds.Value);
        }

        if (options.HeartbeatInterval.HasValue)
        {
            config.HeartbeatInterval = options.HeartbeatInterval.Value;
        }

        return config;
    }
}
