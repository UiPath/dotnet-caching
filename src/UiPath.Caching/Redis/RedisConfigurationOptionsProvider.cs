using System.Text;

namespace UiPath.Caching.Redis;

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
            // With no connection string these options are what a supplied ConnectionFactory is handed, so the
            // mapping still has to run.
            var supplied = new ConfigurationOptions
            {
                LoggerFactory = loggerFactory,
            };
            ApplyMaintenanceNotifications(supplied);
            return supplied;
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

        ApplyMaintenanceNotifications(config);

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

        return config;
    }
    private void ApplyMaintenanceNotifications(ConfigurationOptions config)
    {
        if (_options.MaintenanceNotifications is not { } notifications)
        {
            return;
        }

        // The suppression stops here: SER010 rides on the type, so exposing StackExchange's enum would raise it
        // in every consumer that sets the option.
#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
        config.MaintenanceNotifications = notifications switch
        {
            RedisMaintenanceNotifications.Disabled => MaintenanceNotificationMode.Disabled,
            RedisMaintenanceNotifications.Auto => MaintenanceNotificationMode.Auto,
            RedisMaintenanceNotifications.Required => MaintenanceNotificationMode.Enabled,
            _ => throw new InvalidOperationException($"Unsupported {nameof(RedisMaintenanceNotifications)} value '{notifications}'."),
        };
#pragma warning restore SER010
    }

}
