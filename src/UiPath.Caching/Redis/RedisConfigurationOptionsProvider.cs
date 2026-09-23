using System.Text;

namespace UiPath.Caching.Redis;

public class RedisConfigurationOptionsProvider(ILoggerFactory loggerFactory, IOptions<RedisConnectionOptions> optionsAccessor) : IRedisConfigurationOptionsProvider
{
    private const double MaxMaintenanceSeconds = 600d;

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
            ApplyMaintenanceRelaxedWindow(supplied);
            ApplyBacklogPolicy(supplied);
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
        ApplyMaintenanceRelaxedWindow(config);

        if (_options.HeartbeatConsistencyChecks.HasValue)
        {
            config.HeartbeatConsistencyChecks = _options.HeartbeatConsistencyChecks.Value;
        }

        if (_options.HeartbeatInterval.HasValue)
        {
            config.HeartbeatInterval = _options.HeartbeatInterval.Value;
        }

        ApplyBacklogPolicy(config);

        return config;
    }

    /// <inheritdoc />
    public void ReapplyDerivedBounds(ConfigurationOptions configuration)
    {
        // ToString lists only assigned keys, so a present key means something set it.
        var rendered = configuration.ToString();
        if (HasKey(rendered, "maintRelaxedTimeout"))
        {
            return;
        }

        // Nothing to derive from; the async timeout follows the sync one when unset.
        if (string.IsNullOrWhiteSpace(_options.ConnectionString) && !HasKey(rendered, "asyncTimeout") && !HasKey(rendered, "syncTimeout"))
        {
            return;
        }

#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
        configuration.MaintenanceRelaxedTimeout = DeriveRelaxedTimeout(configuration.AsyncTimeout);
#pragma warning restore SER010
    }

    // The client renders whole seconds, and a zero window maximum turns relaxation off.
    private static TimeSpan WholeSeconds(TimeSpan value) =>
        TimeSpan.FromSeconds(Math.Clamp(Math.Ceiling(value.TotalSeconds), 1d, MaxMaintenanceSeconds));

    private static TimeSpan DeriveRelaxedTimeout(int asyncTimeoutMilliseconds) =>
        WholeSeconds(TimeSpan.FromMilliseconds(asyncTimeoutMilliseconds * 2L));

    private static bool HasKey(string connectionString, string key) =>
        connectionString.Split(',').Any(part =>
            part.Split('=', 2) is [var name, _] && name.Trim().Equals(key, StringComparison.OrdinalIgnoreCase));

    private void ApplyBacklogPolicy(ConfigurationOptions config)
    {
        // Only an explicit false restores queuing.
        if (_options.FailFastBacklogPolicy is not false)
        {
            config.BacklogPolicy = BacklogPolicy.FailFast;
        }
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

    private void ApplyMaintenanceRelaxedWindow(ConfigurationOptions config)
    {
        // Supplied values only; ReapplyDerivedBounds derives after the configurators.
#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
        if (_options.MaintenanceRelaxedTimeout is { } relaxedTimeout)
        {
            config.MaintenanceRelaxedTimeout = WholeSeconds(relaxedTimeout);
        }

        // Unset, the client derives it from the relaxed timeout.
        if (_options.MaintenanceRelaxedWindowMax is { } windowMax)
        {
            config.MaintenanceRelaxedWindowMax = WholeSeconds(windowMax);
        }
#pragma warning restore SER010
    }
}
