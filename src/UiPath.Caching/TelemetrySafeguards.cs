using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

/// <summary>Telemetry calls that never throw at the caller, so the work around them carries on.</summary>
internal static class TelemetrySafeguards
{
    /// <summary>Invokes each subscriber separately, so one that throws does not cost the rest theirs.</summary>
    public static void TryRaise<THandler>(this THandler? handlers, ICachingTelemetryProvider telemetryProvider, Action<THandler> raise)
        where THandler : Delegate
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                raise((THandler)handler);
            }
            catch (Exception ex)
            {
                // Subscribers re-subscribe from these handlers, so stopping at the first throw detaches the rest.
                telemetryProvider.TryTrackException(ex);
            }
        }
    }

    /// <summary>Records an event, reporting a sink that refuses rather than abandoning what follows.</summary>
    public static bool TryTrackEvent(this ICachingTelemetryProvider telemetryProvider, string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default)
    {
        try
        {
            telemetryProvider.TrackEvent(eventName, properties);
            return true;
        }
        catch (Exception ex)
        {
            telemetryProvider.TryTrackException(ex);
            return false;
        }
    }

    /// <summary>Reports a caught failure; a sink that refuses it leaves nowhere else to put it.</summary>
    public static void TryTrackException(this ICachingTelemetryProvider telemetryProvider, Exception ex)
    {
        try
        {
            telemetryProvider.TrackException(ex);
        }
        catch (Exception)
        {
            // Rethrowing would put it back on the path the caller's catch exists to keep clear, and there is no second sink.
        }
    }
}
