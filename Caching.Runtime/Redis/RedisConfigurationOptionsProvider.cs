using System.Text;

namespace UiPath.Platform.Caching.Redis;

public class RedisConfigurationOptionsProvider(ILoggerFactory loggerFactory, IOptions<RedisConnectionOptions> optionsAccessor) : IRedisConfigurationOptionsProvider
{
    private readonly RedisConnectionOptions _options = optionsAccessor.Value;

    public ConfigurationOptions GetConfiguration()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            sb.Append(_options.ConnectionString);
            if (!string.IsNullOrWhiteSpace(_options.ConnectionStringExtraParams))
            {
                sb.Append(',');
                sb.Append(_options.ConnectionStringExtraParams);
            }
        }

        if (sb.Length == 0)
        {
            return new ConfigurationOptions
            {
                LoggerFactory = loggerFactory
            };
        }

        var config = ConfigurationOptions.Parse(sb.ToString());
        config.LoggerFactory = loggerFactory;
        config.AbortOnConnectFail = _options.AbortOnConnectFail; 
        config.ChannelPrefix = default;
        if (Version.TryParse(_options.DefaultVersion, out var version))
        {
            config.DefaultVersion = version;
        }
        if (_options.BackOffMilliseconds > 0)
        {
            config.ReconnectRetryPolicy = new ExponentialRetry(_options.BackOffMilliseconds);
        }

        if (_options.HeartbeatConsistencyChecks.HasValue)
        {
            config.HeartbeatConsistencyChecks = _options.HeartbeatConsistencyChecks.Value;
        }

        if (_options.HeartbeatInterval.HasValue)
        {
            config.HeartbeatInterval = _options.HeartbeatInterval.Value;
        }

        if (_options.FailFastBacklogPolicy.GetValueOrDefault())
        {
            config.BacklogPolicy = BacklogPolicy.FailFast;
        }

        if (_options.ThreadPoolSocketManager.GetValueOrDefault())
        {
            config.SocketManager = SocketManager.ThreadPool;
        }

        return config;
    }
}
