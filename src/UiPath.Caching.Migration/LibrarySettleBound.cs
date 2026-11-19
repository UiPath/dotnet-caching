using StackExchange.Redis;

namespace UiPath.Caching.Migration;

/// <summary>The longest a library write can stay in flight: the pipeline timeout of a call plus the longest command timeout the connection can apply.</summary>
internal static class LibrarySettleBound
{
    /// <summary>The longest bound the router supports; a pending window is a timer.</summary>
    internal static readonly TimeSpan Horizon = TimeSpan.FromDays(1);

    /// <summary>The library never relaxes a timeout past this.</summary>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(600);

    /// <param name="pipeline">The library pipeline timeout of one call.</param>
    /// <param name="liveConfiguration">The configuration of the multiplexer that sent the command, or null when none was ever seen.</param>
    public static TimeSpan Compute(TimeSpan pipeline, string? liveConfiguration) =>
        pipeline + (CommandTimeout(liveConfiguration) ?? Ceiling);

    public static string? Join(string? connectionString, string? extraParams)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(extraParams) ? connectionString : $"{connectionString},{extraParams}";
    }

    /// <summary>The command timeout a configuration text names, or null when it cannot be read.</summary>
    public static TimeSpan? CommandTimeout(string? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return null;
        }

        try
        {
            var options = ConfigurationOptions.Parse(configuration);
#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
            var relaxed = options.MaintenanceRelaxedTimeout;
#pragma warning restore SER010
            var longest = Math.Max(options.AsyncTimeout, options.SyncTimeout);
            var bound = TimeSpan.FromMilliseconds(longest);
            return relaxed > bound ? relaxed : bound;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Startup check of the configured connection: a timeout past the horizon is refused instead of shortened.</summary>
    public static void ThrowIfBeyondHorizon(string? configuration)
    {
        if (CommandTimeout(configuration) is { } timeout && timeout > Horizon)
        {
            throw new InvalidOperationException($"The connection's effective command timeout of {timeout} is longer than the {Horizon.TotalHours:0} hours the router can keep a write of unknown outcome protected. Lower asyncTimeout, syncTimeout or the maintenance relaxed timeout.");
        }
    }
}
